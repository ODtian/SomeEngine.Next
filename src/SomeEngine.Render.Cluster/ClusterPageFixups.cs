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
        internal PageDependency Dependency = null!;
    }

    internal sealed class PageDependency((uint First, uint End)[] ranges)
    {
        internal readonly (uint First, uint End)[] Ranges = ranges;
        internal uint FirstPage => Ranges[0].First;
        internal uint EndPage
        {
            get => Ranges[^1].End;
            set => Ranges[^1] = (Ranges[^1].First, value);
        }
        internal PageDependency[] Parents = [];
        internal int Order;
        internal uint DemandPriority;
    }

    private PageDependency?[] _dependenciesByPage = [];
    private readonly List<PageDependency> _dependencies = [];
    private ulong[] _demandWords = [];
    private int[] _demandPositions = [];
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
        BuildPageDependencies(groups);
        int pageCount = checked((int)(registration.FirstPage + registration.PageCount));
        if (_dependenciesByPage.Length < pageCount)
            Array.Resize(ref _dependenciesByPage, pageCount);
        Reserve(_groupsByPage, groups, static group => Enumerable.Range((int)group.Data.FirstPage, (int)group.Data.PageCount).Select(static page => (uint)page));
        Reserve(_groupsByParentPage, groups, static group => group.Data.Parents.Select(static parent => parent.PageIndex).Distinct());
        _changedGroups.EnsureCapacity(checked(_groupCount + groups.Count));
        int dependencyCapacity = checked(_dependencies.Count + groups.Count);
        _dependencies.EnsureCapacity(dependencyCapacity);
        int wordCount = (dependencyCapacity + 63) / 64;
        if (_demandWords.Length < wordCount)
            Array.Resize(ref _demandWords, Math.Max(wordCount, _demandWords.Length * 2));
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

    private static void BuildPageDependencies(List<Group> groups)
    {
        // Overlapping group ranges describe one indivisible set of physical pages.
        // Co-located parents cannot have a different physical load priority.
        var dependencies = new List<PageDependency>();
        foreach (Group group in groups.OrderBy(static group => group.Data.FirstPage))
        {
            uint first = group.FirstPage + group.Data.FirstPage;
            uint end = first + group.Data.PageCount;
            PageDependency dependency;
            if (dependencies.Count == 0 || first >= dependencies[^1].EndPage)
                dependencies.Add(dependency = new PageDependency([(first, end)]));
            else
            {
                dependency = dependencies[^1];
                dependency.EndPage = Math.Max(dependency.EndPage, end);
            }
            group.Dependency = dependency;
        }
        var byPage = new Dictionary<uint, PageDependency>();
        foreach (PageDependency dependency in dependencies)
            for (uint page = dependency.FirstPage; page < dependency.EndPage; page++)
                byPage.Add(page, dependency);
        foreach (var component in groups.GroupBy(static group => group.Dependency))
            component.Key.Parents = component.SelectMany(group => group.Data.Parents.Select(parent =>
                byPage[parent.PageIndex + group.FirstPage])).Where(parent => parent != component.Key).Distinct().ToArray();

        dependencies = CondenseParentCycles(groups, dependencies);

        // Cache child-before-parent order once at registration. Priority propagation
        // then visits every demanded component at most once, including shared parents.
        var childCounts = dependencies.ToDictionary(static dependency => dependency, static _ => 0);
        foreach (PageDependency dependency in dependencies)
            foreach (PageDependency parent in dependency.Parents) childCounts[parent]++;
        var ready = new Queue<PageDependency>(dependencies.Where(dependency => childCounts[dependency] == 0));
        int order = 0;
        while (ready.TryDequeue(out PageDependency? dependency))
        {
            dependency.Order = order++;
            foreach (PageDependency parent in dependency.Parents)
                if (--childCounts[parent] == 0) ready.Enqueue(parent);
        }
        if (order != dependencies.Count)
            throw new InvalidOperationException("Condensed Cluster page dependencies must be acyclic.");
    }

    private static List<PageDependency> CondenseParentCycles(List<Group> groups, List<PageDependency> dependencies)
    {
        // Different LODs can also share distinct pages whose parent edges form a
        // physical cycle, even though the cluster reduction hierarchy is acyclic.
        // Collapse those strongly connected sets once, without adding gap pages.
        var children = dependencies.ToDictionary(static dependency => dependency, static _ => new List<PageDependency>());
        foreach (PageDependency dependency in dependencies)
            foreach (PageDependency parent in dependency.Parents) children[parent].Add(dependency);
        var visited = new HashSet<PageDependency>();
        var finishOrder = new List<PageDependency>();
        var walk = new Stack<(PageDependency Dependency, bool Finish)>();
        foreach (PageDependency start in dependencies)
        {
            walk.Push((start, false));
            while (walk.TryPop(out var step))
            {
                if (step.Finish) finishOrder.Add(step.Dependency);
                else if (visited.Add(step.Dependency))
                {
                    walk.Push((step.Dependency, true));
                    foreach (PageDependency parent in step.Dependency.Parents) walk.Push((parent, false));
                }
            }
        }

        visited.Clear();
        var components = new Dictionary<PageDependency, PageDependency>();
        var members = new List<PageDependency>();
        for (int index = finishOrder.Count - 1; index >= 0; index--)
        {
            PageDependency start = finishOrder[index];
            if (visited.Contains(start)) continue;
            members.Clear();
            walk.Push((start, false));
            while (walk.TryPop(out var step))
                if (visited.Add(step.Dependency))
                {
                    members.Add(step.Dependency);
                    foreach (PageDependency child in children[step.Dependency]) walk.Push((child, false));
                }
            PageDependency component = members.Count == 1 ? members[0] :
                new PageDependency([.. members.SelectMany(static member => member.Ranges).OrderBy(static range => range.First)]);
            foreach (PageDependency member in members) components.Add(member, component);
        }
        var parentSets = dependencies.GroupBy(dependency => components[dependency]).ToDictionary(
            static group => group.Key,
            group => group.SelectMany(static member => member.Parents).Select(parent => components[parent])
                .Where(parent => parent != group.Key).Distinct().ToArray());
        foreach ((PageDependency component, PageDependency[] parents) in parentSets) component.Parents = parents;
        foreach (Group group in groups) group.Dependency = components[group.Dependency];
        return [.. parentSets.Keys];
    }

    internal void Register(Group[] groups)
    {
        // Mesh parent edges stay inside their registration. Appending each mesh in
        // child-before-parent order also gives the full epoch that same ordering.
        foreach (PageDependency dependency in groups.Select(static group => group.Dependency)
            .Distinct().OrderBy(static dependency => dependency.Order))
        {
            dependency.Order = _dependencies.Count;
            _dependencies.Add(dependency);
        }
        _groupCount += groups.Length;
        foreach (Group group in groups)
        {
            MeshClusterGroup data = group.Data;
            for (uint page = data.FirstPage; page < data.FirstPage + data.PageCount; page++)
            {
                _groupsByPage[page + group.FirstPage].Add(group);
                _dependenciesByPage[page + group.FirstPage] = group.Dependency;
            }
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
        // The mesh gate serializes expansion. The bitset contains only this call's
        // demand; topology is already ordered, so no per-frame heap sort is needed.
        int firstWord = _demandWords.Length;
        int lastWord = -1;
        try
        {
            foreach ((uint page, uint priority) in priorities)
                if (FindPageDependency(page) is { } dependency)
                    Include(dependency, priority);
            for (int word = firstWord; word <= lastWord; word++)
                while (_demandWords[word] != 0)
                {
                    ulong bits = _demandWords[word];
                    int index = word * 64 + BitOperations.TrailingZeroCount(bits);
                    _demandWords[word] = bits & (bits - 1);
                    PageDependency dependency = _dependencies[index];
                    uint priority = dependency.DemandPriority;
                    foreach ((uint first, uint end) in dependency.Ranges)
                        for (uint page = first; page < end; page++) priorities[page] = priority;
                    uint parentPriority = priority == uint.MaxValue ? priority : priority + 1;
                    foreach (PageDependency parent in dependency.Parents) Include(parent, parentPriority);
                }
        }
        finally
        {
            if (lastWord >= firstWord)
                Array.Clear(_demandWords, firstWord, lastWord - firstWord + 1);
        }

        void Include(PageDependency dependency, uint priority)
        {
            int word = dependency.Order / 64;
            ulong bit = 1UL << (dependency.Order & 63);
            if ((_demandWords[word] & bit) == 0)
            {
                _demandWords[word] |= bit;
                dependency.DemandPriority = priority;
                firstWord = Math.Min(firstWord, word);
                lastWord = Math.Max(lastWord, word);
            }
            else if (priority > dependency.DemandPriority) dependency.DemandPriority = priority;
        }
    }

    internal bool TryExpandDependencies(
        ReadOnlySpan<ClusterPageDemand> demands,
        int pageCount,
        List<ClusterPageDemand> expanded)
    {
        // Only positions are shared scratch. The coordinator keeps the ordered output
        // after releasing the mesh gate, including across another stream's expansion.
        CollectionsMarshal.SetCount(expanded, pageCount);
        Span<ClusterPageDemand> output = CollectionsMarshal.AsSpan(expanded);
        int count = 0;
        if (_demandPositions.Length < pageCount)
            Array.Resize(ref _demandPositions, pageCount);
        int firstWord = _demandWords.Length;
        int lastWord = -1;
        bool complete = false;
        try
        {
            foreach (ClusterPageDemand demand in demands)
            {
                if (demand.PageId >= (uint)pageCount) return false;
                int position = _demandPositions[demand.PageId];
                if (position == 0)
                {
                    output[count++] = demand;
                    _demandPositions[demand.PageId] = count;
                }
                else if (demand.Priority > output[position - 1].Priority)
                    output[position - 1] = demand;
                if (FindPageDependency(demand.PageId) is { } dependency)
                    Include(dependency, demand.Priority);
            }
            for (int word = firstWord; word <= lastWord; word++)
                while (_demandWords[word] != 0)
                {
                    ulong bits = _demandWords[word];
                    int index = word * 64 + BitOperations.TrailingZeroCount(bits);
                    _demandWords[word] = bits & (bits - 1);
                    PageDependency dependency = _dependencies[index];
                    uint priority = dependency.DemandPriority;
                    foreach ((uint first, uint end) in dependency.Ranges)
                        for (uint page = first; page < end; page++)
                        {
                            int position = _demandPositions[page];
                            var demand = new ClusterPageDemand(page, priority);
                            if (position == 0)
                            {
                                output[count++] = demand;
                                _demandPositions[page] = count;
                            }
                            else output[position - 1] = demand;
                        }
                    uint parentPriority = priority == uint.MaxValue ? priority : priority + 1;
                    foreach (PageDependency parent in dependency.Parents) Include(parent, parentPriority);
                }
            complete = true;
            return true;
        }
        finally
        {
            foreach (ClusterPageDemand demand in output[..count]) _demandPositions[demand.PageId] = 0;
            if (lastWord >= firstWord)
                Array.Clear(_demandWords, firstWord, lastWord - firstWord + 1);
            CollectionsMarshal.SetCount(expanded, complete ? count : 0);
        }

        void Include(PageDependency dependency, uint priority)
        {
            int word = dependency.Order / 64;
            ulong bit = 1UL << (dependency.Order & 63);
            if ((_demandWords[word] & bit) == 0)
            {
                _demandWords[word] |= bit;
                dependency.DemandPriority = priority;
                firstWord = Math.Min(firstWord, word);
                lastWord = Math.Max(lastWord, word);
            }
            else if (priority > dependency.DemandPriority) dependency.DemandPriority = priority;
        }
    }

    internal void ExpandDependencies(HashSet<uint> pages)
    {
        var priorities = pages.ToDictionary(static page => page, static _ => 0u);
        ExpandDependencies(priorities);
        pages.UnionWith(priorities.Keys);
    }

    internal PageDependency? FindPageDependency(uint page)
        => page < (uint)_dependenciesByPage.Length ? _dependenciesByPage[page] : null;

    internal bool HasResidentDependents(uint page, Func<uint, bool> available,
        PageDependency? evicting = null)
    {
        if (!_groupsByParentPage.TryGetValue(page, out List<Group>? groups)) return false;
        foreach (Group group in groups)
        {
            if (group.Dependency == evicting) continue;
            for (uint child = group.Data.FirstPage; child < group.Data.FirstPage + group.Data.PageCount; child++)
                if (child + group.FirstPage != page && available(child + group.FirstPage)) return true;
        }
        return false;
    }

    internal Batch Apply(
        Func<uint, bool> available, Func<uint, uint> pageOffset, ClusterBvh hierarchy, ReadOnlySpan<uint> installedPages)
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
                if (!available(page) || complete == group.Complete && installedPages.BinarySearch(page) < 0) continue;
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

    internal void RemoveMesh(uint firstPage, uint pageCount)
    {
        var groups = new HashSet<Group>();
        for (uint page = firstPage; page < firstPage + pageCount; page++)
        {
            if (_groupsByPage.Remove(page, out List<Group>? children)) groups.UnionWith(children);
            _groupsByParentPage.Remove(page);
        }
        HashSet<PageDependency> dependencies = groups.Select(static group => group.Dependency).ToHashSet();
        foreach (Group group in groups) _changedGroups.Remove(group);
        Array.Clear(_dependenciesByPage, (int)firstPage, (int)pageCount);
        _dependencies.RemoveAll(dependencies.Contains);
        for (int index = 0; index < _dependencies.Count; index++) _dependencies[index].Order = index;
        _groupCount -= groups.Count;
    }

    internal void Clear()
    {
        Array.Clear(_dependenciesByPage); _groupsByPage.Clear(); _groupsByParentPage.Clear(); _changedGroups.Clear(); _groupCount = 0;
        _dependencies.Clear();
        Array.Clear(_demandWords);
        Array.Clear(_demandPositions);
    }
}
