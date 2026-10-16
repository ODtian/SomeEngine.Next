using System.Runtime.CompilerServices;
using SomeEngine.Core.Diagnostics;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Systems;

namespace SomeEngine.Animation;

public enum TrackFields : byte { Properties, LayerWeights }

/// <summary>Samples property tracks with the value operations selected by the caller.</summary>
public partial struct TrackSystem<T, TOps>(TrackFields fields = TrackFields.Properties) : ISystem<ImmediateSystemContext>
    where T : unmanaged
    where TOps : struct, IValueOps<T>
{
    private readonly TrackFields _fields = fields;
    private QueryHandle _tracks;
    private QueryHandle _finishedTracks;
    private QueryHandle _fieldSources;
    private readonly bool _evaluateTyped = true;
    private TypedTrackSystem<T, TOps, TweenParameters, TweenState, Tween<T, TOps, Linear>> _linear;
    private TypedTrackSystem<T, TOps, SpringParameters, SpringState<T>, Spring<T, TOps>> _spring;
    private TypedTrackSystem<T, TOps, KeyframeParameters, KeyframeState, Keyframes<T, TOps>> _keys;
    private TypedTrackSystem<T, TOps, FunctionTweenParameters, TweenState, FunctionTween<T, TOps>> _function;

    public TrackSystem() : this(TrackFields.Properties) { }
    internal TrackSystem(TrackFields fields, bool evaluateTyped) : this(fields) => _evaluateTyped = evaluateTyped;

    public void OnCreate(ref ImmediateSystemContext context)
    {
        _fieldSources = context.World.Query(GraphTrackQueries<T>.Sources);
        _tracks = CreateQuery(context.World, enabled: true);
        if (_fields == TrackFields.Properties)
            _finishedTracks = CreateQuery(context.World, enabled: false);
        if (_evaluateTyped)
        {
            _linear = new(_fields); _spring = new(_fields); _keys = new(_fields); _function = new(_fields);
            _linear.OnCreate(ref context); _spring.OnCreate(ref context); _keys.OnCreate(ref context); _function.OnCreate(ref context);
        }
    }

    private static QueryHandle CreateQuery(World world, bool enabled)
    {
        var query = world.QueryDefinition()
            .ReadWrite<Timeline>().WriteBuffer<Track<T>>()
            .OptionalBuffer<Keyframe>(QueryAccess.Read)
            .OptionalBuffer<TimelineStage>(QueryAccess.Read)
            .Optional<GraphInstance>(QueryAccess.Read).OptionalBuffer<GraphMotion>(QueryAccess.Write);
        return world.Query(enabled ? query.Enabled<Timeline>() : query.Disabled<Timeline>());
    }

    public void OnUpdate(ref ImmediateSystemContext context)
    {
        bool weightChannels = _fields == TrackFields.LayerWeights;
        Evaluate(context.World, context.LastSystemVersion, _tracks, weightChannels, enabled: true);
        if (!weightChannels)
            Evaluate(context.World, context.LastSystemVersion, _finishedTracks, weightChannels, enabled: false);
        if (_evaluateTyped)
        {
            _linear.OnUpdate(ref context); _spring.OnUpdate(ref context); _keys.OnUpdate(ref context); _function.OnUpdate(ref context);
        }
    }

    private void Evaluate(World world, uint lastVersion, QueryHandle query, bool weightChannels, bool enabled)
    {
        var evaluation = (World: world, WeightChannels: weightChannels, Enabled: enabled);
        world.ExecuteQuery(query, lastVersion, ref evaluation,
            static (QueryCursor rows, ref (World World, bool WeightChannels, bool Enabled) evaluation) =>
        {
            World world = evaluation.World;
            foreach (QueryChunkView chunk in rows.Chunks)
            {
                bool hasKeyframes = chunk.HasBuffer<Keyframe>();
                bool hasStages = chunk.HasBuffer<TimelineStage>();
                foreach (QueryRow row in chunk.Rows)
                {
                    ReadOnlySpan<Keyframe> keyframes = hasKeyframes ? row.ReadBuffer<Keyframe>().AsSpan() : default;
                    ReadOnlySpan<TimelineStage> stages = hasStages ? row.ReadBuffer<TimelineStage>().AsSpan() : default;
                    ref Timeline timeline = ref row.ReadWrite<Timeline>();
                    DynamicBuffer<Track<T>> tracks = row.Buffer<Track<T>>();
                    int lastStage = FindLastSampledStage(stages, timeline.Stage, timeline.Time, timeline.Paused);
                    SubmitFinishedTracks(world, row.Entity, tracks, lastStage, timeline.Time, timeline.Starts, evaluation.WeightChannels);
                    if (!evaluation.Enabled)
                        continue;
                    float time = timeline.Time;
                    for (int stage = timeline.Stage; stage <= lastStage; stage++)
                    {
                        bool duration = stage < stages.Length && stages[stage].End == StageEnd.Duration;
                        float sampleTime = duration ? Math.Min(time, stages[stage].Duration) : time;
                        bool running = SampleTracks(world, row.Entity, tracks, stage, lastStage, sampleTime,
                            timeline.Starts, timeline.Paused, evaluation.WeightChannels, keyframes);
                        if (stage == lastStage && running && (timeline.Paused || !duration || time < stages[stage].Duration))
                            timeline.Running |= 1;
                        if (stage < lastStage)
                            time -= stages[stage].Duration;
                    }
                    if (row.Has<GraphInstance>() && row.Read<GraphInstance>().Data is GraphData[] graph && graph[0].GroupCount > 0)
                        SampleGraph(world, row.Entity, tracks, row.Buffer<GraphMotion>(), graph,
                            timeline.Starts, timeline.Paused, evaluation.WeightChannels, keyframes);
                }
            }
        });
    }

    private static int FindLastSampledStage(ReadOnlySpan<TimelineStage> stages, int stage, float time, bool paused)
    {
        if (paused)
            return stage;
        while (stage < stages.Length && stages[stage].End == StageEnd.Duration && time >= stages[stage].Duration)
        {
            int next = stage + 1;
            if (next >= stages.Length || stages[next].End == StageEnd.Settled)
                break;
            time -= stages[stage].Duration;
            stage = next;
        }
        return stage;
    }

    private static void SubmitFinishedTracks(World world, Entity animation, DynamicBuffer<Track<T>> tracks,
        int lastStage, float time, uint starts, bool weightChannels)
    {
        if (weightChannels)
            return;
        for (int index = 0; index < tracks.Count; index++)
        {
            Track<T> track = tracks.Read(index);
            if (track.Typed) continue;
            Entity target = track.Target == Entity.Null ? animation : track.Target;
            bool previousRun = track.StartId != starts;
            if (!PropertyAnimation.HoldsLayerValue(world, animation, in track) ||
                !previousRun && track.Stage > lastStage ||
                HasLaterTrack(tracks, index, animation, target, in track,
                    previousRun ? ushort.MaxValue : lastStage, previousRun ? float.PositiveInfinity : time, previousRun) ||
                previousRun && HasCurrentTrack(tracks, animation, target, in track, starts, lastStage, time))
                continue;
            Submit(world, target, animation, (uint)index, in track);
        }
    }

    private static bool HasCurrentTrack(DynamicBuffer<Track<T>> tracks, Entity animation, Entity target,
        in Track<T> track, uint starts, int lastStage, float time)
    {
        for (int index = 0; index < tracks.Count; index++)
        {
            Track<T> current = tracks.Read(index);
            if (current.StartId == starts && current.Stage <= lastStage &&
                (current.Stage < lastStage || current.Start <= time) &&
                current.Channel == track.Channel && current.Layer == track.Layer &&
                (current.Target == Entity.Null ? animation : current.Target) == target)
                return true;
        }
        return false;
    }

    private static bool SampleTracks(World world, Entity animation, DynamicBuffer<Track<T>> tracks,
        int stage, int lastStage, float time, uint starts, bool paused, bool weightChannels,
        ReadOnlySpan<Keyframe> keyframes)
    {
        bool running = false;
        for (int i = 0; i < tracks.Count; i++)
        {
            Track<T> previous = tracks.Read(i);
            if (previous.Typed || previous.GraphMotion != 0 || previous.Finished && !previous.FromGraph || previous.Stage != stage || previous.StartId != starts)
                continue;
            bool weightChannel = previous.Channel.Component == ComponentId<AnimationLayers>.Value;
            if (weightChannel != weightChannels)
                continue;
            Entity target = previous.Target == Entity.Null ? animation : previous.Target;
            if (previous.Target != Entity.Null && !world.IsAlive(target))
            {
                tracks[i].Finished = true;
                continue;
            }
            if (time < previous.Start || paused && previous.CaptureStart)
            {
                running = true;
                continue;
            }
            if (paused)
            {
                running = true;
                if (world.HasBuffer<Contribution<T>>(target))
                    Append(world, target, animation, (uint)i, in previous);
                continue;
            }
            ref Track<T> track = ref tracks[i];
            if (!track.Finished)
                SampleValue(world, animation, target, tracks, i, ref track, stage, time, starts, keyframes);
            bool finished = track.Finished;
            if (!HasLaterTrack(tracks, i, animation, target, in track, lastStage, time))
            {
                Submit(world, target, animation, (uint)i, in track);
            }
            running |= !finished;
        }
        return running;
    }

    private static void SampleValue(World world, Entity animation, Entity target, DynamicBuffer<Track<T>> tracks,
        int index, ref Track<T> track, int stage, float time, uint starts, ReadOnlySpan<Keyframe> keyframes)
    {
        if (track.CaptureStart)
        {
            T current = track.Channel.Read(world, target);
            if (track.Channel.Component != ComponentId<AnimationLayers>.Value && world.Has<AnimationLayers>(target) &&
                world.Read<AnimationLayers>(target).Items[track.Layer].Mode == LayerMode.Additive)
                PropertyAnimation.ReadBase(world, target, track.Channel, ref current);
            for (int earlier = 0; earlier < index; earlier++)
            {
                Track<T> value = tracks.Read(earlier);
                Entity valueTarget = value.Target == Entity.Null ? animation : value.Target;
                int valueStage = value.GraphMotion == 0 ? value.Stage : value.MotionStage;
                if (valueTarget == target && value.GraphMotion == track.GraphMotion && value.Channel == track.Channel && value.Layer == track.Layer &&
                    value.StartId == starts && (valueStage < stage || valueStage == stage && value.Start + value.Duration <= track.Start))
                    current = value.Current;
            }
            track.From = track.Current = current;
            track.CaptureStart = false;
        }
        if (track.Endpoints == TrackEndpoints.Displacement)
        {
            track.Displacement = track.To;
            T basis = track.From;
            track.From = track.Current = TOps.Add(in basis, in track.To);
            track.To = basis;
            track.Endpoints = TrackEndpoints.DisplacementValues;
        }
        bool finished;
        if (track.Curve.Kind == CurveKind.Spring)
        {
            finished = track.Curve.Step<T, TOps>(time - track.SampleTime, in track.To, ref track.Current, ref track.Velocity);
        }
        else
        {
            float progress = track.Duration == 0 ? 1 : Math.Clamp((time - track.Start) / track.Duration, 0, 1);
            track.Current = progress == 1 ? track.To : TOps.Lerp(in track.From, in track.To, track.Curve.Sample(progress, keyframes));
            finished = progress == 1;
        }
        track.SampleTime = time;
        track.Finished = finished;
    }

    private static bool HasLaterTrack(DynamicBuffer<Track<T>> tracks, int index, Entity animation, Entity target,
        in Track<T> track, int lastStage, float time, bool finishedValuesOnly = false)
    {
        for (int i = 0; i < tracks.Count; i++)
        {
            Track<T> later = tracks.Read(i);
            if (finishedValuesOnly && !later.Finished || later.GraphMotion != 0 ||
                later.StartId != track.StartId || later.Stage < track.Stage || later.Stage > lastStage ||
                later.Channel != track.Channel || later.Layer != track.Layer)
                continue;
            if (later.Stage == track.Stage)
            {
                if (!track.FromGraph || !later.FromGraph || later.Start > time)
                    continue;
                bool after = later.Start > track.Start;
                bool replaces = later.Start == track.Start && later.Endpoints == TrackEndpoints.Values &&
                    track.Endpoints == TrackEndpoints.Values && i > index;
                if (!after && !replaces)
                    continue;
            }
            Entity laterTarget = later.Target == Entity.Null ? animation : later.Target;
            if (laterTarget == target)
                return true;
        }
        return false;
    }

    private static void Submit(World world, Entity target, Entity animation, uint output, in Track<T> track)
    {
        if (world.HasBuffer<Contribution<T>>(target))
            Append(world, target, animation, output, in track);
        else
            PropertyAnimation.WriteSingle<T, TOps>(world, target, track.Channel, track.Layer, ReadValue(world, target, in track));
    }

    private static void Append(World world, Entity target, Entity source, uint output, in Track<T> track)
    {
        var contribution = new Contribution<T>
        {
            Channel = track.Channel, Layer = track.Layer, Value = ReadValue(world, target, in track),
            Source = source, Output = output,
        };
        world.ExecuteBufferWrite<Contribution<T>, Contribution<T>>(target, ref contribution,
            static (DynamicBuffer<Contribution<T>> values, ref Contribution<T> value) => values.Add(value));
    }

    private static T ReadValue(World world, Entity target, in Track<T> track)
    {
        T value = track.Current;
        if (track.Endpoints == TrackEndpoints.Values || track.Channel.Component == ComponentId<AnimationLayers>.Value ||
            !world.Has<AnimationLayers>(target))
            return value;
        AnimationLayers layers = world.Read<AnimationLayers>(target);
        DevelopmentCheck.Require(layers.Count <= 8 && track.Layer < layers.Count,
            "A property track requires a defined animation layer.");
        if (layers.Items[track.Layer].Mode != LayerMode.Additive)
            return value;
        T reference = track.Endpoints == TrackEndpoints.Displacement ? track.From : track.To;
        T basis = reference;
        PropertyAnimation.ReadBase(world, target, track.Channel, ref basis);
        T displacement = TOps.Subtract(in value, in reference);
        return TOps.Add(in basis, in displacement);
    }

    public void OnDestroy(ref ImmediateSystemContext context)
    {
        context.World.ReleaseQuery(_tracks);
        if (_fields == TrackFields.Properties)
            context.World.ReleaseQuery(_finishedTracks);
        context.World.ReleaseQuery(_fieldSources);
        if (_evaluateTyped)
        {
            _linear.OnDestroy(ref context); _spring.OnDestroy(ref context); _keys.OnDestroy(ref context); _function.OnDestroy(ref context);
        }
    }
}
