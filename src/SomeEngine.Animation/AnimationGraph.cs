using System.Buffers;
using SomeEngine.Core.Diagnostics;
using SomeEngine.ECS;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Systems;

namespace SomeEngine.Animation;

[BufferCapacity(4)]
public struct GraphBranch : IBufferElement
{
    public int Id;
    public Entity Animation;
}

public static partial class AnimationGraph
{
    /// <summary>Declares the shared time and completion storage used by public leaf systems.</summary>
    public static QueryDefinitionBuilder WithTimeAccess(QueryDefinitionBuilder query) => query
        .Optional<Timeline>(QueryAccess.ReadWrite).Optional<GraphInstance>(QueryAccess.Read)
        .OptionalBuffer<TimelineStage>(QueryAccess.Read).OptionalBuffer<GraphMotion>(QueryAccess.ReadWrite);

    /// <summary>Reads the shared phase time and weight selected for an installed public leaf.</summary>
    public static AnimationLeafTime ReadTime(World world, Entity animation, in AnimationLeafPlacement placement)
    {
        Timeline timeline = world.Read<Timeline>(animation);
        LeafPhase phase = CreatePhase(in timeline, in placement);
        float weight = 1;
        uint starts = timeline.Starts;
        if (placement.Motion == 0)
        {
            if (world.HasBuffer<TimelineStage>(animation))
                world.ExecuteBufferRead<TimelineStage, LeafPhase>(animation, ref phase,
                    static (stages, ref phase) => phase.Select(stages.AsSpan()));
            else phase.Select(default);
        }
        else
        {
            var motion = (Index: placement.Motion - 1, Value: default(GraphMotion));
            world.ExecuteBufferRead<GraphMotion, (int Index, GraphMotion Value)>(animation, ref motion,
                static (motions, ref motion) => motion.Value = motions[motion.Index]);
            GraphData[] graph = world.Read<GraphInstance>(animation).Data!;
            SelectMotion(ref phase, in motion.Value, graph, in placement);
            weight = motion.Value.Weight;
            starts = motion.Value.Starts;
        }
        bool active = world.IsEnabled<Timeline>(animation) && phase.Active && weight > 0;
        return new(phase.Sample, weight, starts, active);
    }

    /// <summary>Reads the same phase clock directly from a row declared with WithTimeAccess.</summary>
    public static AnimationLeafTime ReadTime(scoped in QueryRow row, in AnimationLeafPlacement placement)
    {
        Timeline timeline = row.Read<Timeline>();
        return ReadTime(in row, in timeline, in placement);
    }

    /// <summary>Reads the same phase clock while borrowing the Timeline already selected by the query.</summary>
    public static AnimationLeafTime ReadTime(scoped in QueryRow row, scoped in Timeline timeline,
        in AnimationLeafPlacement placement)
    {
        LeafPhase phase = CreatePhase(in timeline, in placement);
        float weight = 1;
        uint starts = timeline.Starts;
        if (placement.Motion == 0)
            phase.Select(row.HasBuffer<TimelineStage>() ? row.ReadBuffer<TimelineStage>().AsSpan() : default);
        else
        {
            GraphMotion motion = row.ReadBuffer<GraphMotion>()[placement.Motion - 1];
            SelectMotion(ref phase, in motion, row.Read<GraphInstance>().Data!, in placement);
            weight = motion.Weight;
            starts = motion.Starts;
        }
        return new(phase.Sample, weight, starts, row.IsComponentEnabled<Timeline>() && phase.Active && weight > 0);
    }

    private static LeafPhase CreatePhase(in Timeline timeline, in AnimationLeafPlacement placement) => new()
    {
        Stage = timeline.Stage, Time = timeline.Time, Delta = timeline.Delta,
        Paused = timeline.Paused, Target = placement.Stage, Start = placement.Start,
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
            Timeline timeline = world.Read<Timeline>(animation);
            timeline.Submit(running);
            world.Replace(animation, timeline);
        }
        else
        {
            var result = (Index: placement.Motion - 1, Running: running);
            world.ExecuteBufferWrite<GraphMotion, (int Index, bool Running)>(animation, ref result,
                static (motions, ref result) => motions[result.Index].Running |= result.Running);
        }
    }

    /// <summary>Submits stage completion through the Timeline or nested motion borrowed by WithTimeAccess.</summary>
    public static void Submit(scoped in QueryRow row, in AnimationLeafPlacement placement, bool running)
    {
        if (placement.Motion == 0) row.ReadWrite<Timeline>().Submit(running);
        else row.Buffer<GraphMotion>()[placement.Motion - 1].Running |= running;
    }

    /// <summary>Submits completion using the Timeline already borrowed by the query or its nested motion.</summary>
    public static void Submit(scoped in QueryRow row, scoped ref Timeline timeline,
        in AnimationLeafPlacement placement, bool running)
    {
        if (placement.Motion == 0) timeline.Submit(running);
        else row.Buffer<GraphMotion>()[placement.Motion - 1].Running |= running;
    }

    public static Entity GetBranch(World world, Entity animation, int id)
    {
        var branch = (Id: id, Animation: Entity.Null);
        world.ExecuteBufferRead<GraphBranch, (int Id, Entity Animation)>(animation, ref branch,
            static (BufferView<GraphBranch> branches, ref (int Id, Entity Animation) branch) =>
            {
                foreach (GraphBranch candidate in branches.AsSpan())
                    if (candidate.Id == branch.Id)
                    {
                        branch.Animation = candidate.Animation;
                        return;
                    }
            });
        DevelopmentCheck.Require(branch.Animation != Entity.Null, "The graph requires a branch with the requested id.");
        return branch.Animation;
    }
}

public readonly record struct AnimationLeafTime(MotionTime Time, float Weight, uint Starts, bool Active);

/// <summary>Updates parameter-driven graphs before property evaluation.</summary>
public struct GraphSystem : ISystem<ImmediateSystemContext>
{
    private QueryHandle _pool;
    private QueryHandle _instances;
    private int _conditions;

    public void OnCreate(ref ImmediateSystemContext context)
    {
        _pool = context.World.Query(GraphQueries.Pool);
        _instances = CreateQuery(context.World);
    }

    private QueryHandle CreateQuery(World world)
    {
        var query = world.QueryDefinition().ReadWrite<GraphInstance>()
            .Optional<Timeline>(QueryAccess.ReadWrite).Optional<GraphParameters>(QueryAccess.Read)
            .OptionalBuffer<GraphState>(QueryAccess.ReadWrite).OptionalBuffer<GraphMotion>(QueryAccess.ReadWrite);
        GraphConditionAccesses.Declare(query);
        _conditions = GraphConditionAccesses.Version;
        return world.Query(query);
    }

    public void OnUpdate(ref ImmediateSystemContext context)
    {
        World world = context.World;
        if (_conditions != GraphConditionAccesses.Version)
        {
            world.ReleaseQuery(_instances);
            _instances = CreateQuery(world);
        }
        world.ExecuteQuery(_instances, context.LastSystemVersion, ref world,
            static (QueryCursor rows, ref World world) =>
            {
                foreach (QueryRow row in rows.Rows)
                {
                    ref GraphInstance instance = ref row.ReadWrite<GraphInstance>();
                    if (!row.Has<Timeline>())
                    {
                        if (instance.Data is not null)
                        {
                            GraphData.Release(instance.Data);
                        }
                        instance.Data = null;
                        if (!instance.CleanupQueued)
                        {
                            world.Commands().Remove<GraphInstance>(row.Entity);
                            instance.CleanupQueued = true;
                        }
                        continue;
                    }
                    if (instance.Data is null)
                        continue;
                    if (!world.IsEnabled<Timeline>(row.Entity))
                    {
                        GraphPlayback.ReleaseConditions(world, row.Entity, instance.Data);
                        GraphData.Release(instance.Data);
                        instance.Data = null;
                        continue;
                    }
                    if (instance.TimeSource != Entity.Null)
                        continue;
                    if (instance.Data[0].GroupCount == 0)
                        continue;
                    GraphPlayback.Update(world, row.Entity, instance.Data, instance.Part, row.Read<Timeline>(), row.Read<GraphParameters>(),
                        row.Buffer<GraphState>(), row.Buffer<GraphMotion>());
                }
            });
        world.ExecuteQuery(_instances, context.LastSystemVersion, ref world,
            static (QueryCursor rows, ref World world) =>
            {
                foreach (QueryRow row in rows.Rows)
                {
                    GraphInstance instance = row.Read<GraphInstance>();
                    if (instance.Data is not null && instance.TimeSource != Entity.Null &&
                        row.Has<Timeline>() && world.IsEnabled<Timeline>(row.Entity))
                    {
                        if (!world.IsAlive(instance.TimeSource) || !world.Has<Timeline>(instance.TimeSource) || !world.IsEnabled<Timeline>(instance.TimeSource))
                        {
                            world.Disable<Timeline>(row.Entity);
                            continue;
                        }
                        row.ReadWrite<Timeline>() = world.Read<Timeline>(instance.TimeSource);
                        if (instance.Data[0].GroupCount > 0)
                        {
                            DynamicBuffer<GraphMotion> motions = row.Buffer<GraphMotion>();
                            GraphMotion[] values = ArrayPool<GraphMotion>.Shared.Rent(motions.Count);
                            try
                            {
                                world.ExecuteBufferRead<GraphMotion, GraphMotion[]>(instance.TimeSource, ref values,
                                    static (BufferView<GraphMotion> source, ref GraphMotion[] destination) => source.AsSpan().CopyTo(destination));
                                values.AsSpan(0, motions.Count).CopyTo(motions.AsSpan());
                            }
                            finally { ArrayPool<GraphMotion>.Shared.Return(values); }
                        }
                    }
                }
            });
    }

    public void OnDestroy(ref ImmediateSystemContext context)
    {
        World world = context.World;
        context.World.ExecuteQuery(_instances, ref world, static (QueryCursor rows, ref World world) =>
        {
            foreach (QueryRow row in rows.Rows)
            {
                ref GraphInstance instance = ref row.ReadWrite<GraphInstance>();
                if (instance.Data is not null)
                {
                    if (row.Has<Timeline>())
                        GraphPlayback.ReleaseConditions(world, row.Entity, instance.Data);
                    GraphData.Release(instance.Data);
                }
                instance.Data = null;
            }
        });
        context.World.ReleaseQuery(_instances);
        context.World.ReleaseQuery(_pool);
    }
}

internal static class GraphQueries
{
    internal static readonly QueryDefinition Pool = new QueryDefinitionBuilder().Read<Timeline>().All<PooledGraph>().Build();
}
