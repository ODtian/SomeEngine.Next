using System.Numerics;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Relations;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;

namespace SomeEngine.Physics;

public sealed partial class CollisionTree
{
    private readonly List<BranchRange> _branchRanges = [];
    private readonly List<int> _upperBranches = [];

    /// <summary>Builds from participating members while their component reads remain borrowed.</summary>
    public void Rebuild<TComponent, TBoundsReader>(scoped QueryCursor members, scoped QueryCursor access,
        TBoundsReader boundsReader)
        where TComponent : struct
        where TBoundsReader : struct, ICollisionBoundsReader<TComponent>
    {
        _leaves.Clear();
        _freeNode = -1;
        LeafCount = 0;
        _branchRanges.Clear();
        _upperBranches.Clear();
        foreach (QueryRow member in members.Rows)
        {
            if (!access.TryGetRow(member.Entity, out var row) || !row.Has<TComponent>()) continue;
            EnsureCapacity(LeafCount + 1);
            _nodes[LeafCount++] = new(default, member.Entity, -1, -1, -1, 1, 0);
        }

        ReadOnlyQueryPairJobs.ExecuteParallel(access, members,
            new ReadLeafBoundsJob<TComponent, TBoundsReader>(this, boundsReader),
            LeafCount, JobScheduleOptions.AutomaticBatchSize);
        EnsureCapacity(2 * LeafCount - 1);
        NodeCount = Math.Max(0, 2 * LeafCount - 1);
        Root = LeafCount == 0 ? -1 : LeafCount == 1 ? 0 : LeafCount;
        int targetRanges = Math.Max(1, JobSystem.WorkerCount * 4);
        int depth = 0;
        for (int ranges = 1; ranges < targetRanges; ranges *= 2) depth++;
        if (LeafCount > 0) CollectBranchRanges(0, LeafCount, -1, LeafCount, depth);
        ReadOnlyQueryPairJobs.ExecuteParallel(access, members, new BuildSubtreesJob(this),
            _branchRanges.Count, JobScheduleOptions.AutomaticBatchSize);
        for (int index = _upperBranches.Count - 1; index >= 0; index--) Refit(_upperBranches[index]);
        for (int leaf = 0; leaf < LeafCount; leaf++) _leaves.Add(_nodes[leaf].Entity, leaf);
    }

    private void CollectBranchRanges(int start, int count, int parent, int branch, int depth)
    {
        if (count == 1)
        {
            _nodes[start] = _nodes[start].WithParent(parent);
            return;
        }
        if (depth == 0)
        {
            _branchRanges.Add(new(start, count, parent, branch));
            return;
        }
        SplitLeaves(start, count);
        int firstCount = count / 2;
        int first = firstCount == 1 ? start : branch + 1;
        int second = count - firstCount == 1 ? start + firstCount : branch + firstCount;
        _nodes[branch] = new(default, default, parent, first, second, count, 0);
        _upperBranches.Add(branch);
        CollectBranchRanges(start, firstCount, branch, branch + 1, depth - 1);
        CollectBranchRanges(start + firstCount, count - firstCount, branch, branch + firstCount, depth - 1);
    }

    private void BuildBranch(int start, int count, int parent, int branch)
    {
        if (count == 1)
        {
            _nodes[start] = _nodes[start].WithParent(parent);
            return;
        }
        SplitLeaves(start, count);
        int firstCount = count / 2;
        int first = firstCount == 1 ? start : branch + 1;
        int second = count - firstCount == 1 ? start + firstCount : branch + firstCount;
        _nodes[branch] = new(default, default, parent, first, second, count, 0);
        BuildBranch(start, firstCount, branch, branch + 1);
        BuildBranch(start + firstCount, count - firstCount, branch, branch + firstCount);
        Refit(branch);
    }

    private void SplitLeaves(int start, int count)
    {
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
    }

    private readonly record struct BranchRange(int Start, int Count, int Parent, int Branch);

    private readonly struct ReadLeafBoundsJob<TComponent, TBoundsReader>(CollisionTree tree,
        TBoundsReader reader) : IReadOnlyQueryPairJob
        where TComponent : struct
        where TBoundsReader : struct, ICollisionBoundsReader<TComponent>
    {
        public void Execute(int index, scoped QueryCursor access, scoped QueryCursor members)
        {
            access.TryGetRow(tree._nodes[index].Entity, out var row);
            CollisionBounds bounds = reader.Read(in row.ReadRef<TComponent>());
            tree._nodes[index] = new(bounds, row.Entity, -1, -1, -1, 1, 0);
        }
    }

    private readonly struct BuildSubtreesJob(CollisionTree tree) : IReadOnlyQueryPairJob
    {
        public void Execute(int index, scoped QueryCursor access, scoped QueryCursor members)
        {
            BranchRange range = tree._branchRanges[index];
            tree.BuildBranch(range.Start, range.Count, range.Parent, range.Branch);
        }
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
}
