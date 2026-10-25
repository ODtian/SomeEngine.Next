using System.Collections.Immutable;
using System.Numerics;

namespace SomeEngine.Resources.Geometry;

public static class ClusterHierarchyBuilder
{
    /// <summary>Builds the shared four-child hierarchy and affected-page fixup metadata.</summary>
    public static ClusterHierarchy Build(IReadOnlyList<ClusterHierarchyCluster> clusters)
    {
        ArgumentNullException.ThrowIfNull(clusters);
        if (clusters.Count == 0) return new([], [], []);
        List<ClusterBVHNode> nodes = BuildNodes(clusters);
        MeshClusterGroup[] groups = BuildGroups(clusters, nodes);
        var basePages = new HashSet<uint>();
        foreach (MeshClusterGroup group in groups)
        {
            if (!group.Parents.IsEmpty) continue;
            for (uint page = group.FirstPage; page < group.FirstPage + group.PageCount; page++)
                basePages.Add(page);
        }
        return new(nodes.ToArray(), groups, basePages.Order().ToArray());
    }

    // A root has no finer/coarser handoff with another group. Its pages can be
    // installed independently, including large Realtime meshes without LODs.
    // Reduction groups retain their complete common page dependency range.
    private static (int Group, uint RootPage) GroupKey(ClusterHierarchyCluster cluster)
        => (cluster.ParentGroupId, cluster.ParentGroupId < 0 ? cluster.PageIndex : 0u);

    private sealed class HierarchyBranch
    {
        internal ClusterHierarchySlice Slice;
        internal HierarchyBranch[] Children = [];
        internal int Level;
    }

    private static List<ClusterBVHNode> BuildNodes(IReadOnlyList<ClusterHierarchyCluster> clusters)
    {
        var pageRanges = clusters.GroupBy(static cluster => GroupKey(cluster)).ToDictionary(
            static group => group.Key,
            static group => (First: group.Min(static cluster => cluster.PageIndex), Last: group.Max(static cluster => cluster.PageIndex)));
        var parts = new List<HierarchyBranch>();
        for (int first = 0; first < clusters.Count;)
        {
            ClusterHierarchyCluster initial = clusters[first];
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
            var range = pageRanges[GroupKey(initial)];
            if (range.Last - range.First + 1 > 31)
                throw new InvalidDataException("A cluster group exceeds the Native page range count.");
            EncodeHierarchyBounds(min, max, out Vector3 center, out Vector3 extent);
            var slice = new ClusterHierarchySlice
            {
                LODBounds = initial.LODSphere,
                BoundsCenter = center,
                BoundsExtent = extent,
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

    private static MeshClusterGroup[] BuildGroups(IReadOnlyList<ClusterHierarchyCluster> clusters, List<ClusterBVHNode> nodes)
    {
        var groups = new Dictionary<(int Group, uint RootPage), List<MeshHierarchyPart>>();
        var clusterGroups = clusters.ToDictionary(static cluster => (cluster.PageIndex, cluster.ClusterStart), static cluster => GroupKey(cluster));
        for (int node = 0; node < nodes.Count; node++)
            for (int child = 0; child < 4; child++)
            {
                ClusterHierarchySlice slice = nodes[node].GetChild(child);
                if (!slice.IsEnabled || !slice.IsLeaf) continue;
                uint page = slice.ChildReference >> 8;
                var group = clusterGroups[(page, slice.FirstCluster)];
                if (!groups.TryGetValue(group, out List<MeshHierarchyPart>? parts)) groups.Add(group, parts = []);
                parts.Add(new MeshHierarchyPart { NodeIndex = (uint)node, ChildIndex = (uint)child,
                    PageIndex = page, FirstCluster = slice.FirstCluster });
            }
        return groups.OrderBy(static group => group.Key.Group).ThenBy(static group => group.Key.RootPage).Select(group =>
        {
            ClusterHierarchyCluster[] children = clusters.Where(cluster => GroupKey(cluster) == group.Key).ToArray();
            uint first = children.Min(static cluster => cluster.PageIndex), last = children.Max(static cluster => cluster.PageIndex);
            return new MeshClusterGroup { FirstPage = first, PageCount = last - first + 1,
                HierarchyParts = group.Value.ToImmutableArray(),
                Parents = clusters.Where(cluster => group.Key.Group >= 0 && cluster.GroupId == group.Key.Group).Select(static cluster =>
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
        Vector4 sphere = MergeLodSpheres(children.Select(static child => child.Slice.LODBounds).ToArray());
        float minError = float.MaxValue, maxError = 0;
        foreach (HierarchyBranch child in children)
        {
            ClusterHierarchySlice value = child.Slice;
            min = Vector3.Min(min, value.BoundsCenter - value.BoundsExtent);
            max = Vector3.Max(max, value.BoundsCenter + value.BoundsExtent);
            minError = Math.Min(minError, value.MinLODError);
            maxError = Math.Max(maxError, value.MaxParentLODError);
        }
        EncodeHierarchyBounds(min, max, out Vector3 center, out Vector3 extent);
        var result = new HierarchyBranch { Children = children, Slice = new ClusterHierarchySlice
        {
            LODBounds = sphere, BoundsCenter = center, BoundsExtent = extent,
            PageRangeKey = uint.MaxValue, GroupPartData = (511u << 16) | 0xFFFFu,
        } };
        result.Slice.SetErrors(minError, maxError);
        return result;
    }

    // The stored float center may move away from the exact midpoint. Compute the
    // radius from that stored center, then round outward so reconstructing either
    // endpoint in float cannot lose a source cluster or a child hierarchy bound.
    private static void EncodeHierarchyBounds(Vector3 min, Vector3 max,
        out Vector3 center, out Vector3 extent)
    {
        center = new(
            (float)(((double)min.X + max.X) * .5),
            (float)(((double)min.Y + max.Y) * .5),
            (float)(((double)min.Z + max.Z) * .5));
        extent = new(
            Radius(min.X, max.X, center.X),
            Radius(min.Y, max.Y, center.Y),
            Radius(min.Z, max.Z, center.Z));

        static float Radius(float low, float high, float center)
        {
            double required = Math.Max((double)center - low, (double)high - center);
            float rounded = (float)required;
            if (rounded < required) rounded = MathF.BitIncrement(rounded);
            // A very small endpoint can disappear even in the double subtraction.
            // Verify the actual stored-float reconstruction as the final contract.
            if (center - rounded > low || center + rounded < high)
                rounded = MathF.BitIncrement(rounded);
            return rounded;
        }
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

    private static Vector4 MergeLodSpheres(ReadOnlySpan<Vector4> spheres)
    {
        int[] minimum = [0, 0, 0], maximum = [0, 0, 0];
        for (int sphere = 0; sphere < spheres.Length; sphere++)
            for (int axis = 0; axis < 3; axis++)
            {
                if (spheres[sphere][axis] - spheres[sphere].W < spheres[minimum[axis]][axis] - spheres[minimum[axis]].W) minimum[axis] = sphere;
                if (spheres[sphere][axis] + spheres[sphere].W > spheres[maximum[axis]][axis] + spheres[maximum[axis]].W) maximum[axis] = sphere;
            }
        float largest = 0;
        int largestAxis = 0;
        for (int axis = 0; axis < 3; axis++)
        {
            Vector4 first = spheres[minimum[axis]], second = spheres[maximum[axis]];
            float distance = Vector3.Distance(Center(first), Center(second)) + first.W + second.W;
            if (distance > largest) { largest = distance; largestAxis = axis; }
        }
        Vector4 result = Merge(spheres[minimum[largestAxis]], spheres[maximum[largestAxis]]);
        foreach (Vector4 sphere in spheres) result = Merge(result, sphere);
        return result;
    }

    private static Vector4 Merge(Vector4 first, Vector4 second)
    {
        if (first.W == 0) return second;
        Vector3 offset = Center(second) - Center(first);
        float squared = offset.LengthSquared();
        float difference = first.W - second.W;
        if (difference * difference + 1e-4f >= squared) return first.W < second.W ? second : first;
        float distance = MathF.Sqrt(squared), radius = (distance + first.W + second.W) * .5f;
        Vector3 center = Center(first);
        if (distance > 1e-8f) center += offset * ((radius - first.W) / distance);
        return new(center, radius);
    }
    private static Vector3 Center(Vector4 sphere) => new(sphere.X, sphere.Y, sphere.Z);
}

/// <summary>One draw cluster's bounds, LOD relationship and location in geometry pages.</summary>
public readonly record struct ClusterHierarchyCluster(
    Vector3 BoundMin, Vector3 BoundMax, Vector4 LODSphere,
    float LODError, float OwnError, uint PageIndex, uint ClusterStart,
    int GroupId, int ParentGroupId, int Level);

/// <summary>Cooked nodes and page dependencies shared by all Cluster geometry producers.</summary>
public readonly record struct ClusterHierarchy(
    ClusterBVHNode[] Nodes, MeshClusterGroup[] Groups, uint[] LowestDetailPages);
