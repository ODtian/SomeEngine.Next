using System.Buffers;
using System.Numerics;
using System.Runtime.CompilerServices;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Relations;
using SomeEngine.Job;

namespace SomeEngine.Physics;

public sealed partial class CollisionTree
{
    private readonly List<BranchRange> _branchRanges = [];
    private readonly List<int> _upperBranches = [];

    internal void Rebuild<TComponent, TBoundsReader>(scoped QueryCursor members, scoped QueryCursor access,
        TBoundsReader boundsReader, Scheduler? scheduler)
        where TComponent : struct
        where TBoundsReader : struct, ICollisionBoundsReader<TComponent>
    {
        TComponent[] geometry = ArrayPool<TComponent>.Shared.Rent(Math.Max(16, LeafCount));
        _leaves.Clear();
        _freeNode = -1;
        LeafCount = 0;
        _branchRanges.Clear();
        _upperBranches.Clear();
        try
        {
            var rows = members.Rows;
            while (rows.MoveNext(out var member))
            {
                if (!access.TryGetRow(member.Entity, out var row) || !row.Has<TComponent>()) continue;
                if (LeafCount == geometry.Length)
                {
                    TComponent[] larger = ArrayPool<TComponent>.Shared.Rent(geometry.Length * 2);
                    geometry.CopyTo(larger, 0);
                    ArrayPool<TComponent>.Shared.Return(geometry, RuntimeHelpers.IsReferenceOrContainsReferences<TComponent>());
                    geometry = larger;
                }
                EnsureCapacity(LeafCount + 1);
                _nodes[LeafCount] = new(default, member.Entity, -1, -1, -1, 1, 0);
                geometry[LeafCount++] = row.ReadRef<TComponent>();
            }

            ExecuteParallel(scheduler, new ReadLeafBoundsJob<TComponent, TBoundsReader>(this, geometry, boundsReader),
                LeafCount);
            EnsureCapacity(2 * LeafCount - 1);
            NodeCount = Math.Max(0, 2 * LeafCount - 1);
            Root = LeafCount == 0 ? -1 : LeafCount == 1 ? 0 : LeafCount;
            int targetRanges = Math.Max(1, (scheduler?.WorkerCount ?? JobSystem.WorkerCount) * 4);
            int depth = 0;
            for (int ranges = 1; ranges < targetRanges; ranges *= 2) depth++;
            if (LeafCount > 0) CollectBranchRanges(0, LeafCount, -1, LeafCount, depth);
            ExecuteParallel(scheduler, new BuildSubtreesJob(this), _branchRanges.Count);
            for (int index = _upperBranches.Count - 1; index >= 0; index--) Refit(_upperBranches[index]);
            for (int leaf = 0; leaf < LeafCount; leaf++) _leaves.Add(_nodes[leaf].Entity, leaf);
        }
        finally
        {
            ArrayPool<TComponent>.Shared.Return(geometry, RuntimeHelpers.IsReferenceOrContainsReferences<TComponent>());
        }
    }

    private static void ExecuteParallel<T>(Scheduler? scheduler, in T job, int length) where T : struct, IJobParallelFor
    {
        if (scheduler is null) JobSystem.ExecuteParallel(job, length, JobScheduleOptions.AutomaticBatchSize);
        else scheduler.ExecuteParallel(job, length, JobScheduleOptions.AutomaticBatchSize);
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

    private readonly struct ReadLeafBoundsJob<TComponent, TBoundsReader>(CollisionTree tree, TComponent[] geometry,
        TBoundsReader reader) : IJobParallelFor
        where TComponent : struct
        where TBoundsReader : struct, ICollisionBoundsReader<TComponent>
    {
        public void Execute(int index)
        {
            CollisionBounds bounds = reader.Read(in geometry[index]);
            tree._nodes[index] = new(bounds, tree._nodes[index].Entity, -1, -1, -1, 1, 0);
        }
    }

    private readonly struct BuildSubtreesJob(CollisionTree tree) : IJobParallelFor
    {
        public void Execute(int index)
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
