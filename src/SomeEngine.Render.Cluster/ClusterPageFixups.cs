using System.Numerics;
using System.Runtime.InteropServices;
using SomeEngine.Resources;
using SomeEngine.Resources.Geometry;

namespace SomeEngine.Render.Cluster;

/// <summary>Reduction-group dependencies and the records changed by page installation.</summary>
internal sealed class ClusterPageFixups
{
    internal sealed class Part(MeshHierarchyPart data, uint errors)
    {
        internal readonly MeshHierarchyPart Data = data;
        internal readonly uint Errors = errors & 0x7FFFFFFFu;
        internal readonly List<Group> ChildGroups = [];
    }

    internal sealed class Group(Mesh mesh, uint firstPage, uint firstNode, uint root,
        MeshClusterGroup data, Vector4 bounds)
    {
        internal readonly Mesh Mesh = mesh;
        internal readonly uint FirstPage = firstPage, FirstNode = firstNode, Root = root;
        internal readonly MeshClusterGroup Data = data;
        internal readonly Vector4 Bounds = bounds;
        internal Part[] Parts = [];
        internal Part[] ParentParts = [];
        internal bool Complete;
    }

    private readonly Dictionary<uint, List<Group>> _groupsByPage = [];
    private readonly Dictionary<uint, List<Group>> _groupsByParentPage = [];
    private readonly HashSet<Group> _changedGroups = [];
    private int _groupCount;
    internal sealed record Batch(ClusterLeafFlagFixup[] Flags, ClusterLodChange[] Changes, Dictionary<Group, bool> GroupStates);

    internal Group[] ReserveRegistration(in ClusterBvhRegistration registration)
    {
        if (registration.Mesh.ClusterGroups.IsEmpty)
            throw new InvalidDataException("Cluster geometry requires reduction-group fixups.");
        ClusterBVHNode[] nodes = MemoryMarshal.Cast<byte, ClusterBVHNode>(registration.Mesh.Bvh.Span).ToArray();
        var groups = new List<Group>();
        var partsByCluster = new Dictionary<(uint Page, uint Cluster), Part>();
        foreach (MeshClusterGroup data in registration.Mesh.ClusterGroups)
        {
            if (data.PageCount == 0 || data.FirstPage + data.PageCount > registration.PageCount || data.HierarchyParts.IsEmpty)
                throw new InvalidDataException("A reduction group exceeds its mesh pages or lacks hierarchy parts.");
            MeshHierarchyPart first = data.HierarchyParts[0];
            Vector4 bounds = nodes[(int)first.NodeIndex].GetChild((int)first.ChildIndex).LODBounds;
            var group = new Group(registration.Mesh, registration.FirstPage, registration.FirstNode,
                registration.Root, data, bounds);
            group.Parts = data.HierarchyParts.Select(part =>
            {
                ClusterHierarchySlice slice = nodes[(int)part.NodeIndex].GetChild((int)part.ChildIndex);
                var result = new Part(part, slice.PackedErrors);
                for (uint cluster = part.FirstCluster; cluster < part.FirstCluster + slice.ClusterCount; cluster++)
                    partsByCluster.Add((part.PageIndex, cluster), result);
                return result;
            }).ToArray();
            groups.Add(group);
        }
        foreach (Group group in groups)
        {
            group.ParentParts = group.Data.Parents.Select(parent => partsByCluster[(parent.PageIndex, parent.ClusterIndex)]).Distinct().ToArray();
            foreach (Part part in group.ParentParts) part.ChildGroups.Add(group);
        }
        Reserve(_groupsByPage, groups, static group => Enumerable.Range((int)group.Data.FirstPage, (int)group.Data.PageCount).Select(static page => (uint)page));
        Reserve(_groupsByParentPage, groups, static group => group.Data.Parents.Select(static parent => parent.PageIndex).Distinct());
        _changedGroups.EnsureCapacity(checked(_groupCount + groups.Count));
        return groups.ToArray();

        static void Reserve(Dictionary<uint, List<Group>> table, List<Group> groups, Func<Group, IEnumerable<uint>> pages)
        {
            foreach (var entry in groups.SelectMany(group => pages(group).Select(page => (Page: page + group.FirstPage, Group: group))).GroupBy(static entry => entry.Page))
            {
                if (!table.TryGetValue(entry.Key, out List<Group>? records)) table.Add(entry.Key, records = []);
                records.EnsureCapacity(checked(records.Count + entry.Count()));
            }
        }
    }

    internal void Register(Group[] groups)
    {
        _groupCount += groups.Length;
        foreach (Group group in groups)
        {
            MeshClusterGroup data = group.Data;
            for (uint page = data.FirstPage; page < data.FirstPage + data.PageCount; page++)
                _groupsByPage[page + group.FirstPage].Add(group);
            foreach (uint page in data.Parents.Select(static parent => parent.PageIndex).Distinct())
                _groupsByParentPage[page + group.FirstPage].Add(group);
            _changedGroups.Add(group);
        }
    }

    internal void PageChanged(uint page)
    {
        if (_groupsByPage.TryGetValue(page, out List<Group>? groups)) _changedGroups.UnionWith(groups);
        if (_groupsByParentPage.TryGetValue(page, out List<Group>? parents)) _changedGroups.UnionWith(parents);
    }

    internal void ExpandDependencies(Dictionary<uint, uint> priorities)
    {
        var queue = new Queue<uint>(priorities.Keys);
        var visited = new HashSet<uint>();
        while (queue.TryDequeue(out uint page))
        {
            if (!visited.Add(page) || !_groupsByPage.TryGetValue(page, out List<Group>? groups)) continue;
            uint priority = priorities[page];
            foreach (Group group in groups)
            {
                for (uint dependency = group.Data.FirstPage; dependency < group.Data.FirstPage + group.Data.PageCount; dependency++)
                    Include(dependency + group.FirstPage);
                foreach (MeshClusterReference parent in group.Data.Parents) Include(parent.PageIndex + group.FirstPage);
            }

            void Include(uint dependency)
            {
                if (!priorities.TryGetValue(dependency, out uint previous))
                { priorities.Add(dependency, priority); queue.Enqueue(dependency); }
                else if (priority > previous)
                { priorities[dependency] = priority; visited.Remove(dependency); queue.Enqueue(dependency); }
            }
        }
    }

    internal void ExpandDependencies(HashSet<uint> pages)
    {
        var priorities = pages.ToDictionary(static page => page, static _ => 0u);
        ExpandDependencies(priorities);
        pages.UnionWith(priorities.Keys);
    }

    internal bool HasResidentDependents(uint page, Func<uint, bool> available)
    {
        if (!_groupsByParentPage.TryGetValue(page, out List<Group>? groups)) return false;
        foreach (Group group in groups)
            for (uint child = group.Data.FirstPage; child < group.Data.FirstPage + group.Data.PageCount; child++)
                if (child + group.FirstPage != page && available(child + group.FirstPage)) return true;
        return false;
    }

    internal Batch Apply(
        Func<uint, bool> available, Func<uint, uint> pageOffset, ClusterBvh hierarchy)
    {
        var flags = new List<ClusterLeafFlagFixup>();
        var changes = new List<ClusterLodChange>();
        var changedParts = new Dictionary<Part, Group>();
        var groupStates = new Dictionary<Group, bool>();
        foreach (Group group in _changedGroups)
        {
            bool complete = true;
            for (uint page = group.Data.FirstPage; page < group.Data.FirstPage + group.Data.PageCount; page++)
                complete &= available(page + group.FirstPage);
            foreach (MeshClusterReference parent in group.Data.Parents) complete &= available(parent.PageIndex + group.FirstPage);
            foreach (MeshHierarchyPart part in group.Data.HierarchyParts)
            {
                uint page = part.PageIndex + group.FirstPage;
                uint reference = complete ? ((pageOffset(page) >> 9) << 8) | part.FirstCluster : ClusterBvh.PageFaultMarker;
                hierarchy.PatchPart(group.FirstNode + part.NodeIndex, part.ChildIndex, reference);
            }
            foreach (MeshClusterReference parent in group.Data.Parents)
            {
                uint page = parent.PageIndex + group.FirstPage;
                if (!available(page)) continue;
                uint address = pageOffset(page) + MeshPageHeader.Size + parent.ClusterIndex * PackedCluster.SizeInBytes;
                flags.Add(new ClusterLeafFlagFixup(address, complete ? 0u : 1u));
            }
            if (complete != group.Complete)
            {
                changes.Add(new ClusterLodChange(group.Mesh, group.Root, group.Bounds,
                    group.FirstPage + group.Data.FirstPage, group.Data.PageCount));
            }
            groupStates.Add(group, complete);
            foreach (Part part in group.Parts) changedParts[part] = group;
            foreach (Part part in group.ParentParts) changedParts[part] = group;
        }
        foreach ((Part part, Group group) in changedParts)
        {
            uint errors = part.Errors;
            if (part.ChildGroups.Any(child => !(groupStates.TryGetValue(child, out bool complete) ? complete : child.Complete))) errors |= 0x80000000u;
            hierarchy.PatchPartErrors(group.FirstNode + part.Data.NodeIndex, part.Data.ChildIndex, errors);
        }
        return new Batch(flags.ToArray(), changes.ToArray(), groupStates);
    }

    internal void Accept(Batch batch)
    {
        foreach ((Group group, bool complete) in batch.GroupStates) group.Complete = complete;
        _changedGroups.Clear();
    }

    internal void Clear()
    {
        _groupsByPage.Clear(); _groupsByParentPage.Clear(); _changedGroups.Clear(); _groupCount = 0;
    }
}
