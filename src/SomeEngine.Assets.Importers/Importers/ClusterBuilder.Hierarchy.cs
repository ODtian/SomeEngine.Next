using System.Collections.Immutable;
using System.Numerics;
using SomeEngine.Resources.Geometry;

namespace SomeEngine.Assets.Importers;

public static partial class ClusterBuilder
{
    private sealed class HierarchyBranch
    {
        internal ClusterHierarchySlice Slice;
        internal HierarchyBranch[] Children = [];
        internal int Level;
    }

    private static List<ClusterBVHNode> BuildBvh(List<ClusterInfo> clusters)
    {
        var pageRanges = clusters.GroupBy(static cluster => cluster.ParentGroupId).ToDictionary(
            static group => group.Key,
            static group => (First: group.Min(static cluster => cluster.PageIndex), Last: group.Max(static cluster => cluster.PageIndex)));
        var parts = new List<HierarchyBranch>();
        for (int first = 0; first < clusters.Count;)
        {
            ClusterInfo initial = clusters[first];
            int end = first + 1;
            while (end < clusters.Count && clusters[end].PageIndex == initial.PageIndex &&
                clusters[end].ParentGroupId == initial.ParentGroupId && clusters[end].Level == initial.Level && end - first < 511) end++;
            Vector3 min = initial.BoundMin, max = initial.BoundMax;
            float minError = initial.OwnError;
            for (int index = first + 1; index < end; index++)
            {
                min = Vector3.Min(min, clusters[index].BoundMin);
                max = Vector3.Max(max, clusters[index].BoundMax);
                minError = Math.Min(minError, clusters[index].OwnError);
            }
            var range = pageRanges[initial.ParentGroupId];
            var slice = new ClusterHierarchySlice
            {
                LODBounds = initial.LODSphere,
                BoundsCenter = (min + max) * .5f,
                BoundsExtent = (max - min) * .5f,
                ChildReference = (initial.PageIndex << 8) | initial.ClusterStart,
                PageRangeKey = (range.First << 5) | (range.Last - range.First + 1) | (range.Last >= 1 ? 0x40000000u : 0u),
                GroupPartData = ((uint)(end - first) << 16) | 0xFFFFu,
            };
            slice.SetErrors(minError, initial.LODError);
            parts.Add(new HierarchyBranch { Slice = slice, Level = initial.Level });
            first = end;
        }
        HierarchyBranch[] levels = parts.GroupBy(static part => part.Level).OrderBy(static group => group.Key)
            .Select(static group => BuildHierarchyTree(group.ToArray(), true)).ToArray();
        HierarchyBranch root = BuildHierarchyLodRoots(levels, 2);
        if (root.Children.Length == 0) root = CombineHierarchy([root]);
        var nodes = new List<ClusterBVHNode>();
        EncodeHierarchy(root, nodes);
        return nodes;
    }

    private static MeshClusterGroup[] BuildMeshGroups(List<ClusterInfo> clusters, List<ClusterBVHNode> nodes)
    {
        var groups = new Dictionary<int, List<MeshHierarchyPart>>();
        var clusterGroups = clusters.ToDictionary(static cluster => (cluster.PageIndex, cluster.ClusterStart), static cluster => cluster.ParentGroupId);
        for (int node = 0; node < nodes.Count; node++)
            for (int child = 0; child < 4; child++)
            {
                ClusterHierarchySlice slice = nodes[node].GetChild(child);
                if (!slice.IsEnabled || !slice.IsLeaf) continue;
                uint page = slice.ChildReference >> 8;
                int group = clusterGroups[(page, slice.FirstCluster)];
                if (!groups.TryGetValue(group, out List<MeshHierarchyPart>? parts)) groups.Add(group, parts = []);
                parts.Add(new MeshHierarchyPart { NodeIndex = (uint)node, ChildIndex = (uint)child,
                    PageIndex = page, FirstCluster = slice.FirstCluster });
            }
        return groups.OrderBy(static group => group.Key).Select(group =>
        {
            ClusterInfo[] children = clusters.Where(cluster => cluster.ParentGroupId == group.Key).ToArray();
            uint first = children.Min(static cluster => cluster.PageIndex), last = children.Max(static cluster => cluster.PageIndex);
            return new MeshClusterGroup { FirstPage = first, PageCount = last - first + 1,
                HierarchyParts = group.Value.ToImmutableArray(),
                Parents = clusters.Where(cluster => group.Key >= 0 && cluster.GroupId == group.Key).Select(static cluster =>
                    new MeshClusterReference { PageIndex = cluster.PageIndex, ClusterIndex = cluster.ClusterStart }).ToImmutableArray() };
        }).ToArray();
    }

    private static HierarchyBranch BuildHierarchyLodRoots(HierarchyBranch[] levels, int chainLevels)
    {
        if (levels.Length <= 4) return levels.Length == 1 ? levels[0] : CombineHierarchy(levels);
        if (chainLevels == 0) return BuildHierarchyTree(levels, false);
        return CombineHierarchy([.. levels[^3..], BuildHierarchyLodRoots(levels[..^3], chainLevels - 1)]);
    }

    // NaniteEncodeHierarchy::BuildHierarchyTopDown: complete four-way tree, one incomplete level.
    private static HierarchyBranch BuildHierarchyTree(HierarchyBranch[] parts, bool sort)
    {
        if (parts.Length == 1) return parts[0];
        if (parts.Length <= 4) return CombineHierarchy(parts);
        int topSize = 4;
        while (topSize <= parts.Length / 4) topSize *= 4;
        int small = topSize / 4, excess = parts.Length - topSize;
        int[] sizes = new int[4];
        for (int child = 3; child >= 0; child--)
        {
            int addition = Math.Min(excess, topSize - small);
            sizes[child] = small + addition;
            excess -= addition;
        }
        if (sort) SortHierarchyParts(parts, sizes);
        var children = new HierarchyBranch[4];
        for (int child = 0, offset = 0; child < 4; offset += sizes[child++])
            children[child] = BuildHierarchyTree(parts.AsSpan(offset, sizes[child]).ToArray(), sort);
        return CombineHierarchy(children);
    }

    private static void SortHierarchyParts(HierarchyBranch[] parts, int[] sizes)
    {
        for (int level = 0; level < 2; level++)
        {
            int childrenPerBucket = 4 >> level, half = childrenPerBucket >> 1, offset = 0;
            for (int bucket = 0; bucket < 1 << level; bucket++)
            {
                int firstSize = 0, secondSize = 0;
                for (int child = 0; child < half; child++)
                {
                    firstSize += sizes[bucket * childrenPerBucket + child];
                    secondSize += sizes[bucket * childrenPerBucket + child + half];
                }
                float bestCost = float.MaxValue;
                int bestAxis = 0;
                for (int axis = 0; axis < 3; axis++)
                {
                    SortAxis(axis);
                    float cost = HierarchySurfaceArea(parts.AsSpan(offset, firstSize)) +
                        HierarchySurfaceArea(parts.AsSpan(offset + firstSize, secondSize));
                    if (cost < bestCost) { bestCost = cost; bestAxis = axis; }
                }
                if (bestAxis != 2) SortAxis(bestAxis);
                offset += firstSize + secondSize;

                void SortAxis(int axis) => Array.Sort(parts, offset, firstSize + secondSize,
                    Comparer<HierarchyBranch>.Create((a, b) => a.Slice.BoundsCenter[axis].CompareTo(b.Slice.BoundsCenter[axis])));
            }
        }
    }

    private static float HierarchySurfaceArea(ReadOnlySpan<HierarchyBranch> children)
    {
        Vector3 min = new(float.MaxValue), max = new(float.MinValue);
        foreach (HierarchyBranch child in children)
        {
            min = Vector3.Min(min, child.Slice.BoundsCenter - child.Slice.BoundsExtent);
            max = Vector3.Max(max, child.Slice.BoundsCenter + child.Slice.BoundsExtent);
        }
        Vector3 size = max - min;
        return 2 * (size.X * size.Y + size.Y * size.Z + size.Z * size.X);
    }

    private static HierarchyBranch CombineHierarchy(HierarchyBranch[] children)
    {
        Vector3 min = new(float.MaxValue), max = new(float.MinValue);
        Vector4 sphere = children[0].Slice.LODBounds;
        float minError = float.MaxValue, maxError = 0;
        foreach (HierarchyBranch child in children)
        {
            ClusterHierarchySlice value = child.Slice;
            min = Vector3.Min(min, value.BoundsCenter - value.BoundsExtent);
            max = Vector3.Max(max, value.BoundsCenter + value.BoundsExtent);
            sphere = MergeHierarchySpheres(sphere, value.LODBounds);
            minError = Math.Min(minError, value.MinLODError);
            maxError = Math.Max(maxError, value.MaxParentLODError);
        }
        var result = new HierarchyBranch { Children = children, Slice = new ClusterHierarchySlice
        {
            LODBounds = sphere, BoundsCenter = (min + max) * .5f, BoundsExtent = (max - min) * .5f,
            PageRangeKey = uint.MaxValue, GroupPartData = (511u << 16) | 0xFFFFu,
        } };
        result.Slice.SetErrors(minError, maxError);
        return result;
    }

    private static Vector4 MergeHierarchySpheres(Vector4 first, Vector4 second)
    {
        Vector3 a = new(first.X, first.Y, first.Z), b = new(second.X, second.Y, second.Z);
        float distance = Vector3.Distance(a, b);
        if (first.W >= distance + second.W) return first;
        if (second.W >= distance + first.W) return second;
        float radius = (distance + first.W + second.W) * .5f;
        return new(a + (b - a) * ((radius - first.W) / Math.Max(distance, 1e-20f)), radius);
    }

    private static uint EncodeHierarchy(HierarchyBranch branch, List<ClusterBVHNode> nodes)
    {
        uint index = checked((uint)nodes.Count);
        nodes.Add(default);
        var node = new ClusterBVHNode();
        for (int child = 0; child < branch.Children.Length; child++)
        {
            HierarchyBranch next = branch.Children[child];
            ClusterHierarchySlice slice = next.Slice;
            if (next.Children.Length != 0) slice.ChildReference = EncodeHierarchy(next, nodes);
            node.SetChild(child, slice);
        }
        nodes[checked((int)index)] = node;
        return index;
    }
}
