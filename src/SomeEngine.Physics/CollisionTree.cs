using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Relations;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;

namespace SomeEngine.Physics;

/// <summary>Holds one binary AABB topology. Node numbers also address attached summaries.</summary>
public sealed partial class CollisionTree
{
    private CollisionNode[] _nodes = [];
    private readonly Dictionary<Entity, int> _leaves = [];
    private int _freeNode = -1;

    public int Root { get; private set; } = -1;

    public int LeafCount { get; private set; }

    public int NodeCount { get; private set; }

    public int NodeCapacity => _nodes.Length;

    public int LeafMapCount => _leaves.Count;

    public int LeafMapCapacity => _leaves.EnsureCapacity(0);

    public int Height => Root < 0 ? 0 : _nodes[Root].Height;

    public ReadOnlySpan<CollisionNode> Nodes => _nodes.AsSpan(0, NodeCount);

    /// <summary>Reuses this tree's storage to build the current World members.</summary>
    public void Rebuild<TComponent, TBoundsReader>(World world, QueryHandle members,
        TBoundsReader boundsReader)
        where TComponent : struct
        where TBoundsReader : struct, ICollisionBoundsReader<TComponent>
    {
        QueryDefinition definition = world.GetQueryDefinition(members).WithOptionalRead<TComponent>();
        QueryHandle query = world.Query(definition);
        WorldJobs.TryGetScheduler(world, out Scheduler? scheduler);
        var execution = new BuildExecution<TComponent, TBoundsReader>(this, boundsReader, scheduler);
        try
        {
            world.ExecuteQuery(query, ref execution);
        }
        finally
        {
            world.ReleaseQuery(query);
        }
    }

    /// <summary>Builds from the participating members under their encompassing read scope.</summary>
    public void Rebuild<TComponent, TBoundsReader>(scoped QueryCursor members, scoped QueryCursor access,
        TBoundsReader boundsReader)
        where TComponent : struct
        where TBoundsReader : struct, ICollisionBoundsReader<TComponent>
        => Rebuild<TComponent, TBoundsReader>(members, access, boundsReader, null);

    private void EnsureCapacity(int count)
    {
        if (count > _nodes.Length)
            Array.Resize(ref _nodes, Math.Max(count, Math.Max(16, _nodes.Length * 2)));
    }

    private struct BuildExecution<TComponent, TBoundsReader>(CollisionTree tree, TBoundsReader boundsReader,
        Scheduler? scheduler)
        : IQueryExecution
        where TComponent : struct
        where TBoundsReader : struct, ICollisionBoundsReader<TComponent>
    {
        public void Execute(scoped QueryCursor query) =>
            tree.Rebuild<TComponent, TBoundsReader>(query, query, boundsReader, scheduler);
    }
}
