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

public static class AnimationGraph
{
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

/// <summary>Updates parameter-driven graphs before property evaluation.</summary>
public struct GraphSystem : ISystem<ImmediateSystemContext>
{
    private QueryHandle _pool;
    private QueryHandle _instances;

    public void OnCreate(ref ImmediateSystemContext context)
    {
        _pool = context.World.Query(GraphQueries.Pool);
        _instances = context.World.Query(context.World.QueryDefinition().ReadWrite<GraphInstance>()
            .Optional<Timeline>(QueryAccess.ReadWrite).Optional<GraphParameters>(QueryAccess.Read)
            .OptionalBuffer<GraphState>(QueryAccess.Write).OptionalBuffer<GraphMotion>(QueryAccess.Write));
    }

    public void OnUpdate(ref ImmediateSystemContext context)
    {
        World world = context.World;
        world.ExecuteQuery(_instances, context.LastSystemVersion, ref world,
            static (QueryCursor rows, ref World world) =>
            {
                foreach (QueryRow row in rows.Rows)
                {
                    ref GraphInstance instance = ref row.ReadWrite<GraphInstance>();
                    if (!row.Has<Timeline>())
                    {
                        if (instance.Data is not null)
                            GraphData.Release(instance.Data);
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
                        GraphData.Release(instance.Data);
                        instance.Data = null;
                        continue;
                    }
                    if (instance.TimeSource != Entity.Null)
                        continue;
                    if (instance.Data[0].GroupCount == 0)
                        continue;
                    GraphPlayback.Update(instance.Data, instance.Part, row.Read<Timeline>(), row.Read<GraphParameters>(),
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
        context.World.ExecuteQuery(_instances, static rows =>
        {
            foreach (QueryRow row in rows.Rows)
            {
                ref GraphInstance instance = ref row.ReadWrite<GraphInstance>();
                if (instance.Data is not null)
                    GraphData.Release(instance.Data);
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
