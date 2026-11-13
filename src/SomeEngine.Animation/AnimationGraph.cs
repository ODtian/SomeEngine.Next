using System.Buffers;
using SomeEngine.Core.Diagnostics;
using SomeEngine.ECS;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Systems;

namespace SomeEngine.Animation;

public struct GraphBranch : IBufferElement
{
    public int Id;
    public Entity Animation;
}

public static partial class AnimationGraph
{
    /// <summary>Declares the shared time and completion storage used by public leaf systems.</summary>
    public static QueryDescription WithTimeAccess(QueryDescription query) => query
        .Optional<Timeline>().Optional<GraphInstance>()
        .Optional<Buffer<TimelineStage>>().Optional<Buffer<GraphMotion>>();

    /// <summary>Reads the shared phase time and weight selected for an installed public leaf.</summary>
    public static AnimationLeafTime ReadTime(World world, Entity animation, in AnimationLeafPlacement placement)
    {
        Timeline timeline = world.ReadValue<Timeline>(animation);
        LeafPhase phase = CreatePhase(in timeline, in placement);
        float weight = 1;
        uint starts = timeline.Starts;
        if (placement.Motion == 0)
        {
            if (world.Has<Buffer<TimelineStage>>(animation))
            {
                using var stages = world.ReadBuffer<TimelineStage>(animation);
                phase.Select(stages.AsSpan());
            }
            else phase.Select(default);
        }
        else
        {
            var motion = (Index: placement.Motion - 1, Value: default(GraphMotion));
            {
                using var motions = world.ReadBuffer<GraphMotion>(animation);
                motion.Value = motions[motion.Index];
            }
            GraphData[] graph = world.ReadValue<GraphInstance>(animation).Data!;
            SelectMotion(ref phase, in motion.Value, graph, in placement);
            weight = motion.Value.Weight;
            starts = motion.Value.Starts;
        }
        bool active = world.IsEnabled<Timeline>(animation) && phase.Active && weight > 0;
        return new(phase.Sample, weight, starts, active);
    }

    /// <summary>Reads the same phase clock directly from a row declared with WithTimeAccess.</summary>
    public static AnimationLeafTime ReadTime(QueryChunk chunk, int rowIndex, in AnimationLeafPlacement placement)
    {
        Timeline timeline = chunk.Read<Timeline>()[rowIndex];
        return ReadTime(chunk, rowIndex, in timeline, in placement);
    }

    /// <summary>Reads the same phase clock while borrowing the Timeline already selected by the query.</summary>
    public static AnimationLeafTime ReadTime(QueryChunk chunk, int rowIndex, scoped in Timeline timeline,
        in AnimationLeafPlacement placement)
    {
        LeafPhase phase = CreatePhase(in timeline, in placement);
        float weight = 1;
        uint starts = timeline.Starts;
        if (placement.Motion == 0)
            phase.Select(chunk.Has<Buffer<TimelineStage>>(rowIndex) ? chunk.ReadBuffer<TimelineStage>(rowIndex).AsSpan() : default);
        else
        {
            GraphMotion motion = chunk.ReadBuffer<GraphMotion>(rowIndex)[placement.Motion - 1];
            SelectMotion(ref phase, in motion, chunk.Read<GraphInstance>()[rowIndex].Data!, in placement);
            weight = motion.Weight;
            starts = motion.Starts;
        }
        return new(phase.Sample, weight, starts, chunk.Optional<Timeline>(rowIndex).IsEnabled && phase.Active && weight > 0);
    }

    private static LeafPhase CreatePhase(in Timeline timeline, in AnimationLeafPlacement placement) => new()
    {
        Stage = timeline.Stage,
        Time = timeline.Time,
        Delta = timeline.Delta,
        Paused = timeline.Paused,
        Target = placement.Stage,
        Start = placement.Start,
    };

    private static void SelectMotion(ref LeafPhase phase, in GraphMotion motion, GraphData[] graph,
        in AnimationLeafPlacement placement)
    {
        GraphMotionData definition = graph[0].Motions![placement.Motion - 1];
        phase.Stage = motion.Stage;
        phase.Time = motion.Time;
        phase.Target = placement.MotionStage;
        phase.Select(definition.Stages.AsSpan(0, definition.StageCount));
    }

    private struct LeafPhase
    {
        public int Stage, Target;
        public float Time, Delta, Start;
        public bool Paused, Active;
        public MotionTime Sample;

        public void Select(ReadOnlySpan<TimelineStage> stages)
        {
            int last = Paused ? Stage : GraphTimes.Project(stages, Stage, Time).Stage;
            if (Target < Stage || Target > last) return;
            float time = Time;
            for (int stage = Stage; stage < Target; stage++) time -= stages[stage].Duration;
            float previous = Math.Max(0, time - Delta - Start);
            if (Target < stages.Length && stages[Target].End == StageEnd.Duration)
                time = Math.Min(time, stages[Target].Duration);
            float elapsed = Math.Max(0, time - Start);
            Sample = new MotionTime(elapsed, Paused ? 0 : Math.Max(0, elapsed - previous));
            Active = time >= Start;
        }
    }

    /// <summary>Submits a public leaf's current running result to its shared stage.</summary>
    public static void Submit(World world, Entity animation, in AnimationLeafPlacement placement, bool running)
    {
        if (placement.Motion == 0)
        {
            Timeline timeline = world.ReadValue<Timeline>(animation);
            timeline.Submit(running);
            world.Set(animation, timeline);
        }
        else
        {
            var result = (Index: placement.Motion - 1, Running: running);
            {
                using var motions = world.GetBuffer<GraphMotion>(animation);
                motions[result.Index].Running |= result.Running;
            }
        }
    }

    /// <summary>Submits stage completion through the Timeline or nested motion borrowed by WithTimeAccess.</summary>
    public static void Submit(QueryChunk chunk, int rowIndex, in AnimationLeafPlacement placement, bool running)
    {
        if (placement.Motion == 0) chunk.Write<Timeline>()[rowIndex].Submit(running);
        else chunk.GetBuffers<GraphMotion>()[rowIndex][placement.Motion - 1].Running |= running;
    }

    /// <summary>Submits completion using the Timeline already borrowed by the query or its nested motion.</summary>
    public static void Submit(QueryChunk chunk, int rowIndex, scoped ref Timeline timeline,
        in AnimationLeafPlacement placement, bool running)
    {
        if (placement.Motion == 0) timeline.Submit(running);
        else chunk.GetBuffers<GraphMotion>()[rowIndex][placement.Motion - 1].Running |= running;
    }

    public static Entity GetBranch(World world, Entity animation, int id)
    {
        var branch = (Id: id, Animation: Entity.Null);
        {
            using var branches = world.ReadBuffer<GraphBranch>(animation);
            Visit(branches, ref branch);

            static void Visit(ReadOnlyBuffer<GraphBranch> branches, ref (int Id, Entity Animation) branch)
            {
                foreach (GraphBranch candidate in branches.AsSpan())
                    if (candidate.Id == branch.Id)
                    {
                        branch.Animation = candidate.Animation;
                        return;
                    }

            }
        }
        DevelopmentCheck.Require(branch.Animation != Entity.Null, "The graph requires a branch with the requested id.");
        return branch.Animation;
    }
}

public readonly record struct AnimationLeafTime(MotionTime Time, float Weight, uint Starts, bool Active);

/// <summary>Updates parameter-driven graphs before property evaluation.</summary>
public struct GraphSystem : ISystem<ImmediateSystemContext>
{
    private Query _instances;
    private List<Entity> _cleanup;
    private List<Entity> _disabled;
    private int _conditions;

    public void OnCreate(ref ImmediateSystemContext context)
    {
        _cleanup = [];
        _disabled = [];
        _instances = CreateQuery(context.World);
    }

    private Query CreateQuery(World world)
    {
        var query = new QueryDescription().WithStored<GraphInstance>()
            .Optional<Timeline>().Optional<GraphParameters>()
            .Optional<Buffer<GraphState>>().Optional<Buffer<GraphMotion>>();
        GraphConditionAccesses.Declare(query);
        _conditions = GraphConditionAccesses.Version;
        return world.Query(query);
    }

    public void OnUpdate(ref ImmediateSystemContext context)
    {
        World world = context.World;
        _cleanup.Clear();
        _disabled.Clear();
        if (_conditions != GraphConditionAccesses.Version)
        {

            _instances = CreateQuery(world);
        }
        {
            using QueryScope read = _instances.Since(context.LastSystemVersion).Open();
            Visit(in read, ref world, _cleanup);

            static void Visit(scoped in QueryScope rows, ref World world, List<Entity> cleanup)
            {
                foreach (QueryChunk chunk in rows)
                {
                    var values0 = chunk.Has<GraphInstance>() ? chunk.Write<GraphInstance>() : default;
                    chunk.TryRead<Timeline>(out var values1);
                    chunk.TryRead<GraphParameters>(out var values2);
                    var buffers0 = chunk.Has<Buffer<GraphState>>() ? chunk.GetBuffers<GraphState>() : default;
                    var buffers1 = chunk.Has<Buffer<GraphMotion>>() ? chunk.GetBuffers<GraphMotion>() : default;
                    for (int rowIndex = 0; rowIndex < chunk.Count; rowIndex++)
                    {
                        ref GraphInstance instance = ref values0[rowIndex];
                        if (!chunk.Has<Timeline>(rowIndex))
                        {
                            if (instance.Data is not null)
                            {
                                GraphData.Release(instance.Data);
                            }
                            instance.Data = null;
                            if (!instance.CleanupQueued)
                            {
                                cleanup.Add(chunk.Entities[rowIndex]);
                                instance.CleanupQueued = true;
                            }
                            continue;
                        }
                        if (instance.Data is null)
                            continue;
                        if (!world.IsEnabled<Timeline>(chunk.Entities[rowIndex]))
                        {
                            GraphPlayback.ReleaseConditions(world, chunk.Entities[rowIndex], instance.Data);
                            GraphData.Release(instance.Data);
                            instance.Data = null;
                            continue;
                        }
                        if (instance.TimeSource != Entity.Null)
                            continue;
                        if (instance.Data[0].GroupCount == 0)
                            continue;
                        GraphPlayback.Update(world, chunk.Entities[rowIndex], instance.Data, instance.Part, values1[rowIndex], values2[rowIndex],
                            buffers0[rowIndex], buffers1[rowIndex]);

                    }
                }

            }
        }
        {
            using QueryScope read = _instances.Since(context.LastSystemVersion).Open();
            Visit(in read, ref world, _disabled);

            static void Visit(scoped in QueryScope rows, ref World world, List<Entity> disabled)
            {
                foreach (QueryChunk chunk in rows)
                {
                    var values0 = chunk.Has<Timeline>() ? chunk.Write<Timeline>() : default;
                    chunk.TryRead<GraphInstance>(out var values1);
                    var buffers0 = chunk.Has<Buffer<GraphMotion>>() ? chunk.GetBuffers<GraphMotion>() : default;
                    for (int rowIndex = 0; rowIndex < chunk.Count; rowIndex++)
                    {
                        GraphInstance instance = values1[rowIndex];
                        if (instance.Data is not null && instance.TimeSource != Entity.Null &&
                            chunk.Has<Timeline>(rowIndex) && world.IsEnabled<Timeline>(chunk.Entities[rowIndex]))
                        {
                            if (!world.IsAlive(instance.TimeSource) || !world.Has<Timeline>(instance.TimeSource) || !world.IsEnabled<Timeline>(instance.TimeSource))
                            {
                                disabled.Add(chunk.Entities[rowIndex]);
                                continue;
                            }
                            values0[rowIndex] = world.ReadValue<Timeline>(instance.TimeSource);
                            if (instance.Data[0].GroupCount > 0)
                            {
                                DynamicBuffer<GraphMotion> motions = buffers0[rowIndex];
                                GraphMotion[] values = ArrayPool<GraphMotion>.Shared.Rent(motions.Count);
                                try
                                {
                                    {
                                        using var source = world.ReadBuffer<GraphMotion>(instance.TimeSource);
                                        source.AsSpan().CopyTo(values);
                                    }
                                    values.AsSpan(0, motions.Count).CopyTo(motions.AsSpan());
                                }
                                finally { ArrayPool<GraphMotion>.Shared.Return(values); }
                            }
                        }

                    }
                }

            }
        }
        foreach (Entity entity in _disabled) world.Disable<Timeline>(entity);
        foreach (Entity entity in _cleanup) world.Remove<GraphInstance>(entity);
    }

    public void OnDestroy(ref ImmediateSystemContext context)
    {
        World world = context.World;
        {
            using QueryScope read = _instances.Open();
            Visit(in read, ref world);

            static void Visit(scoped in QueryScope rows, ref World world)
            {
                foreach (QueryChunk chunk in rows)
                {
                    var values0 = chunk.Has<GraphInstance>() ? chunk.Write<GraphInstance>() : default;
                    for (int rowIndex = 0; rowIndex < chunk.Count; rowIndex++)
                    {
                        ref GraphInstance instance = ref values0[rowIndex];
                        if (instance.Data is not null)
                        {
                            if (chunk.Has<Timeline>(rowIndex))
                                GraphPlayback.ReleaseConditions(world, chunk.Entities[rowIndex], instance.Data);
                            GraphData.Release(instance.Data);
                        }
                        instance.Data = null;

                    }
                }

            }
        }


    }
}

internal static class GraphQueries
{
    internal static readonly QueryDescription Pool = new QueryDescription().WithStored<Timeline>().WithStored<PooledGraph>();
}
