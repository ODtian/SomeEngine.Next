using SomeEngine.Core.Diagnostics;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Systems;

namespace SomeEngine.Animation;

public enum TrackFields : byte { Properties, LayerWeights }

/// <summary>Samples property tracks with the value operations selected by the caller.</summary>
public struct TrackSystem<T, TOps>(TrackFields fields = TrackFields.Properties) : ISystem<ImmediateSystemContext>
    where T : unmanaged
    where TOps : struct, IValueOps<T>
{
    private readonly TrackFields _fields = fields;
    private QueryHandle _tracks;

    public void OnCreate(ref ImmediateSystemContext context) =>
        _tracks = context.World.Query(context.World.QueryDefinition()
            .ReadWrite<Timeline>().WriteBuffer<Track<T>>()
            .OptionalBuffer<Keyframe>(QueryAccess.Read)
            .OptionalBuffer<TimelineStage>(QueryAccess.Read));

    public void OnUpdate(ref ImmediateSystemContext context) =>
        Evaluate(context.World, context.LastSystemVersion, _fields == TrackFields.LayerWeights);

    private void Evaluate(World world, uint lastVersion, bool weightChannels)
    {
        var evaluation = (World: world, WeightChannels: weightChannels);
        world.ExecuteQuery(_tracks, lastVersion, ref evaluation,
            static (QueryCursor rows, ref (World World, bool WeightChannels) evaluation) =>
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
                    SubmitFinishedTracks(world, row.Entity, tracks, lastStage, timeline.Starts, evaluation.WeightChannels);
                    if (!world.IsEnabled<Timeline>(row.Entity))
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
        int lastStage, uint starts, bool weightChannels)
    {
        if (weightChannels)
            return;
        for (int index = 0; index < tracks.Count; index++)
        {
            Track<T> track = tracks.Read(index);
            Entity target = track.Target == Entity.Null ? animation : track.Target;
            if (!track.Finished || track.Endpoints != TrackEndpoints.Values || track.StartId != starts ||
                track.Stage > lastStage || track.Channel.Component == ComponentId<AnimationLayers>.Value ||
                !world.IsAlive(target) || !world.Has<AnimationLayers>(target) ||
                HasLaterTrack(tracks, animation, target, in track, lastStage))
                continue;
            Submit(world, target, animation, (uint)index, in track);
        }
    }

    private static bool SampleTracks(World world, Entity animation, DynamicBuffer<Track<T>> tracks,
        int stage, int lastStage, float time, uint starts, bool paused, bool weightChannels,
        ReadOnlySpan<Keyframe> keyframes)
    {
        bool running = false;
        for (int i = 0; i < tracks.Count; i++)
        {
            Track<T> previous = tracks.Read(i);
            if (previous.Finished || previous.Stage != stage || previous.StartId != starts)
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
            if (paused)
            {
                running = true;
                if (world.HasBuffer<Contribution<T>>(target))
                    Append(world, target, animation, (uint)i, in previous);
                continue;
            }
            ref Track<T> track = ref tracks[i];
            if (track.Endpoints == TrackEndpoints.Displacement)
            {
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
            if (!HasLaterTrack(tracks, animation, target, in track, lastStage))
            {
                Submit(world, target, animation, (uint)i, in track);
            }
            running |= !finished;
        }
        return running;
    }

    private static bool HasLaterTrack(DynamicBuffer<Track<T>> tracks, Entity animation, Entity target, in Track<T> track, int lastStage)
    {
        if (track.Stage == lastStage)
            return false;
        for (int i = 0; i < tracks.Count; i++)
        {
            Track<T> later = tracks.Read(i);
            if (later.StartId != track.StartId || later.Stage <= track.Stage || later.Stage > lastStage ||
                later.Channel != track.Channel || later.Layer != track.Layer)
                continue;
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

    public void OnDestroy(ref ImmediateSystemContext context) => context.World.ReleaseQuery(_tracks);
}
