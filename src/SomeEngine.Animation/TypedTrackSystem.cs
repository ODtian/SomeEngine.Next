using System.Buffers;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Systems;

namespace SomeEngine.Animation;

/// <summary>Evaluates one complete concrete algorithm in batches over its typed ECS storage.</summary>
public struct TypedTrackSystem<T, TOps, TParameters, TState, TEvaluator>(TrackFields fields = TrackFields.Properties) : ISystem<ImmediateSystemContext>
    where T : unmanaged
    where TOps : struct, IValueOps<T>
    where TParameters : struct
    where TState : struct
    where TEvaluator : struct, IMotion<T, TOps, TParameters, TState>
{
    private readonly TrackFields _fields = fields;
    private QueryHandle _tracks;
    private FieldWrite<T>[] _writes = null!;

    public void OnCreate(ref ImmediateSystemContext context)
    {
        _writes = ArrayPool<FieldWrite<T>>.Shared.Rent(64);
        _tracks = context.World.Query(context.World.QueryDefinition().ReadWrite<Timeline>().WriteBuffer<Track<T>>()
            .WriteBuffer<MotionInstance<T, TOps, TParameters, TState, TEvaluator>>()
            .Optional<GraphMotionParameters<T, TOps, TParameters, TState, TEvaluator>>(QueryAccess.Read)
            .Optional<GraphInstance>(QueryAccess.Read).OptionalBuffer<GraphMotion>(QueryAccess.ReadWrite)
            .OptionalBuffer<PropertySource<T>>(QueryAccess.ReadWrite).Optional<AnimationLayers>(QueryAccess.Read)
            .OptionalBuffer<BaseValue<T>>(QueryAccess.Read).OptionalBuffer<LayerChannel>(QueryAccess.Read)
            .OptionalBuffer<Contribution<T>>(QueryAccess.ReadWrite)
            .OptionalBuffer<TimelineStage>(QueryAccess.Read));
    }

    public unsafe void OnUpdate(ref ImmediateSystemContext context)
    {
        var evaluation = (World: context.World, Weights: _fields == TrackFields.LayerWeights, Writes: _writes, Count: 0);
        context.World.ExecuteQuery(_tracks, context.LastSystemVersion, ref evaluation, &Evaluate);
        _writes = evaluation.Writes;
        FieldWrites<T>.Flush(context.World, _writes, ref evaluation.Count);
    }

    private static void Evaluate(QueryCursor rows, ref (World World, bool Weights, FieldWrite<T>[] Writes, int Count) evaluation)
    {
        World world = evaluation.World;
        int type = MotionStorage<T, TOps, TParameters, TState, TEvaluator>.Type;
        int writeCount = 0;
        foreach (QueryChunkView chunk in rows.Chunks)
        {
            bool hasStages = chunk.HasBuffer<TimelineStage>();
            bool hasMotions = chunk.HasBuffer<GraphMotion>();
            foreach (QueryRow row in chunk.Rows)
            {
                DynamicBuffer<Track<T>> tracks = row.Buffer<Track<T>>();
                Span<MotionData<T, TParameters, TState>> data = MotionStorage<T, TOps, TParameters, TState, TEvaluator>.Data(
                    row.Buffer<MotionInstance<T, TOps, TParameters, TState, TEvaluator>>());
                for (int index = 0; index < data.Length; index++)
                    data[index].Active = false;
                ref Timeline timeline = ref row.ReadWrite<Timeline>();
                bool enabled = world.IsEnabled<Timeline>(row.Entity);
                ReadOnlySpan<TimelineStage> stages = hasStages ? row.ReadBuffer<TimelineStage>().AsSpan() : default;
                DynamicBuffer<GraphMotion> motions = hasMotions ? row.Buffer<GraphMotion>() : default;
                GraphData[]? graph = row.Has<GraphInstance>() ? row.Read<GraphInstance>().Data : null;
                ReadOnlySpan<TParameters> parameters = row.Has<GraphMotionParameters<T, TOps, TParameters, TState, TEvaluator>>()
                    ? row.Read<GraphMotionParameters<T, TOps, TParameters, TState, TEvaluator>>().Values : default;
                int lastStage = GraphTimes.LastStage(stages, timeline.Stage, timeline.Time, timeline.Paused);
                bool evaluate = false;
                for (int index = 0; index < tracks.Count; index++)
                {
                    Track<T> previous = tracks.Read(index);
                    if (!Selected(in previous, type, evaluation.Weights))
                        continue;
                    if (previous.Target != Entity.Null && !world.IsAlive(previous.Target))
                    {
                        ref Track<T> removed = ref tracks[index];
                        if (removed.MotionInitialized)
                            TEvaluator.Release(ref data[removed.MotionIndex].State);
                        removed.MotionInitialized = false;
                        removed.SourceActive = false;
                        removed.Finished = true;
                        continue;
                    }
                    if (previous.Finished && previous.GraphMotion == 0 || !enabled || timeline.Paused)
                    {
                        if (enabled && !previous.Finished)
                            timeline.Submit(true);
                        if (!previous.FromGraph && (enabled || previous.Finished && PropertyAnimation.HoldsLayerValue(world, row.Entity, in previous)))
                            Submit(world, row.Entity, index, in previous, ref evaluation.Writes, ref writeCount);
                        continue;
                    }
                    ref Track<T> track = ref tracks[index];
                    float time;
                    if (track.GraphMotion == 0)
                    {
                        if (track.Stage < timeline.Stage || track.Stage > lastStage)
                            continue;
                        time = timeline.Time;
                        for (int stage = timeline.Stage; stage < track.Stage; stage++)
                            time -= stages[stage].Duration;
                        if (track.Stage < lastStage || track.Stage < stages.Length && stages[track.Stage].End == StageEnd.Duration)
                            time = Math.Min(time, stages[track.Stage].Duration);
                    }
                    else
                    {
                        ref GraphMotion motion = ref motions[track.GraphMotion - 1];
                        if (motion.Weight <= 0)
                            continue;
                        if (track.MotionStartId != motion.Starts)
                        {
                            if (track.MotionInitialized)
                            {
                                TEvaluator.Release(ref data[track.MotionIndex].State);
                                track.MotionInitialized = false;
                            }
                            track.MotionStartId = motion.Starts;
                            track.CaptureStart = true;
                            track.Finished = false;
                            track.SampleTime = track.Start;
                            if (track.Endpoints == TrackEndpoints.DisplacementValues)
                            {
                                track.Endpoints = TrackEndpoints.Displacement;
                                track.To = track.Displacement;
                            }
                        }
                        GraphMotionData source = graph![0].Motions![track.GraphMotion - 1];
                        var sample = GraphTimes.Project(source.Stages.AsSpan(0, source.StageCount), motion.Stage, motion.Time);
                        if (track.Finished || track.MotionStage < motion.Stage || track.MotionStage > sample.Stage)
                            continue;
                        time = motion.Time;
                        for (int stage = motion.Stage; stage < track.MotionStage; stage++)
                            time -= source.Stages![stage].Duration;
                        if (track.MotionStage < sample.Stage)
                            time = source.Stages![track.MotionStage].Duration;
                    }
                    if (time < track.Start)
                    {
                        if (track.GraphMotion == 0)
                            timeline.Submit(true);
                        else
                            motions[track.GraphMotion - 1].Running = true;
                        continue;
                    }
                    Entity target = track.Target == Entity.Null ? row.Entity : track.Target;
                    if (track.CaptureStart)
                        GraphTrackValues<T, TOps>.Capture(world, row.Entity, target, tracks, index, ref track);
                    ref MotionData<T, TParameters, TState> motionData = ref data[track.MotionIndex];
                    motionData.Values = new(track.From, track.To, track.Current);
                    if (!track.MotionInitialized)
                    {
                        ref readonly TParameters definition = ref (parameters.IsEmpty ? ref motionData.Parameters : ref parameters[track.MotionIndex]);
                        TEvaluator.Initialize(in definition, in motionData.Values, ref motionData.State);
                        track.MotionInitialized = true;
                    }
                    motionData.Time = new(time - track.Start, Math.Max(0, time - track.SampleTime));
                    motionData.Active = true;
                    evaluate = true;
                }
                if (!evaluate)
                    continue;
                var batch = new MotionBatch<T, TParameters, TState>(data, parameters);
                TEvaluator.EvaluateBatch<TEvaluator>(ref batch);
                for (int index = 0; index < tracks.Count; index++)
                {
                    ref Track<T> track = ref tracks[index];
                    if (!Selected(in track, type, evaluation.Weights) || !data[track.MotionIndex].Active)
                        continue;
                    ref MotionData<T, TParameters, TState> motion = ref data[track.MotionIndex];
                    track.Current = motion.Sample.Value;
                    track.Finished = motion.Sample.Finished;
                    track.SampleTime = motion.Time.Time + track.Start;
                    motion.Active = false;
                    if (!track.FromGraph)
                        Submit(world, row.Entity, index, in track, ref evaluation.Writes, ref writeCount);
                    if (!track.Finished)
                    {
                        if (track.GraphMotion != 0)
                            motions[track.GraphMotion - 1].Running = true;
                        else if (track.Stage == lastStage &&
                            (track.Stage >= stages.Length || stages[track.Stage].End != StageEnd.Duration || timeline.Time < stages[track.Stage].Duration))
                            timeline.Submit(true);
                    }
                }
            }
        }
        evaluation.Count = writeCount;
    }

    private static bool Selected(in Track<T> track, int type, bool weights) =>
        track.MotionType == type && track.SourceActive &&
        (track.Channel.Component == ComponentId<AnimationLayers>.Value) == weights;

    private static void Submit(World world, Entity animation, int index, in Track<T> track,
        ref FieldWrite<T>[] writes, ref int count)
    {
        if (PropertyAnimation.TrySubmitTyped<T, TOps>(world, animation, (uint)index, in track, out FieldWrite<T> write))
            FieldWrites<T>.Add(ref writes, ref count, write.Target, write.Channel, write.Value);
    }

    public void OnDestroy(ref ImmediateSystemContext context)
    {
        context.World.ReleaseQuery(_tracks);
        ArrayPool<FieldWrite<T>>.Shared.Return(_writes);
    }
}

internal static class GraphTrackValues<T, TOps>
    where T : unmanaged
    where TOps : struct, IValueOps<T>
{
    internal static void Capture(World world, Entity animation, Entity target, DynamicBuffer<Track<T>> tracks, int index, ref Track<T> track)
    {
        T current = track.Channel.Read(world, target);
        if (track.Channel.Component != ComponentId<AnimationLayers>.Value && world.Has<AnimationLayers>(target) &&
            world.Read<AnimationLayers>(target).Items[track.Layer].Mode == LayerMode.Additive)
            PropertyAnimation.ReadBase(world, target, track.Channel, ref current);
        int stage = track.GraphMotion == 0 ? track.Stage : track.MotionStage;
        for (int earlier = 0; earlier < index; earlier++)
        {
            Track<T> previous = tracks.Read(earlier);
            Entity previousTarget = previous.Target == Entity.Null ? animation : previous.Target;
            int previousStage = previous.GraphMotion == 0 ? previous.Stage : previous.MotionStage;
            if (previousTarget != target || previous.GraphMotion != track.GraphMotion || previous.Channel != track.Channel ||
                previous.Layer != track.Layer || previous.StartId != track.StartId ||
                !(previousStage < stage || previousStage == stage && previous.Start + previous.Duration <= track.Start))
                continue;
            current = previous.Finished ? previous.Current : previous.Endpoints == TrackEndpoints.Values ? previous.To :
                previous.Endpoints == TrackEndpoints.DisplacementValues ? previous.To : current;
        }
        track.From = track.Current = current;
        track.CaptureStart = false;
        if (track.Endpoints == TrackEndpoints.Displacement)
        {
            track.Displacement = track.To;
            track.From = track.Current = TOps.Add(in current, in track.To);
            track.To = current;
            track.Endpoints = TrackEndpoints.DisplacementValues;
        }
    }
}
