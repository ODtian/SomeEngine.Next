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
        var time = (Stage: (int)timeline.Stage, Time: timeline.Time, Weight: 1f, Starts: timeline.Starts);
        if (placement.Motion == 0)
        {
            var phase = (Stage: time.Stage, Time: time.Time);
            if (world.HasBuffer<TimelineStage>(animation))
                world.ExecuteBufferRead<TimelineStage, (int Stage, float Time)>(animation, ref phase,
                    static (stages, ref phase) => phase = GraphTimes.Project(stages.AsSpan(), phase.Stage, phase.Time));
            time.Stage = phase.Stage;
            time.Time = phase.Time;
        }
        else
        {
            var motion = (Index: placement.Motion - 1, Value: default(GraphMotion));
            world.ExecuteBufferRead<GraphMotion, (int Index, GraphMotion Value)>(animation, ref motion,
                static (motions, ref motion) => motion.Value = motions[motion.Index]);
            GraphData[] graph = world.Read<GraphInstance>(animation).Data!;
            GraphMotionData definition = graph[0].Motions![placement.Motion - 1];
            var phase = GraphTimes.Project(definition.Stages.AsSpan(0, definition.StageCount), motion.Value.Stage, motion.Value.Time);
            time = (phase.Stage, phase.Time, motion.Value.Weight, motion.Value.Starts);
        }
        int stage = placement.Motion == 0 ? placement.Stage : placement.MotionStage;
        bool active = world.IsEnabled<Timeline>(animation) && time.Stage == stage && time.Time >= placement.Start && time.Weight > 0;
        float elapsed = Math.Max(0, time.Time - placement.Start);
        return new(new MotionTime(elapsed, timeline.Paused ? 0 : Math.Min(timeline.Delta, elapsed)), time.Weight, time.Starts, active);
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
                            GraphPlayback.ReleaseConditions(world, row.Entity, instance.Data);
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
