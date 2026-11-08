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
            ClusterSpatial = true,
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
    public const byte SourceVertexLockBit = 1 << 2;
    private const int MaxVerticesPerMeshlet = 64;
    private const int MaxTrianglesPerMeshlet = 124;
    private const float ConeWeight = 0.0f;

    public struct ClusterMeshlet
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
        public uint[] LocalVertices;
        public byte[] LocalIndices;

    }

    private struct ClusterLodBounds
    {
        public Vector3 Center;
        public float Radius;
        public float Error;
    }

    public static void BuildLod(ClusterLodConfig config, ReadOnlySpan<Vector3> positions, ReadOnlySpan<uint> indices,
        float[] materialIndicesArray, List<ClusterMeshlet> clusters, List<uint> globalIndices,
        ReadOnlySpan<byte> vertexLocks = default, IReadOnlyList<RawAttribute>? vertexAttributes = null)
        => BuildLod(config, positions, indices, materialIndicesArray.Select(static value => checked((uint)value)).ToArray(),
            clusters, globalIndices, vertexLocks, vertexAttributes);

    public static void BuildLod(ClusterLodConfig config, ReadOnlySpan<Vector3> positions, ReadOnlySpan<uint> indices,
        uint[] materialIndicesArray, List<ClusterMeshlet> clusters, List<uint> globalIndices,
        ReadOnlySpan<byte> vertexLocks = default, IReadOnlyList<RawAttribute>? vertexAttributes = null)
    {
        HashSet<Vector3> materialBoundaries = FindMaterialBoundaries(positions,
            materialIndicesArray);
        Clusterize(config, indices, positions, materialIndicesArray, clusters, globalIndices);
        int nextGroupId = InitializeLeafClusters(positions, clusters, globalIndices);
        List<int> current = Enumerable.Range(0, clusters.Count).ToList();
        var mergedIndices = new List<uint>();
        var attributes = current.Count > 1 ? new ClusterSimplificationAttributes(vertexAttributes ?? [], positions.Length) : null;
        while (current.Count > 1 && attributes!.CanSimplify)
        {
            List<int[]> groups = PartitionClusterGroups(positions, clusters, globalIndices, current);
            byte[] locks = LockGroupBoundaries(positions, positions.Length, clusters, globalIndices, current,
                groups, materialBoundaries);
            if (!vertexLocks.IsEmpty)
                for (int vertex = 0; vertex < locks.Length; vertex++)
                {
                    locks[vertex] |= (byte)(vertexLocks[vertex] & 3);
                    if ((vertexLocks[vertex] & SourceVertexLockBit) != 0) locks[vertex] |= 1;
                }
            var next = new List<int>();
            foreach (int[] groupIndices in groups)
            {
                int[] group = groupIndices.Select(index => current[index]).ToArray();
                if (group.Length == 1) { next.Add(group[0]); continue; }
                MergeGroupIndices(globalIndices, clusters, group, mergedIndices);
                ClusterLodBounds groupBounds = BoundsMerge(clusters, group);
                int parentCount = Math.Max(1, (mergedIndices.Count / 3 + config.MaxTriangles * 2 - 1) / (config.MaxTriangles * 2));
                int targetClusterSize = config.MaxTriangles - 2;
                float error = groupBounds.Error;
                var parents = new List<ClusterMeshlet>();
                var parentIndices = new List<uint>();
                var reducedIndices = new List<uint>();
                float[] attributeWeights = attributes.GroupWeights(positions,
                    CollectionsMarshal.AsSpan(mergedIndices));
                do
                {
                    SimplifyLodGroup(positions,
                        CollectionsMarshal.AsSpan(mergedIndices), locks, parentCount * targetClusterSize * 3,
                        attributes.Values, attributeWeights, config.SimplifyPermissive, out float reductionError, reducedIndices);
                    error = groupBounds.Error + reductionError;
                    parents.Clear();
                    parentIndices.Clear();
                    Clusterize(config, CollectionsMarshal.AsSpan(reducedIndices),
                        positions, materialIndicesArray, parents, parentIndices);
                    if (parents.Count <= parentCount) break;
                    targetClusterSize -= 2;
                } while (targetClusterSize > config.MaxTriangles / 2);
                if (parents.Count >= group.Length)
                {
                    foreach (int index in group)
                    {
                        ClusterMeshlet root = clusters[index];
                        root.ParentError = float.MaxValue;
                        clusters[index] = root;
                    }
                    continue;
                }
                int groupId = nextGroupId++;
                foreach (int index in group)
                {
                    ClusterMeshlet child = clusters[index];
                    child.ParentError = error; child.ParentGroupId = groupId;
                    child.LodCenter = groupBounds.Center; child.LodRadius = groupBounds.Radius;
                    clusters[index] = child;
                }
                int firstParent = clusters.Count;
                int firstIndex = globalIndices.Count;
                foreach (uint index in parentIndices)
                    globalIndices.Add(index);
                foreach (ClusterMeshlet data in parents)
                {
                    ClusterMeshlet parent = data;
                    parent.IndicesOffset += firstIndex;
                    clusters.Add(parent);
                }
                int level = group.Max(index => clusters[index].Level) + 1;
                for (int index = firstParent; index < clusters.Count; index++)
                {
                    ClusterMeshlet parent = clusters[index];
                    ClusterLodBounds bounds = BoundsCompute(positions,
                        CollectionsMarshal.AsSpan(globalIndices).Slice(parent.IndicesOffset, parent.IndicesCount), 0);
                    parent.Level = level; parent.Error = error; parent.GroupId = groupId; parent.ParentGroupId = -1;
                    parent.Center = bounds.Center; parent.Radius = bounds.Radius;
                    parent.LodCenter = parent.SelfLodCenter = groupBounds.Center;
                    parent.LodRadius = parent.SelfLodRadius = groupBounds.Radius;
                    clusters[index] = parent;
                    next.Add(index);
                }
            }
            current = next;
        }
        foreach (int index in current)
        {
            ClusterMeshlet root = clusters[index]; root.ParentError = float.MaxValue; root.ParentGroupId = -1;
            clusters[index] = root;
        }
        ConstrainMeshlets(clusters, globalIndices, materialIndicesArray);
    }

    private static int InitializeLeafClusters(ReadOnlySpan<Vector3> positions,
        List<ClusterMeshlet> clusters, List<uint> globalIndices)
    {
        for (int index = 0; index < clusters.Count; index++)
        {
            ClusterMeshlet cluster = clusters[index];
            ClusterLodBounds bounds = BoundsCompute(positions, CollectionsMarshal.AsSpan(globalIndices)
                .Slice(cluster.IndicesOffset, cluster.IndicesCount), 0);
            cluster.Center = cluster.LodCenter = cluster.SelfLodCenter = bounds.Center;
            cluster.Radius = cluster.LodRadius = cluster.SelfLodRadius = bounds.Radius;
            cluster.Error = 0; cluster.Level = 0; cluster.GroupId = index; cluster.ParentGroupId = -1;
            clusters[index] = cluster;
        }
        return clusters.Count;
    }

    private static void MergeGroupIndices(
        List<uint> globalIndices,
        List<ClusterMeshlet> clusters,
        Span<int> group,
        List<uint> destination)
    {
        destination.Clear();
        Span<uint> globalSpan = CollectionsMarshal.AsSpan(globalIndices);
        foreach (int clusterIndex in group)
        {
            ClusterMeshlet cluster = clusters[clusterIndex];
            ReadOnlySpan<uint> clusterIndices = globalSpan.Slice(cluster.IndicesOffset, cluster.IndicesCount);
            for (int index = 0; index < clusterIndices.Length; index++)
            {
                destination.Add(clusterIndices[index]);
            }
        }
    }

    private static unsafe nuint BuildRemap(
        Span<uint> destination,
        ReadOnlySpan<uint> indices,
        Vector3[] positions,
        VertexData attributes,
        uint[] materialIndices)
    {
        VertexStream[] attributesStreams = attributes.Streams;
        VertexStream[] inputs = [new(positions, Unsafe.SizeOf<Vector3>()), .. attributesStreams, new(materialIndices, sizeof(uint))];
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
        public uint mat;
    }

    private static void EmitSplitMeshlet(
        ReadOnlySpan<TempTri> tris,
        List<ClusterMeshlet> clusters,
        List<uint> globalIndices
    )
    {
        int startIndex = globalIndices.Count;
        var vertices = new HashSet<uint>();
        foreach (TempTri triangle in tris)
        {
            globalIndices.Add(triangle.v0); globalIndices.Add(triangle.v1); globalIndices.Add(triangle.v2);
            vertices.Add(triangle.v0); vertices.Add(triangle.v1); vertices.Add(triangle.v2);
        }
        clusters.Add(new ClusterMeshlet
        {
            IndicesOffset = startIndex, IndicesCount = tris.Length * 3,
            VertexCount = vertices.Count, GroupId = -1, ParentGroupId = -1,
        });
    }

    private static void Clusterize(
        ClusterLodConfig config,
        ReadOnlySpan<uint> indices,
        ReadOnlySpan<Vector3> positions,
        uint[] materialIndicesArray,
        List<ClusterMeshlet> clusters,
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
        uint[] materialIndicesArray,
        List<ClusterMeshlet> clusters,
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
        uint[] materialIndicesArray,
        List<ClusterMeshlet> clusters,
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
        uint[] materialIndicesArray)
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
        uint[] materialIndicesArray)
    {
        int triangleOffset = (int)meshlet.triangle_offset + (int)triangleIndex * 3;
        uint v0 = scratch.Vertices[(int)meshlet.vertex_offset + scratch.Triangles[triangleOffset + 0]];
        uint v1 = scratch.Vertices[(int)meshlet.vertex_offset + scratch.Triangles[triangleOffset + 1]];
        uint v2 = scratch.Vertices[(int)meshlet.vertex_offset + scratch.Triangles[triangleOffset + 2]];
        return new TempTri { v0 = v0, v1 = v1, v2 = v2, mat = materialIndicesArray[v0] };
    }

    private static void SortTrianglesByMaterial(TempTri[] triangles)
        => triangles.GroupBy(static triangle => triangle.mat).OrderByDescending(static group => group.Count())
            .ThenBy(static group => group.Key).SelectMany(static group => group).ToArray().CopyTo(triangles, 0);



    private static void EmitMaterialChunks(
        TempTri[] triangles,
        List<ClusterMeshlet> clusters,
        List<uint> globalIndices)
    {
        var uniqueMaterials = new List<uint>();
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
        List<uint> uniqueMaterials,
        ref int chunkStart,
        List<ClusterMeshlet> clusters,
        List<uint> globalIndices)
    {
        if (uniqueMaterials.Contains(triangles[index].mat))
        {
            return false;
        }

        if (uniqueMaterials.Count == 64)
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
        List<ClusterMeshlet> clusters,
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





    private static void SimplifyLodGroup(ReadOnlySpan<Vector3> positions, ReadOnlySpan<uint> indices,
        ReadOnlySpan<byte> locks, int targetIndexCount, ReadOnlySpan<float> attributes,
        ReadOnlySpan<float> attributeWeights, bool allowPermissive, out float error, List<uint> outputIndices)
    {
        outputIndices.Clear();
        if (targetIndexCount >= indices.Length)
        {
            outputIndices.AddRange(indices);
            error = 0;
            return;
        }

        uint[] reducedIndices = ArrayPool<uint>.Shared.Rent(indices.Length);
        try
        {
            SimplificationOptions permissive = allowPermissive ? (SimplificationOptions)(1 << 5) : 0;
            int count = checked((int)Meshopt.SimplifyWithAttributes(reducedIndices, indices,
                MemoryMarshal.Cast<Vector3, float>(positions), (nuint)Unsafe.SizeOf<Vector3>(),
                attributes, (nuint)(attributeWeights.Length * sizeof(float)), attributeWeights,
                (nuint)attributeWeights.Length, locks, (nuint)targetIndexCount, float.MaxValue,
                SimplificationOptions.SimplifyLockBorder | SimplificationOptions.meshopt_SimplifySparse |
                SimplificationOptions.meshopt_SimplifyErrorAbsolute | permissive, out error));
            outputIndices.AddRange(reducedIndices.AsSpan(0, count));
        }
        finally
        {
            ArrayPool<uint>.Shared.Return(reducedIndices);
        }
    }

    private static ClusterLodBounds BoundsCompute(ReadOnlySpan<Vector3> positions, ReadOnlySpan<uint> indices, float error)
    {
        var used = new HashSet<uint>();
        var points = new List<Vector3>();
        foreach (uint index in indices) if (used.Add(index)) points.Add(positions[(int)index]);
        Vector4 sphere = ClusterSphere.FromPoints(CollectionsMarshal.AsSpan(points));
        return new ClusterLodBounds { Center = new(sphere.X, sphere.Y, sphere.Z), Radius = sphere.W, Error = error };
    }

    private static ClusterLodBounds BoundsMerge(List<ClusterMeshlet> clusters, ReadOnlySpan<int> group)
    {
        var spheres = new Vector4[group.Length];
        float maximumError = 0;
        for (int index = 0; index < group.Length; index++)
        {
            ClusterMeshlet cluster = clusters[group[index]];
            spheres[index] = new(cluster.SelfLodCenter, cluster.SelfLodRadius);
            maximumError = Math.Max(maximumError, cluster.Error);
        }
        Vector4 sphere = ClusterSphere.FromSpheres(spheres);
        return new ClusterLodBounds { Center = new(sphere.X, sphere.Y, sphere.Z), Radius = sphere.W, Error = maximumError };
    }
}
