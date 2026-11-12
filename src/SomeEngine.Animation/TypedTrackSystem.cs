using System.Buffers;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;

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
    private Query _tracks;
    private Query _finishedTracks;
    private FieldWrite<T>[] _writes = null!;
    private GraphCapture<T>[] _captures = null!;
    private MotionRanges _ranges = null!;

    public void OnCreate(ref ImmediateSystemContext context)
    {
        _writes = ArrayPool<FieldWrite<T>>.Shared.Rent(64);
        _captures = ArrayPool<GraphCapture<T>>.Shared.Rent(64);
        _ranges = new();
        _tracks = CreateQuery(context.World, enabled: true);
        _finishedTracks = CreateQuery(context.World, enabled: false);
    }

    private static Query CreateQuery(World world, bool enabled)
    {
        var query = new QueryDescription().WithStored<Timeline>().WithStored<Buffer<Track<T>>>()
            .WithStored<Buffer<MotionInstance<T, TOps, TParameters, TState, TEvaluator>>>()
            .Optional<GraphMotionParameters<T, TOps, TParameters, TState, TEvaluator>>()
            .Optional<GraphInstance>().Optional<Buffer<GraphMotion>>()
            .Optional<Buffer<PropertySource<T>>>().Optional<AnimationLayers>()
            .Optional<Buffer<BaseValue<T>>>().Optional<Buffer<LayerChannel>>()
            .Optional<Buffer<Contribution<T>>>()
            .Optional<Buffer<DirectBoundary>>()
            .Optional<Buffer<TimelineStage>>();
        return world.Query(enabled ? query.With<Timeline>() : query.WithDisabled<Timeline>());
    }

    public unsafe void OnUpdate(ref ImmediateSystemContext context)
    {
        if (!(_tracks.Count != 0) && !(_finishedTracks.Count != 0)) return;
        var evaluation = (World: context.World, Weights: _fields == TrackFields.LayerWeights, Writes: _writes, Count: 0,
            Captures: _captures, CaptureCount: 0, CaptureOnly: false, Enabled: true, Ranges: _ranges);
        {
            using QueryScope read = _tracks.Since(context.LastSystemVersion).Open();
            Evaluate(in read, ref evaluation);
        }
        evaluation.Enabled = false;
        {
            using QueryScope read = _finishedTracks.Since(context.LastSystemVersion).Open();
            Evaluate(in read, ref evaluation);
        }
        if (evaluation.CaptureCount > 0)
        {
            for (int index = 0; index < evaluation.CaptureCount; index++)
            {
                GraphCapture<T> capture = evaluation.Captures[index];
                var value = (Index: capture.Track, Value: capture.Channel.Read(context.World, capture.Target));
                {
                    using var tracks = context.World.GetBuffer<Track<T>>(capture.Animation);
                    tracks[value.Index].Current = value.Value;
                }
            }
            evaluation.CaptureOnly = true;
            evaluation.Enabled = true;
            {
                using QueryScope read = _tracks.Since(context.LastSystemVersion).Open();
                Evaluate(in read, ref evaluation);
            }
            evaluation.Enabled = false;
            {
                using QueryScope read = _finishedTracks.Since(context.LastSystemVersion).Open();
                Evaluate(in read, ref evaluation);
            }
        }
        _writes = evaluation.Writes;
        _captures = evaluation.Captures;
        FieldWrites<T>.Flush(context.World, _writes, ref evaluation.Count);
    }

    private static void Evaluate(scoped in QueryScope rows, ref (World World, bool Weights, FieldWrite<T>[] Writes, int Count,
        GraphCapture<T>[] Captures, int CaptureCount, bool CaptureOnly, bool Enabled, MotionRanges Ranges) evaluation)
    {
        World world = evaluation.World;
        bool enabled = evaluation.Enabled;
        int type = MotionStorage<T, TOps, TParameters, TState, TEvaluator>.Type;
        int writeCount = evaluation.Count;
        MotionRanges ranges = evaluation.Ranges;
        ranges.Count = 0;
        foreach (QueryChunk chunk in rows)
        {
            bool hasStages = chunk.Has<Buffer<TimelineStage>>();
            bool hasMotions = chunk.Has<Buffer<GraphMotion>>();
            bool inlineDirect = chunk.Has<Buffer<PropertySource<T>>>() && !chunk.Has<AnimationLayers>() &&
                !chunk.Has<Buffer<Contribution<T>>>();
            var values0 = chunk.Has<Timeline>() ? chunk.Write<Timeline>() : default;
            chunk.TryRead<GraphInstance>(out var values1);
            chunk.TryRead<GraphMotionParameters<T, TOps, TParameters, TState, TEvaluator>>(out var values2);
            var buffers0 = chunk.Has<Buffer<MotionInstance<T, TOps, TParameters, TState, TEvaluator>>>() ? chunk.GetBuffers<MotionInstance<T, TOps, TParameters, TState, TEvaluator>>() : default;
            var buffers1 = chunk.Has<Buffer<Track<T>>>() ? chunk.GetBuffers<Track<T>>() : default;
            var buffers2 = chunk.Has<Buffer<PropertySource<T>>>() ? chunk.GetBuffers<PropertySource<T>>() : default;
            var buffers3 = chunk.Has<Buffer<GraphMotion>>() ? chunk.GetBuffers<GraphMotion>() : default;
            for (int rowIndex = 0; rowIndex < chunk.Count; rowIndex++)
            {
                DynamicBuffer<MotionInstance<T, TOps, TParameters, TState, TEvaluator>> instances =
                    buffers0[rowIndex];
                if (instances.Count == 0)
                    continue;
                DynamicBuffer<Track<T>> tracks = buffers1[rowIndex];
                ReadOnlySpan<Track<T>> previousTracks = tracks.AsSpan();
                int firstSelected = 0;
                for (; firstSelected < previousTracks.Length; firstSelected++)
                {
                    ref readonly Track<T> candidate = ref previousTracks[firstSelected];
                    if (Selected(in candidate, type, evaluation.Weights) &&
                        (!evaluation.CaptureOnly || candidate.CaptureStart))
                        break;
                }
                if (firstSelected == previousTracks.Length)
                    continue;
                DynamicBuffer<PropertySource<T>> sources = inlineDirect ? buffers2[rowIndex] : default;
                Span<MotionData<T, TParameters, TState>> data = MotionStorage<T, TOps, TParameters, TState, TEvaluator>.Data(instances);
                for (int index = 0; index < data.Length; index++)
                    data[index].Active = false;
                ref Timeline timeline = ref values0[rowIndex];
                ReadOnlySpan<TimelineStage> stages = hasStages ? chunk.ReadBuffer<TimelineStage>(rowIndex).AsSpan() : default;
                DynamicBuffer<GraphMotion> motions = hasMotions ? buffers3[rowIndex] : default;
                GraphData[]? graph = chunk.Has<GraphInstance>(rowIndex) ? values1[rowIndex].Data : null;
                ReadOnlySpan<TParameters> parameters = chunk.Has<GraphMotionParameters<T, TOps, TParameters, TState, TEvaluator>>(rowIndex)
                    ? values2[rowIndex].Values : default;
                int lastStage = GraphTimes.LastStage(stages, timeline.Stage, timeline.Time, timeline.Paused);
                bool evaluate = false;
                Span<Track<T>> writableTracks = default;
                for (int index = firstSelected; index < previousTracks.Length; index++)
                {
                    ref readonly Track<T> previous = ref previousTracks[index];
                    if (!Selected(in previous, type, evaluation.Weights))
                        continue;
                    if (evaluation.CaptureOnly && !previous.CaptureStart)
                        continue;
                    if (previous.Target != Entity.Null && !world.IsAlive(previous.Target))
                    {
                        ref Track<T> removed = ref tracks[index];
                        if (removed.MotionInitialized)
                            TEvaluator.Release(ref data[removed.MotionIndex].State);
                        removed.MotionInitialized = false;
                        removed.SourceActive = false;
                        removed.Finished = true;
                        if (!removed.FromGraph)
                            PropertyAnimation.RemoveDirectBoundary<T>(world, chunk.Entities[rowIndex], index, in timeline);
                        continue;
                    }
                    if (previous.Finished && previous.GraphMotion == 0 || !enabled || timeline.Paused)
                    {
                        if (previous.Finished && !previous.FromGraph)
                            PropertyAnimation.FinishSource(world, chunk.Entities[rowIndex], in previous);
                        if (enabled && !previous.Finished)
                            timeline.Submit(true);
                        if (!previous.FromGraph && (enabled && !previous.Finished && previous.Sampled ||
                            previous.Finished && PropertyAnimation.HoldsLayerValue(world, chunk.Entities[rowIndex], in previous)) &&
                            !(previous.Stage < lastStage && TrackSystem<T, TOps>.HasLaterTrack(tracks, index, chunk.Entities[rowIndex],
                                previous.Target == Entity.Null ? chunk.Entities[rowIndex] : previous.Target, in previous, lastStage, timeline.Time)))
                            Submit(world, chunk.Entities[rowIndex], index, in previous, sources, inlineDirect, ref evaluation.Writes, ref writeCount);
                        continue;
                    }
                    if (writableTracks.IsEmpty)
                    {
                        writableTracks = tracks.AsSpan();
                        previousTracks = writableTracks;
                    }
                    ref Track<T> track = ref writableTracks[index];
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
                            track.TimeOffset = 0;
                            track.TimeScale = 1;
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
                    Entity target = track.Target == Entity.Null ? chunk.Entities[rowIndex] : track.Target;
                    if (track.CaptureStart)
                    {
                        if (!evaluation.CaptureOnly)
                        {
                            if (evaluation.CaptureCount == evaluation.Captures.Length)
                            {
                                GraphCapture<T>[] previousCaptures = evaluation.Captures;
                                evaluation.Captures = ArrayPool<GraphCapture<T>>.Shared.Rent(previousCaptures.Length * 2);
                                previousCaptures.CopyTo(evaluation.Captures, 0);
                                ArrayPool<GraphCapture<T>>.Shared.Return(previousCaptures);
                            }
                            evaluation.Captures[evaluation.CaptureCount++] = new(chunk.Entities[rowIndex], target, track.Channel, index);
                            continue;
                        }
                        GraphTrackValues<T, TOps>.Capture(world, chunk.Entities[rowIndex], target, tracks, index, ref track);
                    }
                    ref MotionData<T, TParameters, TState> motionData = ref data[track.MotionIndex];
                    motionData.Values = new(track.From, track.To, track.Current);
                    if (!track.MotionInitialized)
                    {
                        ref readonly TParameters definition = ref (parameters.IsEmpty ? ref motionData.Parameters : ref parameters[track.MotionIndex]);
                        TEvaluator.Initialize(in definition, in motionData.Values, ref motionData.State);
                        track.MotionInitialized = true;
                    }
                    float scale = track.TimeScale == 0 ? 1 : track.TimeScale;
                    motionData.Time = new((time - track.Start - track.TimeOffset) * scale,
                        Math.Max(0, time - track.SampleTime) * scale);
                    motionData.Active = true;
                    evaluate = true;
                }
                if (!evaluate)
                    continue;
                MotionRange range = ranges.Add(chunk.Entities[rowIndex], data.Length);
                data.CopyTo(range.Data);
                range.Parameters = chunk.Has<GraphMotionParameters<T, TOps, TParameters, TState, TEvaluator>>(rowIndex)
                    ? values2[rowIndex].Values : null;

            }
        }
        if (ranges.Count == 0)
        {
            evaluation.Count = writeCount;
            return;
        }
        try
        {
            var work = new EvaluateRanges(ranges);
            if (WorldJobs.TryGetScheduler(world, out Scheduler? scheduler))
                scheduler.ExecuteParallel(work, ranges.Count);
            else
                for (int index = 0; index < ranges.Count; index++)
                    work.Execute(index);
            int rangeIndex = 0;
            foreach (QueryChunk chunk in rows)
            {
                bool hasStages = chunk.Has<Buffer<TimelineStage>>();
                bool hasMotions = chunk.Has<Buffer<GraphMotion>>();
                bool inlineDirect = chunk.Has<Buffer<PropertySource<T>>>() && !chunk.Has<AnimationLayers>() &&
                    !chunk.Has<Buffer<Contribution<T>>>();
                var values0 = chunk.Has<Timeline>() ? chunk.Write<Timeline>() : default;
                var buffers0 = chunk.Has<Buffer<MotionInstance<T, TOps, TParameters, TState, TEvaluator>>>() ? chunk.GetBuffers<MotionInstance<T, TOps, TParameters, TState, TEvaluator>>() : default;
                var buffers1 = chunk.Has<Buffer<Track<T>>>() ? chunk.GetBuffers<Track<T>>() : default;
                var buffers2 = chunk.Has<Buffer<PropertySource<T>>>() ? chunk.GetBuffers<PropertySource<T>>() : default;
                var buffers3 = chunk.Has<Buffer<GraphMotion>>() ? chunk.GetBuffers<GraphMotion>() : default;
                for (int rowIndex = 0; rowIndex < chunk.Count; rowIndex++)
                {
                    if (rangeIndex == ranges.Count || ranges.Items[rangeIndex].Animation != chunk.Entities[rowIndex])
                        continue;
                    MotionRange range = ranges.Items[rangeIndex++];
                    DynamicBuffer<MotionInstance<T, TOps, TParameters, TState, TEvaluator>> instances =
                        buffers0[rowIndex];
                    Span<MotionData<T, TParameters, TState>> data = MotionStorage<T, TOps, TParameters, TState, TEvaluator>.Data(instances);
                    range.Data.AsSpan(0, range.Count).CopyTo(data);
                    DynamicBuffer<Track<T>> tracks = buffers1[rowIndex];
                    Span<Track<T>> writableTracks = tracks.AsSpan();
                    DynamicBuffer<PropertySource<T>> sources = inlineDirect ? buffers2[rowIndex] : default;
                    ref Timeline timeline = ref values0[rowIndex];
                    ReadOnlySpan<TimelineStage> stages = hasStages ? chunk.ReadBuffer<TimelineStage>(rowIndex).AsSpan() : default;
                    DynamicBuffer<GraphMotion> motions = hasMotions ? buffers3[rowIndex] : default;
                    int lastStage = GraphTimes.LastStage(stages, timeline.Stage, timeline.Time, timeline.Paused);
                    for (int index = 0; index < writableTracks.Length; index++)
                    {
                        ref Track<T> track = ref writableTracks[index];
                        if (!Selected(in track, type, evaluation.Weights) || !data[track.MotionIndex].Active)
                            continue;
                        ref MotionData<T, TParameters, TState> motion = ref data[track.MotionIndex];
                        track.Current = motion.Sample.Value;
                        track.Sampled = true;
                        track.Finished = motion.Sample.Finished;
                        float scale = track.TimeScale == 0 ? 1 : track.TimeScale;
                        track.SampleTime = motion.Time.Time / scale + track.Start + track.TimeOffset;
                        motion.Active = false;
                        if (!track.FromGraph && !(track.Stage < lastStage && TrackSystem<T, TOps>.HasLaterTrack(tracks, index, chunk.Entities[rowIndex],
                            track.Target == Entity.Null ? chunk.Entities[rowIndex] : track.Target, in track, lastStage, timeline.Time)))
                        {
                            Submit(world, chunk.Entities[rowIndex], index, in track, sources, inlineDirect, ref evaluation.Writes, ref writeCount);
                        }
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
        }
        finally
        {
            for (int index = 0; index < ranges.Count; index++)
                ranges.Items[index].Clear();
            ranges.Count = 0;
        }
        evaluation.Count = writeCount;
    }

    private sealed class MotionRange
    {
        internal Entity Animation;
        internal MotionData<T, TParameters, TState>[] Data = [];
        internal TParameters[]? Parameters;
        internal int Count;

        internal void Clear()
        {
            Data.AsSpan(0, Count).Clear();
            Parameters = null;
            Animation = default;
            Count = 0;
        }
    }

    private sealed class MotionRanges
    {
        internal readonly List<MotionRange> Items = [];
        internal int Count;

        internal MotionRange Add(Entity animation, int count)
        {
            if (Count == Items.Count)
                Items.Add(new());
            MotionRange range = Items[Count++];
            if (range.Data.Length < count)
                range.Data = new MotionData<T, TParameters, TState>[count];
            range.Animation = animation;
            range.Count = count;
            return range;
        }
    }

    private readonly struct EvaluateRanges(MotionRanges ranges) : IJobParallelFor
    {
        public void Execute(int index)
        {
            MotionRange range = ranges.Items[index];
            var batch = new MotionBatch<T, TParameters, TState>(range.Data.AsSpan(0, range.Count), range.Parameters);
            TEvaluator.EvaluateBatch<TEvaluator>(ref batch);
        }
    }

    private static bool Selected(in Track<T> track, int type, bool weights) =>
        track.MotionType == type && track.SourceActive &&
        (track.Channel.Component == ComponentId<AnimationLayers>.Value) == weights;

    private static void Submit(World world, Entity animation, int index, in Track<T> track,
        DynamicBuffer<PropertySource<T>> sources, bool inlineDirect,
        ref FieldWrite<T>[] writes, ref int count)
    {
        if (inlineDirect && track.Target == Entity.Null)
        {
            if (PropertyAnimation.UpdateSource<T, TOps>(sources, track.SourceIndex, in track))
                FieldWrites<T>.Add(ref writes, ref count, animation, track.Channel, track.Current);
            return;
        }
        if (PropertyAnimation.TrySubmitTyped<T, TOps>(world, animation, (uint)index, in track, out FieldWrite<T> write))
            FieldWrites<T>.Add(ref writes, ref count, write.Target, write.Channel, write.Value);
    }

    public void OnDestroy(ref ImmediateSystemContext context)
    {


        ArrayPool<FieldWrite<T>>.Shared.Return(_writes);
        ArrayPool<GraphCapture<T>>.Shared.Return(_captures);
    }
}

internal readonly record struct GraphCapture<T>(Entity Animation, Entity Target, Channel<T> Channel, int Track) where T : unmanaged;

internal static class GraphTrackValues<T, TOps>
    where T : unmanaged
    where TOps : struct, IValueOps<T>
{
    internal static void Capture(World world, Entity animation, Entity target, DynamicBuffer<Track<T>> tracks, int index, ref Track<T> track)
    {
        T current = track.Current;
        if (track.Channel.Component != ComponentId<AnimationLayers>.Value && world.Has<AnimationLayers>(target) &&
            world.ReadComponentData<AnimationLayers>(target).Items[track.Layer].Mode == LayerMode.Additive)
            PropertyAnimation.ReadBase(world, target, track.Channel, ref current);
        int stage = track.GraphMotion == 0 ? track.Stage : track.MotionStage;
        for (int earlier = 0; earlier < index; earlier++)
        {
            Track<T> previous = tracks[earlier];
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
