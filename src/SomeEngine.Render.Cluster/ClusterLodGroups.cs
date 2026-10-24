using SomeEngine.Resources.Geometry;

namespace SomeEngine.Render.Cluster;

/// <summary>Controls the complete coarse-to-fine handoff for each mesh reduction group.</summary>
internal sealed class ClusterLodGroups
{
    private const int HideGeometry = 1;
    private const int KeepCoarseGeometry = 2;
    private readonly int[] _groupsByNode;
    private readonly int[] _parentsByNode;
    private readonly Dictionary<int, ReductionGroup> _groups = [];

    private sealed class ReductionGroup
    {
        internal readonly List<int> Children = [];
        internal readonly HashSet<int> Parents = [];
        internal byte Readiness;
    }

    internal ClusterLodGroups(ReadOnlySpan<ClusterBVHNode> nodes)
    {
        _groupsByNode = new int[nodes.Length];
        _parentsByNode = new int[nodes.Length];
        for (int node = 0; node < nodes.Length; node++)
        {
            if (nodes[node].NodeType != 1u) continue;
            int group = (int)nodes[node].BoundMin.W;
            int parent = (int)nodes[node].BoundMax.W;
            _groupsByNode[node] = group;
            _parentsByNode[node] = parent;
            if (parent != 0) GetGroup(parent).Children.Add(node);
            if (group != 0)
            {
                ReductionGroup current = GetGroup(group);
                if (parent != 0) current.Parents.Add(parent);
            }
        }

        ReductionGroup GetGroup(int index)
        {
            if (!_groups.TryGetValue(index, out ReductionGroup? group))
                _groups.Add(index, group = new ReductionGroup());
            return group;
        }
    }

    internal bool UpdateReadiness(Span<ClusterBVHNode> nodes)
    {
        foreach (ReductionGroup group in _groups.Values) group.Readiness = 0;
        bool changed = false;
        for (int node = 0; node < nodes.Length; node++)
        {
            if (nodes[node].NodeType != 1u) continue;
            int group = _groupsByNode[node];
            int parent = _parentsByNode[node];
            int flags = parent != 0 && !CanRefine(parent, nodes)
                ? HideGeometry
                : group != 0 && !CanRefine(group, nodes) ? KeepCoarseGeometry : 0;
            if (nodes[node].BoundMin.W == flags) continue;
            nodes[node].BoundMin.W = flags;
            changed = true;
        }
        return changed;
    }

    private bool CanRefine(int index, ReadOnlySpan<ClusterBVHNode> nodes)
    {
        ReductionGroup group = _groups[index];
        if (group.Readiness != 0) return group.Readiness == 2;
        foreach (int child in group.Children)
            if (nodes[child].ChildPointer == ClusterBvh.PageFaultMarker)
            {
                group.Readiness = 1;
                return false;
            }
        foreach (int parent in group.Parents)
            if (!CanRefine(parent, nodes))
            {
                group.Readiness = 1;
                return false;
            }
        group.Readiness = 2;
        return true;
    }

    internal void CollectRequiredNodes(ReadOnlySpan<int> pageNodes, List<int> requiredNodes, HashSet<int> visited)
    {
        foreach (int node in pageNodes)
            if (_parentsByNode[node] != 0)
                AddGroup(_parentsByNode[node]);

        void AddGroup(int index)
        {
            if (!visited.Add(index)) return;
            ReductionGroup group = _groups[index];
            requiredNodes.AddRange(group.Children);
            foreach (int parent in group.Parents) AddGroup(parent);
        }
    }
}
