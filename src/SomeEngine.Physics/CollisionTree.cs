using System.Numerics;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Relations;

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
        var execution = new BuildExecution<TComponent, TBoundsReader>(this, boundsReader);
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
    public void Rebuild<TComponent, TBoundsReader>(QueryCursor members, QueryCursor access,
        TBoundsReader boundsReader)
        where TComponent : struct
        where TBoundsReader : struct, ICollisionBoundsReader<TComponent>
    {
        _leaves.Clear();
        _freeNode = -1;
        LeafCount = 0;
        var rows = members.Rows;
        while (rows.MoveNext(out var member))
        {
            if (!access.TryGetRow(member.Entity, out var row) || !row.Has<TComponent>())
                continue;
            EnsureCapacity(LeafCount + 1);
            CollisionBounds bounds = boundsReader.Read(in row.ReadRef<TComponent>());
            _nodes[LeafCount++] = new CollisionNode(bounds, member.Entity, -1, -1, -1, 1, 0);
        }

        EnsureCapacity(2 * LeafCount - 1);
        NodeCount = LeafCount;
        Root = LeafCount == 0 ? -1 : BuildBranch(0, LeafCount, -1);
        for (int leaf = 0; leaf < LeafCount; leaf++)
            _leaves.Add(_nodes[leaf].Entity, leaf);
    }

    private int BuildBranch(int start, int count, int parent)
    {
        if (count == 1)
        {
            _nodes[start] = _nodes[start].WithParent(parent);
            return start;
        }

        Vector3 firstCenter = _nodes[start].Bounds.Min + _nodes[start].Bounds.Max;
        Vector3 minCenter = firstCenter;
        Vector3 maxCenter = firstCenter;
        for (int index = start + 1; index < start + count; index++)
        {
            Vector3 center = _nodes[index].Bounds.Min + _nodes[index].Bounds.Max;
            minCenter = Vector3.Min(minCenter, center);
            maxCenter = Vector3.Max(maxCenter, center);
        }
        Vector3 extent = maxCenter - minCenter;
        int axis = extent.X >= extent.Y && extent.X >= extent.Z ? 0 : extent.Y >= extent.Z ? 1 : 2;
        _nodes.AsSpan(start, count).Sort(CenterComparison.ForAxis(axis));

        int branch = NodeCount++;
        int firstCount = count / 2;
        int first = BuildBranch(start, firstCount, branch);
        int second = BuildBranch(start + firstCount, count - firstCount, branch);
        _nodes[branch] = new CollisionNode(
            CollisionBounds.Union(in _nodes[first].Bounds, in _nodes[second].Bounds),
            default, parent, first, second, count, 1 + Math.Max(_nodes[first].Height, _nodes[second].Height));
        return branch;
    }

    private void EnsureCapacity(int count)
    {
        if (count > _nodes.Length)
            Array.Resize(ref _nodes, Math.Max(count, Math.Max(16, _nodes.Length * 2)));
    }

    private sealed class CenterComparison(int axis) : IComparer<CollisionNode>
    {
        private static readonly CenterComparison X = new(0);
        private static readonly CenterComparison Y = new(1);
        private static readonly CenterComparison Z = new(2);

        internal static CenterComparison ForAxis(int axis) => axis == 0 ? X : axis == 1 ? Y : Z;

        public int Compare(CollisionNode first, CollisionNode second)
        {
            float firstCenter = first.Bounds.Min[axis] + first.Bounds.Max[axis];
            float secondCenter = second.Bounds.Min[axis] + second.Bounds.Max[axis];
            int comparison = firstCenter.CompareTo(secondCenter);
            return comparison != 0 ? comparison : EntityOrder.Compare(first.Entity, second.Entity);
        }
    }

    private struct BuildExecution<TComponent, TBoundsReader>(CollisionTree tree, TBoundsReader boundsReader)
        : IQueryExecution
        where TComponent : struct
        where TBoundsReader : struct, ICollisionBoundsReader<TComponent>
    {
        public void Execute(QueryCursor query) =>
            tree.Rebuild<TComponent, TBoundsReader>(query, query, boundsReader);
    }
}
