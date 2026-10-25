using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;

namespace SomeEngine.Physics;

public sealed partial class CollisionMaintenance<TGeometry, TBoundsReader, TFilter, TGroupResults>
{
    private readonly HashSet<Entity> _changedEntities = [];
    private readonly List<MemberChange> _memberChanges = [];
    private readonly List<Entity> _boundsEntities = [];
    private CollisionBounds[] _bounds = [];
    private readonly HashSet<int> _dirtyNodes = [];
    private int[] _dirtyChildren = [];
    private List<int> _currentLayer = [];
    private List<int> _nextLayer = [];

    private void BeginChanges()
    {
        _changedEntities.Clear();
        _memberChanges.Clear();
        _boundsEntities.Clear();
        _dirtyNodes.Clear();
        _currentLayer.Clear();
        _nextLayer.Clear();
    }

    private void RecordChange(Entity entity, scoped QueryCursor members, scoped QueryCursor access)
    {
        if (!_changedEntities.Add(entity)) return;
        int boundsIndex = -1;
        if (members.TryGetRow(entity, out _) && access.TryGetRow(entity, out var row) && row.Has<TGeometry>())
        {
            boundsIndex = _boundsEntities.Count;
            _boundsEntities.Add(entity);
        }
        _memberChanges.Add(new(entity, boundsIndex));
    }

    private void ApplyChanges(scoped QueryCursor members, scoped QueryCursor access)
    {
        if (_bounds.Length < _boundsEntities.Count) Array.Resize(ref _bounds, _boundsEntities.Count);
        ReadOnlyQueryPairJobs.ExecuteParallel(access, members, new BoundsJob(this),
            _boundsEntities.Count, JobScheduleOptions.AutomaticBatchSize);

        CollisionTree tree = Tree;
        foreach (MemberChange change in _memberChanges)
        {
            if (change.BoundsIndex >= 0)
            {
                ref readonly CollisionBounds bounds = ref _bounds[change.BoundsIndex];
                if (tree.TryGetLeaf(change.Entity, out int leaf))
                    tree.SetBounds(leaf, in bounds);
                else
                    leaf = tree.Insert(change.Entity, in bounds);
                MarkAncestors(leaf);
            }
            else if (tree.TryGetLeaf(change.Entity, out _))
            {
                MarkAncestors(tree.Remove(change.Entity));
            }
        }
        RefitLayers(access);
    }

    private void MarkAncestors(int node)
    {
        while (node >= 0)
        {
            _dirtyNodes.Add(node);
            node = Tree.Nodes[node].Parent;
        }
    }

    private void RefitLayers(scoped QueryCursor access)
    {
        CollisionTree tree = Tree;
        if (_dirtyChildren.Length < tree.NodeCount) Array.Resize(ref _dirtyChildren, tree.NodeCount);
        foreach (int node in _dirtyNodes)
        {
            ref readonly CollisionNode data = ref tree.Nodes[node];
            if (data.MemberCount == 0) continue;
            int children = data.IsLeaf ? 0 :
                (_dirtyNodes.Contains(data.FirstChild) ? 1 : 0) +
                (_dirtyNodes.Contains(data.SecondChild) ? 1 : 0);
            _dirtyChildren[node] = children;
            if (children == 0) _currentLayer.Add(node);
        }

        TFilter filter = Query.Enumerable.Filter;
        while (_currentLayer.Count > 0)
        {
            _scheduler.ExecuteParallel(new BoundsRefitJob(tree, _currentLayer), _currentLayer.Count,
                JobScheduleOptions.AutomaticBatchSize);
            foreach (int node in _currentLayer)
            {
                filter.RefitSummary(node, access);
                int parent = tree.Nodes[node].Parent;
                if (parent >= 0 && _dirtyNodes.Contains(parent) && --_dirtyChildren[parent] == 0)
                    _nextLayer.Add(parent);
            }
            _currentLayer.Clear();
            (_currentLayer, _nextLayer) = (_nextLayer, _currentLayer);
        }
    }

    private readonly record struct MemberChange(Entity Entity, int BoundsIndex);

    private readonly struct BoundsJob(
        CollisionMaintenance<TGeometry, TBoundsReader, TFilter, TGroupResults> maintenance) : IReadOnlyQueryPairJob
    {
        public void Execute(int index, scoped QueryCursor access, scoped QueryCursor members)
        {
            access.TryGetRow(maintenance._boundsEntities[index], out var row);
            maintenance._bounds[index] = maintenance.Query.Enumerable.Source.ReadBounds(row);
        }
    }

    private readonly struct BoundsRefitJob(CollisionTree tree, List<int> nodes) : IJobParallelFor
    {
        public void Execute(int index) => tree.Refit(nodes[index]);
    }
}
