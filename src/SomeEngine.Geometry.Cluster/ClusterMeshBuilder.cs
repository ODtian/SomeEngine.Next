using System.Buffers;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using MeshOptimizer;
using SomeEngine.Resources;
using SomeEngine.Resources.Geometry;

namespace SomeEngine.Geometry.Cluster;

public struct ClusterLodConfig
{
    public int MaxVertices;
    public int MinTriangles;
    public int MaxTriangles;
    public bool PartitionSpatial;
    public bool PartitionSort;
    public int PartitionSize;
    public bool ClusterSpatial;
    public float ClusterFillWeight;
    public float ClusterSplitFactor;
    public float SimplifyRatio;
    public float SimplifyThreshold;
    public float SimplifyErrorFactorSloppy;
    public float SimplifyErrorEdgeLimit;
    public bool SimplifyPermissive;
    public bool SimplifyFallbackPermissive;
    public bool SimplifyFallbackSloppy;
    public bool SimplifyRegularize;
    public bool OptimizeBounds;
    public bool OptimizeClusters;

    public static ClusterLodConfig GetDefault(int maxTriangles = 124)
    {
        return new ClusterLodConfig
        {
            MaxVertices = 64,
            MinTriangles = maxTriangles / 3,
            MaxTriangles = maxTriangles,
            PartitionSpatial = true,
            PartitionSort = false,
            PartitionSize = 16,
            ClusterSpatial = false,
            ClusterFillWeight = 0.5f,
            ClusterSplitFactor = 2.0f,
            SimplifyRatio = 0.5f,
            SimplifyThreshold = 0.85f,
            SimplifyErrorFactorSloppy = 2.0f,
            SimplifyErrorEdgeLimit = 0.0f,
            SimplifyPermissive = true,
            SimplifyFallbackPermissive = false,
            SimplifyFallbackSloppy = true,
            SimplifyRegularize = false,
            OptimizeBounds = true,
            OptimizeClusters = true,
        };
    }
}

public static partial class ClusterMeshBuilder
{
    private const int MaxVerticesPerMeshlet = 64;
    private const int MaxTrianglesPerMeshlet = 124;
    private const float ConeWeight = 0.0f;
    private const int GroupSize = 4;
    private const float SimplifyRatio = 0.5f;
    private const int PageSize = 128 * 1024; // 128KB
    private const int PageHeaderSize = MeshPageHeader.Size;
    private const int MaxEncodedTriangleStart = ushort.MaxValue;

    private struct BuilderMeshlet
    {
        public int IndicesOffset;
        public int IndicesCount;
        public int Level;
        public float Error;
        public float ParentError;
        public int GroupId;
        public int ParentGroupId;
        public Vector3 Center;
        public float Radius;
        public Vector3 LodCenter;
        public float LodRadius;
        public Vector3 SelfLodCenter;
        public float SelfLodRadius;
        public int VertexCount;

        public byte Mat0;
        public byte Mat1;
        public byte Mat2;
        public byte Range0End;
        public byte Range1End;
        public uint VertReuseBatchWord0;
        public uint VertReuseBatchWord1;
        public uint VertReuseBatchWord2;
        public uint VertReuseBatchWord3;
    }

    private struct ClusterLodBounds
    {
        public Vector3 Center;
        public float Radius;
        public float Error;
    }

    private struct ClusterInfo
    {
        public Vector3 BoundMin;
        public Vector3 BoundMax;
        public Vector4 LODSphere; // xyz: center, w: radius
        public float LODError;
        public uint PageIndex;
        public uint ClusterStart;
        public int GroupId;
        public int ParentGroupId;
    }

    private readonly record struct BvhBounds(
        Vector3 Min,
        Vector3 Max,
        Vector3 Center,
        float Radius,
        float Error);

    private sealed class LodWorkspace
    {
        public List<int> GroupOffsets { get; } = [];
        public List<uint> MergedIndices { get; } = [];
        public List<uint> SimplifiedIndices { get; } = [];
    }

    // Morton Code Helpers
    private static uint ExpandBits(uint v)
    {
        v = (v * 0x00010001u) & 0xFF0000FFu;
        v = (v * 0x00000101u) & 0x0F00F00Fu;
        v = (v * 0x00000011u) & 0xC30C30C3u;
        v = (v * 0x00000005u) & 0x49249249u;
        return v;
    }

    private static uint Morton3D(Vector3 p)
    {
        p.X = Math.Min(Math.Max(p.X * 1024.0f, 0.0f), 1023.0f);
        p.Y = Math.Min(Math.Max(p.Y * 1024.0f, 0.0f), 1023.0f);
        p.Z = Math.Min(Math.Max(p.Z * 1024.0f, 0.0f), 1023.0f);
        return ExpandBits((uint)p.X) * 4 + ExpandBits((uint)p.Y) * 2 + ExpandBits((uint)p.Z);
    }

    private static List<ClusterBVHNode> BuildBvh(List<ClusterInfo> clusters)
    {
        var nodes = new List<ClusterBVHNode>();
        if (clusters.Count == 0)
        {
            return nodes;
        }

        List<int> currentLevelIndices = CreateBvhLeafNodes(clusters, nodes);
        AddBvhInternalLevels(nodes, currentLevelIndices);
        return nodes;
    }

    private static List<int> CreateBvhLeafNodes(
        List<ClusterInfo> clusters,
        List<ClusterBVHNode> nodes)
    {
        var currentLevelIndices = new List<int>();
        int clusterIndex = 0;
        while (clusterIndex < clusters.Count)
        {
            int count = CountLeafClusters(clusters, clusterIndex);
            nodes.Add(CreateBvhLeafNode(clusters, clusterIndex, count));
            currentLevelIndices.Add(nodes.Count - 1);
            clusterIndex += count;
        }

        return currentLevelIndices;
    }

    private static int CountLeafClusters(List<ClusterInfo> clusters, int start)
    {
        uint currentPage = clusters[start].PageIndex;
        int currentParent = clusters[start].ParentGroupId;
        int currentGroup = clusters[start].GroupId;
        int count = 0;
        while (start + count < clusters.Count
            && clusters[start + count].PageIndex == currentPage
            && clusters[start + count].ParentGroupId == currentParent
            && clusters[start + count].GroupId == currentGroup
            && count < 128)
        {
            count++;
        }

        return count;
    }

    private static ClusterBVHNode CreateBvhLeafNode(
        List<ClusterInfo> clusters,
        int start,
        int count)
    {
        BvhBounds bounds = ComputeLeafBounds(clusters, start, count);
        ClusterInfo first = clusters[start];
        var node = new ClusterBVHNode
        {
            BoundMin = new Vector4(bounds.Min, first.GroupId + 1),
            BoundMax = new Vector4(bounds.Max, first.ParentGroupId + 1),
            LODSphere = first.LODSphere,
            LODError = first.LODError,
            ChildPointer = first.PageIndex,
            NodeType = 1,
        };
        node.SetLeafData(first.ClusterStart, (uint)count);
        return node;
    }

    private static BvhBounds ComputeLeafBounds(
        List<ClusterInfo> clusters,
        int start,
        int count)
    {
        Vector3 min = new(float.MaxValue);
        Vector3 max = new(float.MinValue);
        for (int offset = 0; offset < count; offset++)
        {
            ClusterInfo cluster = clusters[start + offset];
            min = Vector3.Min(min, cluster.BoundMin);
            max = Vector3.Max(max, cluster.BoundMax);
        }

        return new BvhBounds(min, max, Vector3.Zero, 0, 0);
    }

    private static void AddBvhInternalLevels(
        List<ClusterBVHNode> nodes,
        List<int> currentLevelIndices)
    {
        while (currentLevelIndices.Count > 1)
        {
            var nextLevelIndices = new List<int>();
            for (int start = 0; start < currentLevelIndices.Count; start += 16)
            {
                int count = Math.Min(16, currentLevelIndices.Count - start);
                nodes.Add(CreateBvhInternalNode(nodes, currentLevelIndices, start, count));
                nextLevelIndices.Add(nodes.Count - 1);
            }

            currentLevelIndices = nextLevelIndices;
        }
    }

    private static ClusterBVHNode CreateBvhInternalNode(
        List<ClusterBVHNode> nodes,
        List<int> currentLevelIndices,
        int start,
        int count)
    {
        BvhBounds bounds = ComputeInternalBounds(nodes, currentLevelIndices, start, count);
        return new ClusterBVHNode
        {
            ChildPointer = (uint)currentLevelIndices[start],
            ChildCount = (uint)count,
            NodeType = 0,
            BoundMin = new Vector4(bounds.Min, 0),
            BoundMax = new Vector4(bounds.Max, 0),
            LODSphere = new Vector4(bounds.Center.X, bounds.Center.Y, bounds.Center.Z, bounds.Radius),
            LODError = bounds.Error,
        };
    }

    private static BvhBounds ComputeInternalBounds(
        List<ClusterBVHNode> nodes,
        List<int> currentLevelIndices,
        int start,
        int count)
    {
        Vector3 min = new(float.MaxValue);
        Vector3 max = new(float.MinValue);
        float maxError = 0;
        Vector3 centerSum = Vector3.Zero;
        for (int offset = 0; offset < count; offset++)
        {
            ClusterBVHNode child = nodes[currentLevelIndices[start + offset]];
            min = Vector3.Min(min, ToVector3(child.BoundMin));
            max = Vector3.Max(max, ToVector3(child.BoundMax));
            maxError = Math.Max(maxError, child.LODError);
            centerSum += ToVector3(child.LODSphere);
        }

        Vector3 center = centerSum / count;
        float radius = ComputeInternalLodRadius(nodes, currentLevelIndices, start, count, center);
        return new BvhBounds(min, max, center, radius, maxError);
    }

    private static float ComputeInternalLodRadius(
        List<ClusterBVHNode> nodes,
        List<int> currentLevelIndices,
        int start,
        int count,
        Vector3 center)
    {
        float radius = 0;
        for (int offset = 0; offset < count; offset++)
        {
            ClusterBVHNode child = nodes[currentLevelIndices[start + offset]];
            float distance = Vector3.Distance(center, ToVector3(child.LODSphere)) + child.LODSphere.W;
            radius = Math.Max(radius, distance);
        }

        return radius;
    }

    private static Vector3 ToVector3(Vector4 value)
        => new(value.X, value.Y, value.Z);
}

public static partial class ClusterMeshBuilder
{
    private static void BuildClusterLod(
        ClusterLodConfig config,
        ReadOnlySpan<Vector3> positions,
        ReadOnlySpan<uint> indices,
        float[] materialIndicesArray,
        List<BuilderMeshlet> clusters,
        List<uint> globalIndices
    )
    {
        var locks = ArrayPool<byte>.Shared.Rent(positions.Length);
        var remap = ArrayPool<uint>.Shared.Rent(positions.Length);
        try
        {
            GeneratePositionRemap(positions, remap.AsSpan(0, positions.Length));
            Clusterize(
                config,
                indices,
                positions,
                materialIndicesArray,
                clusters,
                globalIndices);
            int nextGroupId = InitializeLeafClusters(positions, clusters, globalIndices);
            ReduceClusterLod(
                config,
                positions,
                materialIndicesArray,
                clusters,
                globalIndices,
                locks.AsSpan(0, positions.Length),
                remap.AsSpan(0, positions.Length),
                nextGroupId);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(locks);
            ArrayPool<uint>.Shared.Return(remap);
        }
    }

    private static void GeneratePositionRemap(
        ReadOnlySpan<Vector3> positions,
        Span<uint> remap)
    {
        Meshopt.GeneratePositionRemap(
            remap,
            MemoryMarshal.Cast<Vector3, float>(positions),
            (nuint)Unsafe.SizeOf<Vector3>());
    }

    private static int InitializeLeafClusters(
        ReadOnlySpan<Vector3> positions,
        List<BuilderMeshlet> clusters,
        List<uint> globalIndices)
    {
        int nextGroupId = 0;
        for (int index = 0; index < clusters.Count; index++)
        {
            BuilderMeshlet cluster = clusters[index];
            ReadOnlySpan<uint> clusterIndices = CollectionsMarshal
                .AsSpan(globalIndices)
                .Slice(cluster.IndicesOffset, cluster.IndicesCount);
            ClusterLodBounds bounds = BoundsCompute(positions, clusterIndices, 0);
            clusters[index] = InitializeLeafCluster(cluster, bounds, nextGroupId++);
        }

        return nextGroupId;
    }

    private static BuilderMeshlet InitializeLeafCluster(
        BuilderMeshlet cluster,
        ClusterLodBounds bounds,
        int groupId)
    {
        cluster.Center = bounds.Center;
        cluster.Radius = bounds.Radius;
        cluster.LodCenter = bounds.Center;
        cluster.LodRadius = bounds.Radius;
        cluster.SelfLodCenter = bounds.Center;
        cluster.SelfLodRadius = bounds.Radius;
        cluster.Error = 0;
        cluster.Level = 0;
        cluster.GroupId = groupId;
        return cluster;
    }

    private static void ReduceClusterLod(
        ClusterLodConfig config,
        ReadOnlySpan<Vector3> positions,
        float[] materialIndicesArray,
        List<BuilderMeshlet> clusters,
        List<uint> globalIndices,
        Span<byte> locks,
        ReadOnlySpan<uint> remap,
        int nextGroupId)
    {
        List<int> levelClusters = CreateLeafClusterIndices(clusters.Count);
        var workspace = new LodWorkspace();
        int depth = 0;
        while (levelClusters.Count > 1)
        {
            levelClusters = ReduceClusterLodDepth(
                config,
                positions,
                materialIndicesArray,
                clusters,
                globalIndices,
                locks,
                remap,
                levelClusters,
                workspace,
                ref nextGroupId,
                depth++);
        }

        AssignRootParentError(clusters, levelClusters);
    }

    private static List<int> CreateLeafClusterIndices(int clusterCount)
    {
        var levelClusters = new List<int>(clusterCount);
        for (int index = 0; index < clusterCount; index++)
        {
            levelClusters.Add(index);
        }

        return levelClusters;
    }

    private static List<int> ReduceClusterLodDepth(
        ClusterLodConfig config,
        ReadOnlySpan<Vector3> positions,
        float[] materialIndicesArray,
        List<BuilderMeshlet> clusters,
        List<uint> globalIndices,
        Span<byte> locks,
        ReadOnlySpan<uint> remap,
        List<int> levelClusters,
        LodWorkspace workspace,
        ref int nextGroupId,
        int depth)
    {
        Partition(config, positions, clusters, globalIndices, levelClusters, remap, workspace.GroupOffsets);
        LockBoundary(locks, clusters, globalIndices, levelClusters, workspace.GroupOffsets, remap);
        var nextLevelClusters = new List<int>();
        Span<int> levelClusterIndices = CollectionsMarshal.AsSpan(levelClusters);
        for (int groupIndex = 0; groupIndex < workspace.GroupOffsets.Count - 1; groupIndex++)
        {
            Span<int> group = GetClusterGroup(levelClusterIndices, workspace.GroupOffsets, groupIndex);
            ReduceClusterGroup(
                config,
                positions,
                materialIndicesArray,
                clusters,
                globalIndices,
                locks,
                group,
                workspace,
                nextLevelClusters,
                ref nextGroupId,
                depth);
        }

        return nextLevelClusters;
    }

    private static Span<int> GetClusterGroup(
        Span<int> levelClusters,
        List<int> groupOffsets,
        int groupIndex)
    {
        int start = groupOffsets[groupIndex];
        int count = groupOffsets[groupIndex + 1] - start;
        return levelClusters.Slice(start, count);
    }

    private static void ReduceClusterGroup(
        ClusterLodConfig config,
        ReadOnlySpan<Vector3> positions,
        float[] materialIndicesArray,
        List<BuilderMeshlet> clusters,
        List<uint> globalIndices,
        Span<byte> locks,
        Span<int> group,
        LodWorkspace workspace,
        List<int> nextLevelClusters,
        ref int nextGroupId,
        int depth)
    {
        MergeGroupIndices(globalIndices, clusters, group, workspace.MergedIndices);
        int targetSize = (int)((workspace.MergedIndices.Count / 3) * config.SimplifyRatio) * 3;
        ClusterLodBounds groupBounds = BoundsMerge(clusters, group);
        Simplify(config, positions, CollectionsMarshal.AsSpan(workspace.MergedIndices), locks, targetSize, out float error, workspace.SimplifiedIndices);
        if (workspace.SimplifiedIndices.Count > workspace.MergedIndices.Count * config.SimplifyThreshold)
        {
            MarkUnmergedGroup(clusters, group);
            return;
        }

        float groupError = groupBounds.Error + error;
        int groupId = nextGroupId++;
        AssignGroupParent(clusters, group, groupBounds, groupError, groupId);
        BuildParentClusters(
            config,
            positions,
            materialIndicesArray,
            clusters,
            globalIndices,
            workspace.SimplifiedIndices,
            groupBounds,
            groupError,
            groupId,
            depth,
            nextLevelClusters);
    }

    private static void MergeGroupIndices(
        List<uint> globalIndices,
        List<BuilderMeshlet> clusters,
        Span<int> group,
        List<uint> destination)
    {
        destination.Clear();
        Span<uint> globalSpan = CollectionsMarshal.AsSpan(globalIndices);
        foreach (int clusterIndex in group)
        {
            BuilderMeshlet cluster = clusters[clusterIndex];
            ReadOnlySpan<uint> clusterIndices = globalSpan.Slice(cluster.IndicesOffset, cluster.IndicesCount);
            for (int index = 0; index < clusterIndices.Length; index++)
            {
                destination.Add(clusterIndices[index]);
            }
        }
    }

    private static void MarkUnmergedGroup(List<BuilderMeshlet> clusters, Span<int> group)
    {
        foreach (int index in group)
        {
            BuilderMeshlet cluster = clusters[index];
            cluster.ParentError = float.MaxValue;
            clusters[index] = cluster;
        }
    }

    private static void AssignGroupParent(
        List<BuilderMeshlet> clusters,
        Span<int> group,
        ClusterLodBounds bounds,
        float error,
        int groupId)
    {
        foreach (int index in group)
        {
            BuilderMeshlet cluster = clusters[index];
            cluster.ParentError = error;
            cluster.ParentGroupId = groupId;
            cluster.LodCenter = bounds.Center;
            cluster.LodRadius = bounds.Radius;
            clusters[index] = cluster;
        }
    }

    private static void BuildParentClusters(
        ClusterLodConfig config,
        ReadOnlySpan<Vector3> positions,
        float[] materialIndicesArray,
        List<BuilderMeshlet> clusters,
        List<uint> globalIndices,
        List<uint> simplifiedIndices,
        ClusterLodBounds bounds,
        float error,
        int groupId,
        int depth,
        List<int> nextLevelClusters)
    {
        int start = clusters.Count;
        Clusterize(
            config,
            CollectionsMarshal.AsSpan(simplifiedIndices),
            positions,
            materialIndicesArray,
            clusters,
            globalIndices);
        for (int index = start; index < clusters.Count; index++)
        {
            clusters[index] = InitializeParentCluster(
                clusters[index],
                positions,
                globalIndices,
                bounds,
                error,
                groupId,
                depth + 1);
            nextLevelClusters.Add(index);
        }
    }

    private static BuilderMeshlet InitializeParentCluster(
        BuilderMeshlet cluster,
        ReadOnlySpan<Vector3> positions,
        List<uint> globalIndices,
        ClusterLodBounds bounds,
        float error,
        int groupId,
        int level)
    {
        ReadOnlySpan<uint> clusterIndices = CollectionsMarshal
            .AsSpan(globalIndices)
            .Slice(cluster.IndicesOffset, cluster.IndicesCount);
        ClusterLodBounds selfBounds = BoundsCompute(positions, clusterIndices, 0);
        cluster.Level = level;
        cluster.Center = selfBounds.Center;
        cluster.Radius = selfBounds.Radius;
        cluster.Error = error;
        cluster.GroupId = groupId;
        cluster.LodCenter = bounds.Center;
        cluster.LodRadius = bounds.Radius;
        cluster.SelfLodCenter = bounds.Center;
        cluster.SelfLodRadius = bounds.Radius;
        return cluster;
    }

    private static void AssignRootParentError(List<BuilderMeshlet> clusters, List<int> levelClusters)
    {
        if (levelClusters.Count != 1)
        {
            return;
        }

        BuilderMeshlet cluster = clusters[levelClusters[0]];
        cluster.ParentError = float.MaxValue;
        clusters[levelClusters[0]] = cluster;
    }
}

public static partial class ClusterMeshBuilder
{
    private static unsafe nuint BuildRemap(
        Span<uint> destination,
        ReadOnlySpan<uint> indices,
        Vector3[] positions,
        VertexData attributes,
        float[] materialIndices)
    {
        VertexStream[] attributesStreams = attributes.Streams;
        VertexStream[] inputs = [new(positions, Unsafe.SizeOf<Vector3>()), .. attributesStreams, new(materialIndices, sizeof(float))];
        var streams = new MeshOptimizer.Stream[inputs.Length];
        var handles = new GCHandle[inputs.Length];
        try
        {
            for (int index = 0; index < inputs.Length; index++)
            {
                handles[index] = GCHandle.Alloc(inputs[index].Data, GCHandleType.Pinned);
                nuint stride = (nuint)inputs[index].Stride;
                streams[index] = new(handles[index].AddrOfPinnedObject().ToPointer(), stride, stride);
            }
            return Meshopt.GenerateVertexRemapMulti<byte>(destination, indices, (nuint)positions.Length, streams);
        }
        finally
        {
            foreach (GCHandle handle in handles)
                if (handle.IsAllocated) handle.Free();
        }
    }

    private struct TempTri
    {
        public uint v0, v1, v2;
        public byte mat;
    }

    private readonly record struct VertReuseBatchData(
        uint Word0,
        uint Word1,
        uint Word2,
        uint Word3);

    private static void EmitSplitMeshlet(
        ReadOnlySpan<TempTri> tris,
        List<BuilderMeshlet> clusters,
        List<uint> globalIndices
    )
    {
        VertReuseBatchData vertReuseBatches = BuildVertReuseBatchInfo(tris);

        int startIndex = globalIndices.Count;
        var uniqueMats = new List<byte>();
        int range0End = 0, range1End = 0;

        var uniqueVerts = new HashSet<uint>();

        for (int i = 0; i < tris.Length; i++)
        {
            ref readonly var t = ref tris[i];
            if (!uniqueMats.Contains(t.mat))
            {
                uniqueMats.Add(t.mat);
                if (uniqueMats.Count == 2) range0End = i;
                if (uniqueMats.Count == 3) range1End = i;
            }
            globalIndices.Add(t.v0);
            globalIndices.Add(t.v1);
            globalIndices.Add(t.v2);
            uniqueVerts.Add(t.v0);
            uniqueVerts.Add(t.v1);
            uniqueVerts.Add(t.v2);
        }

        if (uniqueMats.Count < 2) range0End = tris.Length;
        if (uniqueMats.Count < 3) range1End = tris.Length;

        clusters.Add(
            new BuilderMeshlet
            {
                IndicesOffset = startIndex,
                IndicesCount = tris.Length * 3,
                VertexCount = uniqueVerts.Count,
                GroupId = -1,
                ParentGroupId = -1,
                Mat0 = uniqueMats.Count > 0 ? uniqueMats[0] : (byte)0,
                Mat1 = uniqueMats.Count > 1 ? uniqueMats[1] : (byte)0,
                Mat2 = uniqueMats.Count > 2 ? uniqueMats[2] : (byte)0,
                Range0End = (byte)range0End,
                Range1End = (byte)range1End,
                VertReuseBatchWord0 = vertReuseBatches.Word0,
                VertReuseBatchWord1 = vertReuseBatches.Word1,
                VertReuseBatchWord2 = vertReuseBatches.Word2,
                VertReuseBatchWord3 = vertReuseBatches.Word3,
            }
        );
    }
}

public static partial class ClusterMeshBuilder
{



    private static VertReuseBatchData BuildVertReuseBatchInfo(ReadOnlySpan<TempTri> triangles)
    {
        Span<uint> words = stackalloc uint[4];
        words.Clear();
        int triangleBitOffset = 12;
        int rangeIndex = 0;
        int rangeStart = 0;

        while (rangeStart < triangles.Length)
        {
            byte material = triangles[rangeStart].mat;
            int rangeEnd = rangeStart + 1;
            while (rangeEnd < triangles.Length && triangles[rangeEnd].mat == material)
                rangeEnd++;

            if (rangeIndex >= 3)
                throw new InvalidDataException("A cluster material chunk contains more than three ranges.");

            List<byte> batchTriangleCounts = BuildVertReuseBatches(
                triangles.Slice(rangeStart, rangeEnd - rangeStart));
            if (batchTriangleCounts.Count == 0 || batchTriangleCounts.Count >= 16)
                throw new InvalidDataException("A cluster material range has an invalid vertex-reuse batch count.");

            WriteVertReuseBits(words, rangeIndex * 4, (uint)batchTriangleCounts.Count, 4);
            foreach (byte triangleCount in batchTriangleCounts)
            {
                WriteVertReuseBits(words, triangleBitOffset, (uint)(triangleCount - 1), 5);
                triangleBitOffset += 5;
            }

            rangeIndex++;
            rangeStart = rangeEnd;
        }

        return new VertReuseBatchData(words[0], words[1], words[2], words[3]);
    }

    private static List<byte> BuildVertReuseBatches(ReadOnlySpan<TempTri> triangles)
    {
        var batchTriangleCounts = new List<byte>();
        var usedVertices = new HashSet<uint>();
        int triangleCount = 0;

        for (int index = 0; index < triangles.Length; index++)
        {
            ref readonly TempTri triangle = ref triangles[index];
            int newVertexCount = VertReuseVertexCountAfterAdd(usedVertices, triangle);
            if (newVertexCount > 32)
            {
                if (triangleCount == 0)
                    throw new InvalidDataException("A triangle exceeds the 32-vertex reuse-batch limit.");
                CloseVertReuseBatch(batchTriangleCounts, usedVertices, ref triangleCount);
                index--;
                continue;
            }

            AddVertReuseTriangleVertices(usedVertices, triangle);
            triangleCount++;
            if (triangleCount == 32)
                CloseVertReuseBatch(batchTriangleCounts, usedVertices, ref triangleCount);
        }

        CloseVertReuseBatch(batchTriangleCounts, usedVertices, ref triangleCount);
        return batchTriangleCounts;
    }

    private static int VertReuseVertexCountAfterAdd(HashSet<uint> usedVertices, TempTri triangle)
    {
        int count = usedVertices.Count;
        if (!usedVertices.Contains(triangle.v0)) count++;
        if (!usedVertices.Contains(triangle.v1)) count++;
        if (!usedVertices.Contains(triangle.v2)) count++;
        return count;
    }

    private static void AddVertReuseTriangleVertices(HashSet<uint> usedVertices, TempTri triangle)
    {
        usedVertices.Add(triangle.v0);
        usedVertices.Add(triangle.v1);
        usedVertices.Add(triangle.v2);
    }

    private static void CloseVertReuseBatch(
        List<byte> batchTriangleCounts,
        HashSet<uint> usedVertices,
        ref int triangleCount)
    {
        if (triangleCount == 0)
            return;

        batchTriangleCounts.Add(checked((byte)triangleCount));
        triangleCount = 0;
        usedVertices.Clear();
    }

    private static void WriteVertReuseBits(Span<uint> words, int bitOffset, uint value, int bitCount)
    {
        if (bitOffset < 0 || bitCount <= 0 || bitCount > 31 || bitOffset + bitCount > 128)
            throw new InvalidDataException("Vertex-reuse batch metadata exceeds its inline encoding.");

        uint mask = (1u << bitCount) - 1u;
        if ((value & ~mask) != 0)
            throw new InvalidDataException("Vertex-reuse batch metadata value exceeds its bit field.");

        int wordIndex = bitOffset >> 5;
        int wordBit = bitOffset & 31;
        words[wordIndex] |= value << wordBit;
        int lowBits = Math.Min(bitCount, 32 - wordBit);
        if (lowBits < bitCount)
            words[wordIndex + 1] |= value >> lowBits;
    }



    private static void Clusterize(
        ClusterLodConfig config,
        ReadOnlySpan<uint> indices,
        ReadOnlySpan<Vector3> positions,
        float[] materialIndicesArray,
        List<BuilderMeshlet> clusters,
        List<uint> globalIndices
    )
    {
        if (indices.IsEmpty)
        {
            return;
        }

        if (indices.Length % 3 != 0)
            throw new InvalidDataException("Cluster triangle indices are not divisible by three.");

        MeshletScratch scratch = RentMeshletScratch(config, indices.Length);
        try
        {
            nuint meshletCount = BuildMeshlets(config, indices, positions, scratch);
            EmitMeshlets(
                scratch,
                meshletCount,
                materialIndicesArray,
                clusters,
                globalIndices,
                config.OptimizeClusters);
        }
        finally
        {
            scratch.Return();
        }
    }

    private static byte ResolveTriangleMaterial(
        uint vertex,
        float[] materialIndices,
        int materialCount)
    {
        if (vertex >= materialIndices.Length)
            throw new InvalidDataException($"Vertex {vertex} has no material index.");
        float encoded = materialIndices[vertex];
        if (!float.IsFinite(encoded) ||
            encoded < 0 ||
            encoded >= materialCount ||
            encoded != MathF.Truncate(encoded))
        {
            throw new InvalidDataException($"Vertex {vertex} has invalid material index {encoded}.");
        }
        int material = (int)encoded;
        return checked((byte)material);
    }

    private static MeshletScratch RentMeshletScratch(ClusterLodConfig config, int indexCount)
    {
        nuint maxMeshlets = Meshopt.BuildMeshletsBound(
            (nuint)indexCount,
            (nuint)config.MaxVertices,
            (nuint)config.MaxTriangles);
        return new MeshletScratch(
            ArrayPool<MeshOptimizer.Meshlet>.Shared.Rent((int)maxMeshlets),
            ArrayPool<uint>.Shared.Rent((int)maxMeshlets * config.MaxVertices),
            ArrayPool<byte>.Shared.Rent((int)maxMeshlets * config.MaxTriangles * 3));
    }

    private static nuint BuildMeshlets(
        ClusterLodConfig config,
        ReadOnlySpan<uint> indices,
        ReadOnlySpan<Vector3> positions,
        MeshletScratch scratch)
    {
        ReadOnlySpan<float> positionSpan = MemoryMarshal.Cast<Vector3, float>(positions);
        return config.ClusterSpatial
            ? BuildSpatialMeshlets(config, indices, positionSpan, scratch)
            : BuildFlexibleMeshlets(config, indices, positionSpan, scratch);
    }

    private static nuint BuildSpatialMeshlets(
        ClusterLodConfig config,
        ReadOnlySpan<uint> indices,
        ReadOnlySpan<float> positions,
        MeshletScratch scratch)
        => Meshopt.BuildMeshletsSpatial(
            scratch.Meshlets.AsSpan(),
            scratch.Vertices.AsSpan(),
            scratch.Triangles.AsSpan(),
            indices,
            positions,
            (nuint)Unsafe.SizeOf<Vector3>(),
            (nuint)config.MaxVertices,
            (nuint)config.MinTriangles,
            (nuint)config.MaxTriangles,
            config.ClusterFillWeight);

    private static nuint BuildFlexibleMeshlets(
        ClusterLodConfig config,
        ReadOnlySpan<uint> indices,
        ReadOnlySpan<float> positions,
        MeshletScratch scratch)
        => Meshopt.BuildMeshletsFlex(
            scratch.Meshlets.AsSpan(),
            scratch.Vertices.AsSpan(),
            scratch.Triangles.AsSpan(),
            indices,
            positions,
            (nuint)Unsafe.SizeOf<Vector3>(),
            (nuint)config.MaxVertices,
            (nuint)config.MinTriangles,
            (nuint)config.MaxTriangles,
            0.0f,
            config.ClusterSplitFactor);

    private static void EmitMeshlets(
        MeshletScratch scratch,
        nuint meshletCount,
        float[] materialIndicesArray,
        List<BuilderMeshlet> clusters,
        List<uint> globalIndices,
        bool optimizeClusters)
    {
        for (int index = 0; index < (int)meshletCount; index++)
        {
            EmitMeshlet(scratch, index, materialIndicesArray, clusters, globalIndices, optimizeClusters);
        }
    }

    private static void EmitMeshlet(
        MeshletScratch scratch,
        int meshletIndex,
        float[] materialIndicesArray,
        List<BuilderMeshlet> clusters,
        List<uint> globalIndices,
        bool optimizeClusters)
    {
        ref MeshOptimizer.Meshlet meshlet = ref scratch.Meshlets[meshletIndex];
        if (optimizeClusters)
        {
            OptimizeMeshlet(scratch, meshlet);
        }

        TempTri[] triangles = ReadMeshletTriangles(scratch, meshlet, materialIndicesArray);
        SortTrianglesByMaterial(triangles);
        EmitMaterialChunks(triangles, clusters, globalIndices);
    }

    private static void OptimizeMeshlet(MeshletScratch scratch, MeshOptimizer.Meshlet meshlet)
    {
        Meshopt.OptimizeMeshlet(
            scratch.Vertices.AsSpan((int)meshlet.vertex_offset, (int)meshlet.vertex_count),
            scratch.Triangles.AsSpan((int)meshlet.triangle_offset, (int)meshlet.triangle_count * 3),
            meshlet.triangle_count,
            meshlet.vertex_count);
    }

    private static TempTri[] ReadMeshletTriangles(
        MeshletScratch scratch,
        MeshOptimizer.Meshlet meshlet,
        float[] materialIndicesArray)
    {
        TempTri[] triangles = new TempTri[meshlet.triangle_count];
        for (uint triangleIndex = 0; triangleIndex < meshlet.triangle_count; triangleIndex++)
        {
            triangles[triangleIndex] = ReadMeshletTriangle(scratch, meshlet, triangleIndex, materialIndicesArray);
        }

        return triangles;
    }

    private static TempTri ReadMeshletTriangle(
        MeshletScratch scratch,
        MeshOptimizer.Meshlet meshlet,
        uint triangleIndex,
        float[] materialIndicesArray)
    {
        int triangleOffset = (int)meshlet.triangle_offset + (int)triangleIndex * 3;
        uint v0 = scratch.Vertices[(int)meshlet.vertex_offset + scratch.Triangles[triangleOffset + 0]];
        uint v1 = scratch.Vertices[(int)meshlet.vertex_offset + scratch.Triangles[triangleOffset + 1]];
        uint v2 = scratch.Vertices[(int)meshlet.vertex_offset + scratch.Triangles[triangleOffset + 2]];
        return new TempTri { v0 = v0, v1 = v1, v2 = v2, mat = (byte)materialIndicesArray[v0] };
    }

    private static void SortTrianglesByMaterial(TempTri[] triangles)
    {
        List<TempTri>[] buckets = new List<TempTri>[256];
        for (int index = 0; index < triangles.Length; index++)
        {
            byte material = triangles[index].mat;
            buckets[material] ??= [];
            buckets[material].Add(triangles[index]);
        }

        CopyMaterialBuckets(buckets, triangles);
    }

    private static void CopyMaterialBuckets(List<TempTri>[] buckets, TempTri[] triangles)
    {
        int destination = 0;
        for (int material = 0; material < buckets.Length; material++)
        {
            if (buckets[material] == null)
            {
                continue;
            }

            foreach (TempTri triangle in buckets[material])
            {
                triangles[destination++] = triangle;
            }
        }
    }
}

public static partial class ClusterMeshBuilder
{


    private static void EmitMaterialChunks(
        TempTri[] triangles,
        List<BuilderMeshlet> clusters,
        List<uint> globalIndices)
    {
        var uniqueMaterials = new List<byte>();
        int chunkStart = 0;
        for (int index = 0; index < triangles.Length; index++)
        {
            if (TryStartMaterialChunk(
                    triangles,
                    index,
                    uniqueMaterials,
                    ref chunkStart,
                    clusters,
                    globalIndices))
            {
                continue;
            }
        }

        EmitRemainingMaterialChunk(
            triangles,
            chunkStart,
            clusters,
            globalIndices);
    }

    private static bool TryStartMaterialChunk(
        TempTri[] triangles,
        int index,
        List<byte> uniqueMaterials,
        ref int chunkStart,
        List<BuilderMeshlet> clusters,
        List<uint> globalIndices)
    {
        if (uniqueMaterials.Contains(triangles[index].mat))
        {
            return false;
        }

        if (uniqueMaterials.Count == 3)
        {
            EmitSplitMeshlet(
                new ReadOnlySpan<TempTri>(triangles, chunkStart, index - chunkStart),
                clusters,
                globalIndices);
            uniqueMaterials.Clear();
            chunkStart = index;
        }

        uniqueMaterials.Add(triangles[index].mat);
        return true;
    }

    private static void EmitRemainingMaterialChunk(
        TempTri[] triangles,
        int chunkStart,
        List<BuilderMeshlet> clusters,
        List<uint> globalIndices)
    {
        if (chunkStart < triangles.Length)
        {
            EmitSplitMeshlet(
                new ReadOnlySpan<TempTri>(triangles, chunkStart, triangles.Length - chunkStart),
                clusters,
                globalIndices);
        }
    }

    private readonly record struct MeshletScratch(
        MeshOptimizer.Meshlet[] Meshlets,
        uint[] Vertices,
        byte[] Triangles)
    {
        public void Return()
        {
            ArrayPool<MeshOptimizer.Meshlet>.Shared.Return(Meshlets);
            ArrayPool<uint>.Shared.Return(Vertices);
            ArrayPool<byte>.Shared.Return(Triangles);
        }
    }



    private static int Partition(
        ClusterLodConfig config,
        ReadOnlySpan<Vector3> positions,
        List<BuilderMeshlet> clusters,
        List<uint> globalIndices,
        List<int> levelClusters,
        ReadOnlySpan<uint> remap,
        List<int> groupOffsets
    )
    {
        groupOffsets.Clear();
        if (levelClusters.Count <= config.PartitionSize)
        {
            AddSinglePartition(levelClusters, groupOffsets);
            return 1;
        }

        int totalIndexCount = TotalPartitionIndexCount(clusters, levelClusters);
        PartitionScratch scratch = RentPartitionScratch(levelClusters.Count, totalIndexCount);
        try
        {
            FillPartitionInputs(scratch, clusters, globalIndices, levelClusters, remap);
            nuint partitionCount = ExecutePartition(config, positions, scratch, totalIndexCount, levelClusters.Count);
            scratch.Remap = CreatePartitionRemap(config, positions, clusters, levelClusters, scratch.Partitions, partitionCount);
            ApplyPartitionOrder(levelClusters, groupOffsets, scratch, partitionCount);
            return (int)partitionCount;
        }
        finally
        {
            scratch.Return();
        }
    }

    private static void AddSinglePartition(List<int> levelClusters, List<int> groupOffsets)
    {
        groupOffsets.Add(0);
        groupOffsets.Add(levelClusters.Count);
    }

    private static int TotalPartitionIndexCount(List<BuilderMeshlet> clusters, List<int> levelClusters)
    {
        int total = 0;
        for (int index = 0; index < levelClusters.Count; index++)
        {
            total += clusters[levelClusters[index]].IndicesCount;
        }

        return total;
    }

    private static PartitionScratch RentPartitionScratch(int levelClusterCount, int totalIndexCount)
    {
        return new PartitionScratch(
            ArrayPool<uint>.Shared.Rent(totalIndexCount),
            ArrayPool<uint>.Shared.Rent(levelClusterCount),
            ArrayPool<uint>.Shared.Rent(levelClusterCount),
            ArrayPool<int>.Shared.Rent(levelClusterCount));
    }

    private static void FillPartitionInputs(
        PartitionScratch scratch,
        List<BuilderMeshlet> clusters,
        List<uint> globalIndices,
        List<int> levelClusters,
        ReadOnlySpan<uint> remap)
    {
        int offset = 0;
        Span<uint> globalIndicesSpan = CollectionsMarshal.AsSpan(globalIndices);
        for (int index = 0; index < levelClusters.Count; index++)
        {
            BuilderMeshlet cluster = clusters[levelClusters[index]];
            scratch.Counts[index] = (uint)cluster.IndicesCount;
            offset = CopyPartitionIndices(cluster, globalIndicesSpan, remap, scratch.Indices, offset);
        }
    }

    private static int CopyPartitionIndices(
        BuilderMeshlet cluster,
        Span<uint> globalIndices,
        ReadOnlySpan<uint> remap,
        uint[] destination,
        int offset)
    {
        ReadOnlySpan<uint> source = globalIndices.Slice(cluster.IndicesOffset, cluster.IndicesCount);
        for (int index = 0; index < source.Length; index++)
        {
            destination[offset++] = remap[(int)source[index]];
        }

        return offset;
    }

    private static nuint ExecutePartition(
        ClusterLodConfig config,
        ReadOnlySpan<Vector3> positions,
        PartitionScratch scratch,
        int totalIndexCount,
        int levelClusterCount)
    {
        ReadOnlySpan<float> positionSpan = MemoryMarshal.Cast<Vector3, float>(positions);
        return Meshopt.PartitionClusters(
            scratch.Partitions.AsSpan(0, levelClusterCount),
            scratch.Indices.AsSpan(0, totalIndexCount),
            scratch.Counts.AsSpan(0, levelClusterCount),
            config.PartitionSpatial ? positionSpan : default,
            (nuint)Unsafe.SizeOf<Vector3>(),
            (nuint)config.PartitionSize);
    }

    private static uint[]? CreatePartitionRemap(
        ClusterLodConfig config,
        ReadOnlySpan<Vector3> positions,
        List<BuilderMeshlet> clusters,
        List<int> levelClusters,
        uint[] clusterPart,
        nuint partitionCount)
    {
        if (!config.PartitionSort)
        {
            return null;
        }

        float[] partitionPoints = ArrayPool<float>.Shared.Rent((int)partitionCount * 3);
        uint[] partitionRemap = ArrayPool<uint>.Shared.Rent((int)partitionCount);
        try
        {
            FillPartitionPoints(partitionPoints, clusters, levelClusters, clusterPart);
            Meshopt.SpatialSortRemap(
                partitionRemap.AsSpan(0, (int)partitionCount),
                partitionPoints.AsSpan(0, (int)partitionCount * 3),
                (nuint)Unsafe.SizeOf<Vector3>());
            return partitionRemap;
        }
        finally
        {
            ArrayPool<float>.Shared.Return(partitionPoints);
        }
    }

    private static void FillPartitionPoints(
        float[] partitionPoints,
        List<BuilderMeshlet> clusters,
        List<int> levelClusters,
        uint[] clusterPart)
    {
        for (int index = 0; index < levelClusters.Count; index++)
        {
            Vector3 center = clusters[levelClusters[index]].Center;
            int offset = (int)clusterPart[index] * 3;
            partitionPoints[offset + 0] = center.X;
            partitionPoints[offset + 1] = center.Y;
            partitionPoints[offset + 2] = center.Z;
        }
    }

    private static void ApplyPartitionOrder(
        List<int> levelClusters,
        List<int> groupOffsets,
        PartitionScratch scratch,
        nuint partitionCount)
    {
        int count = (int)partitionCount;
        int[] partitionSizes = ArrayPool<int>.Shared.Rent(count);
        int[] offsets = ArrayPool<int>.Shared.Rent(count);
        try
        {
            Array.Clear(partitionSizes, 0, count);
            CountPartitionSizes(scratch, levelClusters.Count, partitionSizes);
            FillPartitionOffsets(partitionSizes, count, offsets, groupOffsets);
            CopyClustersByPartition(levelClusters, scratch, offsets);
        }
        finally
        {
            ArrayPool<int>.Shared.Return(partitionSizes);
            ArrayPool<int>.Shared.Return(offsets);
        }
    }

    private static void CountPartitionSizes(
        PartitionScratch scratch,
        int levelClusterCount,
        int[] partitionSizes)
    {
        for (int index = 0; index < levelClusterCount; index++)
        {
            uint partitionId = RemappedPartitionId(scratch, index);
            partitionSizes[(int)partitionId]++;
        }
    }

    private static uint RemappedPartitionId(PartitionScratch scratch, int index)
    {
        uint partitionId = scratch.Partitions[index];
        return scratch.Remap == null ? partitionId : scratch.Remap[partitionId];
    }

    private static void FillPartitionOffsets(
        int[] partitionSizes,
        int partitionCount,
        int[] offsets,
        List<int> groupOffsets)
    {
        int runningOffset = 0;
        groupOffsets.Add(0);
        for (int index = 0; index < partitionCount; index++)
        {
            offsets[index] = runningOffset;
            runningOffset += partitionSizes[index];
            groupOffsets.Add(runningOffset);
        }
    }

    private static void CopyClustersByPartition(
        List<int> levelClusters,
        PartitionScratch scratch,
        int[] offsets)
    {
        for (int index = 0; index < levelClusters.Count; index++)
        {
            uint partitionId = RemappedPartitionId(scratch, index);
            int destination = offsets[partitionId]++;
            scratch.ClusterOrder[destination] = levelClusters[index];
        }

        new Span<int>(scratch.ClusterOrder, 0, levelClusters.Count).CopyTo(CollectionsMarshal.AsSpan(levelClusters));
    }
}

public static partial class ClusterMeshBuilder
{


    private sealed class PartitionScratch
    {
        public PartitionScratch(uint[] indices, uint[] counts, uint[] partitions, int[] clusterOrder)
        {
            Indices = indices;
            Counts = counts;
            Partitions = partitions;
            ClusterOrder = clusterOrder;
        }

        public uint[] Indices { get; }
        public uint[] Counts { get; }
        public uint[] Partitions { get; }
        public int[] ClusterOrder { get; }
        public uint[]? Remap { get; set; }

        public void Return()
        {
            ArrayPool<uint>.Shared.Return(Indices);
            ArrayPool<uint>.Shared.Return(Counts);
            ArrayPool<uint>.Shared.Return(Partitions);
            ArrayPool<int>.Shared.Return(ClusterOrder);
            if (Remap != null)
            {
                ArrayPool<uint>.Shared.Return(Remap);
            }
        }
    }



    private static void LockBoundary(
        Span<byte> locks,
        List<BuilderMeshlet> clusters,
        List<uint> globalIndices,
        List<int> levelClusters,
        List<int> groupOffsets,
        ReadOnlySpan<uint> remap
    )
    {
        const byte LockBit = 1 << 0;
        const byte SeenBit = 1 << 7;
        const byte SimplifyProtect = 2; // meshopt_SimplifyVertex_Protect

        for (int i = 0; i < locks.Length; i++)
            locks[i] &= unchecked((byte)~(LockBit | SeenBit));

        var globalIndicesSpan = CollectionsMarshal.AsSpan(globalIndices);

        for (int g = 0; g < groupOffsets.Count - 1; g++)
        {
            int start = groupOffsets[g];
            int count = groupOffsets[g + 1] - start;
            var group = CollectionsMarshal.AsSpan(levelClusters).Slice(start, count);

            foreach (int clusterIdx in group)
            {
                var c = clusters[clusterIdx];
                var indices = globalIndicesSpan.Slice(c.IndicesOffset, c.IndicesCount);
                foreach (var v in indices)
                {
                    uint r = remap[(int)v];
                    locks[(int)r] |= (byte)((locks[(int)r] & SeenBit) >> 7);
                }
            }
            foreach (int clusterIdx in group)
            {
                var c = clusters[clusterIdx];
                var indices = globalIndicesSpan.Slice(c.IndicesOffset, c.IndicesCount);
                foreach (var v in indices)
                {
                    uint r = remap[(int)v];
                    locks[(int)r] |= SeenBit;
                }
            }
        }

        for (int i = 0; i < locks.Length; i++)
        {
            locks[i] = (byte)((locks[i] & LockBit) | (locks[i] & SimplifyProtect));
        }
    }

    private static void Simplify(
        ClusterLodConfig config,
        ReadOnlySpan<Vector3> positions,
        ReadOnlySpan<uint> indices,
        ReadOnlySpan<byte> locks,
        int targetCount,
        out float error,
        List<uint> outputIndices
    )
    {
        outputIndices.Clear();
        if (targetCount >= indices.Length)
        {
            CopySimplifiedIndices(indices, outputIndices);
            error = 0;
            return;
        }

        uint[] simplified = ArrayPool<uint>.Shared.Rent(indices.Length);
        try
        {
            nuint newCount = SimplifyWithFallback(
                config,
                positions,
                indices,
                locks,
                targetCount,
                simplified,
                out error);
            error = ClampSimplifyError(config, positions, indices, error);
            CopySimplifiedIndices(simplified.AsSpan(0, (int)newCount), outputIndices);
        }
        finally
        {
            ArrayPool<uint>.Shared.Return(simplified);
        }
    }

    private static nuint SimplifyWithFallback(
        ClusterLodConfig config,
        ReadOnlySpan<Vector3> positions,
        ReadOnlySpan<uint> indices,
        ReadOnlySpan<byte> locks,
        int targetCount,
        uint[] simplified,
        out float error)
    {
        ReadOnlySpan<float> positionSpan = MemoryMarshal.Cast<Vector3, float>(positions);
        nuint newCount = Meshopt.SimplifyWithAttributes(
            simplified.AsSpan(),
            indices,
            positionSpan,
            (nuint)Unsafe.SizeOf<Vector3>(),
            null,
            0,
            null,
            0,
            locks,
            (nuint)targetCount,
            float.MaxValue,
            SimplifyOptions(config),
            out error);
        return TrySloppySimplify(config, indices, positionSpan, targetCount, simplified, newCount, ref error);
    }

    private static SimplificationOptions SimplifyOptions(ClusterLodConfig config)
    {
        var options = SimplificationOptions.SimplifyLockBorder;
        options |= (SimplificationOptions)2;
        options |= (SimplificationOptions)4;
        if (config.SimplifyPermissive)
        {
            options |= (SimplificationOptions)32;
        }

        if (config.SimplifyRegularize)
        {
            options |= (SimplificationOptions)16;
        }

        return options;
    }

    private static nuint TrySloppySimplify(
        ClusterLodConfig config,
        ReadOnlySpan<uint> indices,
        ReadOnlySpan<float> positions,
        int targetCount,
        uint[] simplified,
        nuint newCount,
        ref float error)
    {
        if (newCount <= (nuint)targetCount || !config.SimplifyFallbackSloppy)
        {
            return newCount;
        }

        nuint fallbackCount = Meshopt.SimplifySloppy(
            simplified.AsSpan(),
            indices,
            positions,
            (nuint)Unsafe.SizeOf<Vector3>(),
            (nuint)targetCount,
            float.MaxValue,
            out error);
        error *= config.SimplifyErrorFactorSloppy;
        return fallbackCount;
    }

    private static float ClampSimplifyError(
        ClusterLodConfig config,
        ReadOnlySpan<Vector3> positions,
        ReadOnlySpan<uint> indices,
        float error)
    {
        if (config.SimplifyErrorEdgeLimit <= 0)
        {
            return error;
        }

        float maxEdgeSq = MaxTriangleEdgeMetric(positions, indices);
        return Math.Min(error, (float)Math.Sqrt(maxEdgeSq) * config.SimplifyErrorEdgeLimit);
    }

    private static float MaxTriangleEdgeMetric(
        ReadOnlySpan<Vector3> positions,
        ReadOnlySpan<uint> indices)
    {
        float maxEdgeSq = 0;
        for (int index = 0; index < indices.Length; index += 3)
        {
            maxEdgeSq = Math.Max(maxEdgeSq, TriangleEdgeMetric(positions, indices, index));
        }

        return maxEdgeSq;
    }

    private static float TriangleEdgeMetric(
        ReadOnlySpan<Vector3> positions,
        ReadOnlySpan<uint> indices,
        int index)
    {
        Vector3 va = positions[(int)indices[index + 0]];
        Vector3 vb = positions[(int)indices[index + 1]];
        Vector3 vc = positions[(int)indices[index + 2]];
        float eab = Vector3.DistanceSquared(va, vb);
        float eac = Vector3.DistanceSquared(va, vc);
        float ebc = Vector3.DistanceSquared(vb, vc);
        float maxEdge = Math.Max(Math.Max(eab, eac), ebc);
        float minEdge = Math.Min(Math.Min(eab, eac), ebc);
        return Math.Max(minEdge, maxEdge / 4.0f);
    }

    private static void CopySimplifiedIndices(
        ReadOnlySpan<uint> indices,
        List<uint> outputIndices)
    {
        foreach (uint index in indices)
        {
            outputIndices.Add(index);
        }
    }



    private static ClusterLodBounds BoundsCompute(
        ReadOnlySpan<Vector3> positions,
        ReadOnlySpan<uint> indices,
        float error
    )
    {
        var posSpan = MemoryMarshal.Cast<Vector3, float>(positions);
        var b = Meshopt.ComputeClusterBounds(indices, posSpan, (nuint)Unsafe.SizeOf<Vector3>());

        Vector3 center;
        unsafe
        {
            center = *(Vector3*)b.center;
        }

        return new ClusterLodBounds
        {
            Center = center,
            Radius = b.radius,
            Error = error,
        };
    }

    private static ClusterLodBounds BoundsMerge(
        List<BuilderMeshlet> clusters,
        ReadOnlySpan<int> group
    )
    {
        var centers = ArrayPool<float>.Shared.Rent(group.Length * 3);
        var radii = ArrayPool<float>.Shared.Rent(group.Length);

        try
        {
            float maxError = 0;
            for (int i = 0; i < group.Length; i++)
            {
                var c = clusters[group[i]];
                centers[i * 3 + 0] = c.SelfLodCenter.X;
                centers[i * 3 + 1] = c.SelfLodCenter.Y;
                centers[i * 3 + 2] = c.SelfLodCenter.Z;
                radii[i] = c.SelfLodRadius;
                maxError = Math.Max(maxError, c.Error);
            }

            var merged = Meshopt.ComputeSphereBounds(
                centers.AsSpan(0, group.Length * 3),
                sizeof(float) * 3,
                radii.AsSpan(0, group.Length),
                sizeof(float)
            );

            Vector3 mergedCenter;
            unsafe
            {
                mergedCenter = *(Vector3*)merged.center;
            }
            return new ClusterLodBounds
            {
                Center = mergedCenter,
                Radius = merged.radius,
                Error = maxError,
            };
        }
        finally
        {
            ArrayPool<float>.Shared.Return(centers);
            ArrayPool<float>.Shared.Return(radii);
        }
    }
}
