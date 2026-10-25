using System.Buffers.Binary;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using MeshOptimizer;
using SomeEngine.Resources.Geometry;
using SomeEngine.Resources;
using SomeEngine.Job;
using ValueType = SomeEngine.Resources.Geometry.ValueType;
using Stream = System.IO.Stream;

namespace SomeEngine.Geometry.Cluster;

public static partial class ClusterMeshBuilder
{
    private static Mesh BuildCore(
        Vector3[] positions, VertexData attributes, uint[] indices, float[] materialIndices,
        int materialCount, string name, ClusterBuildStrategy strategy)
    {
        using var stream = new MemoryStream();
        var scratch = new RawMeshScratch(positions.Length);
        try
        {
            MeshInput mesh = PrepareMesh(positions, attributes, indices, materialIndices, strategy, scratch);
            MeshletBuild meshlets = BuildMeshletData(mesh, strategy);
            SortMeshlets(meshlets.Meshlets, meshlets.Bounds);
            List<ClusterInfo> clusters = WriteMeshPages(stream, mesh, meshlets);
            uint[] lowestPages = clusters
                .Where(static cluster => cluster.LODError >= (float)Half.MaxValue)
                .Select(static cluster => cluster.PageIndex).Distinct().Order().ToArray();
            if (lowestPages.Length == 0)
                lowestPages = [clusters.Max(static cluster => cluster.PageIndex)];
            long bvhOffset = WriteBvh(stream, clusters);
            byte[] content = stream.ToArray();
            var descriptions = MeshIntegrity.Inspect(content, bvhOffset, attributes.Compatibility);
            var pages = new ReadOnlyMemory<byte>[descriptions.Length];
            for (int index = 0; index < pages.Length; index++)
                pages[index] = content.AsMemory((int)descriptions[index].Offset, (int)descriptions[index].Length);
            return Mesh.Create(name, attributes.Compatibility, descriptions, content.AsMemory((int)bvhOffset),
                pages, CreateBounds(meshlets.Bounds), CreateMeshRegions(materialCount), lowestPages);
        }
        finally { scratch.Return(); }
    }

    private static MeshInput PrepareMesh(Vector3[] positions, VertexData attributes,
        uint[] indices, float[] materialIndices, ClusterBuildStrategy strategy, RawMeshScratch scratch)
    {
        if (strategy == ClusterBuildStrategy.Realtime)
            return new(positions, indices, positions.Length, indices.Length, attributes, materialIndices);

        nuint vertexCount = BuildRemap(scratch.Remap.AsSpan(0, positions.Length), indices, positions, attributes, materialIndices);
        scratch.Positions = ArrayPool<Vector3>.Shared.Rent((int)vertexCount);
        scratch.Indices = ArrayPool<uint>.Shared.Rent(indices.Length);
        RemapPositions(positions, scratch.Remap, vertexCount, scratch.Positions);
        RemapIndices(indices, scratch.Remap.AsSpan(0, positions.Length), scratch.Indices);
        VertexData remapped = attributes.Remap(scratch.Remap.AsSpan(0, positions.Length), (int)vertexCount);
        float[] remappedMaterialIndices = RemapValues(materialIndices, scratch.Remap.AsSpan(0, positions.Length), (int)vertexCount);
        Meshopt.OptimizeVertexCache(scratch.Indices.AsSpan(0, indices.Length), scratch.Indices.AsSpan(0, indices.Length), vertexCount);
        return new(scratch.Positions, scratch.Indices, (int)vertexCount, indices.Length, remapped, remappedMaterialIndices);
    }

    private static void RemapPositions(
        Vector3[] source,
        uint[] remap,
        nuint vertexCount,
        Vector3[] destination)
    {
        for (int oldIndex = 0; oldIndex < source.Length; oldIndex++)
        {
            uint newIndex = remap[oldIndex];
            if (newIndex != uint.MaxValue && newIndex < vertexCount)
            {
                destination[newIndex] = source[oldIndex];
            }
        }
    }

    private static void RemapIndices(
        uint[] rawIndices,
        ReadOnlySpan<uint> remap,
        uint[] destination)
    {
        Meshopt.RemapIndexBuffer(
            destination.AsSpan(0, rawIndices.Length),
            rawIndices.AsSpan(),
            remap);
    }

    private static MeshletBuild BuildMeshletData(MeshInput mesh, ClusterBuildStrategy strategy)
    {
        var meshlets = new List<ClusterMeshlet>();
        var globalIndices = new List<uint>();
        ClusterLodConfig config = ClusterLodConfig.GetDefault() with { ClusterSpatial = true };
        ReadOnlySpan<Vector3> positions = mesh.Positions.AsSpan(0, mesh.VertexCount);
        ReadOnlySpan<uint> indices = mesh.Indices.AsSpan(0, mesh.IndexCount);
        if (strategy == ClusterBuildStrategy.Offline)
            BuildLod(config, positions, indices, mesh.MaterialIndices, meshlets, globalIndices);
        else
        {
            Clusterize(config, indices, positions, mesh.MaterialIndices, meshlets, globalIndices);
            InitializeLeafClusters(positions, meshlets, globalIndices);
            for (int index = 0; index < meshlets.Count; index++)
            {
                ClusterMeshlet cluster = meshlets[index];
                cluster.ParentError = float.MaxValue;
                meshlets[index] = cluster;
            }
        }
        return new(meshlets, globalIndices, ComputeMeshBounds(mesh.Positions, mesh.VertexCount));
    }

    private static MeshBoundsInfo ComputeMeshBounds(Vector3[] positions, int vertexCount)
    {
        Vector3 sceneMin = new(float.MaxValue);
        Vector3 sceneMax = new(float.MinValue);
        for (int index = 0; index < vertexCount; index++)
        {
            sceneMin = Vector3.Min(sceneMin, positions[index]);
            sceneMax = Vector3.Max(sceneMax, positions[index]);
        }

        Vector3 extent = Vector3.Max(sceneMax - sceneMin, new Vector3(1e-6f));
        float maxExtent = Math.Max(extent.X, Math.Max(extent.Y, extent.Z));
        return new MeshBoundsInfo(
            sceneMin,
            sceneMax,
            extent,
            maxExtent);
    }

    private static void SortMeshlets(
        List<ClusterMeshlet> meshlets,
        MeshBoundsInfo bounds)
    {
        meshlets.Sort((left, right) => CompareMeshlets(left, right, bounds));
    }

    private static int CompareMeshlets(
        ClusterMeshlet left,
        ClusterMeshlet right,
        MeshBoundsInfo bounds)
    {
        if (left.ParentGroupId != right.ParentGroupId)
        {
            return left.ParentGroupId.CompareTo(right.ParentGroupId);
        }

        uint leftCode = Morton3D((left.LodCenter - bounds.Min) / bounds.Extent);
        uint rightCode = Morton3D((right.LodCenter - bounds.Min) / bounds.Extent);
        return leftCode.CompareTo(rightCode);
    }

    private static List<ClusterInfo> WriteMeshPages(
        Stream stream,
        MeshInput mesh,
        MeshletBuild meshletBuild)
    {
        var writer = new MeshPageWriter(stream, mesh, meshletBuild);
        return writer.Write();
    }

    private static long WriteBvh(Stream stream, List<ClusterInfo> clusterInfos)
    {
        List<ClusterBVHNode> bvhNodes = BuildBvh(clusterInfos);
        long bvhOffset = stream.Position;
        Span<ClusterBVHNode> bvhSpan = CollectionsMarshal.AsSpan(bvhNodes);
        ReadOnlySpan<byte> bvhBytes = MemoryMarshal.Cast<ClusterBVHNode, byte>(bvhSpan);
        stream.Write(bvhBytes);
        return bvhOffset;
    }

    private static SomeEngine.Resources.Bounds CreateBounds(MeshBoundsInfo bounds)
    {
        Vector3 center = (bounds.Min + bounds.Max) * 0.5f;
        return new SomeEngine.Resources.Bounds
        {
            Center = CreateVec3(center),
            Radius = bounds.MaxExtent * 0.5f,
        };
    }

    private static SomeEngine.Resources.Vec3 CreateVec3(Vector3 value)
    {
        return new SomeEngine.Resources.Vec3
        {
            X = value.X,
            Y = value.Y,
            Z = value.Z,
        };
    }

    private static MeshRegion[] CreateMeshRegions(int count)
    {
        var regions = new MeshRegion[count];
        for (int index = 0; index < count; index++)
            regions[index] = new MeshRegion { Name = $"region_{index}" };
        return regions;
    }

    private sealed class RawMeshScratch
    {
        public RawMeshScratch(int rawVertexCount)
        {
            Remap = ArrayPool<uint>.Shared.Rent(rawVertexCount);
        }

        public uint[] Remap { get; }
        public Vector3[]? Positions { get; set; }
        public uint[]? Indices { get; set; }

        public void Return()
        {
            ArrayPool<uint>.Shared.Return(Remap);
            ReturnPositions();
            ReturnIndices();
        }

        private void ReturnPositions()
        {
            if (Positions != null)
            {
                ArrayPool<Vector3>.Shared.Return(Positions);
            }
        }

        private void ReturnIndices()
        {
            if (Indices != null)
            {
                ArrayPool<uint>.Shared.Return(Indices);
            }
        }

    }

    private sealed partial class MeshPageWriter
    {
        public MeshPageWriter(
            Stream stream,
            MeshInput mesh,
            MeshletBuild meshletBuild)
        {
            Stream = stream;
            MeshInput = mesh;
            MeshletBuild = meshletBuild;
            VertexEncodingHash = VertexEncodingCompatibility.Hash(mesh.Attributes.Compatibility);
        }

        private Stream Stream { get; }
        private MeshInput MeshInput { get; }
        private MeshletBuild MeshletBuild { get; }
        private ulong VertexEncodingHash { get; }
        private List<ClusterInfo> ClusterInfos { get; } = [];
        private List<PackedCluster> CurrentClusters { get; } = [];
        private List<EncodedCluster> CurrentEncoded { get; } = [];
        private List<EncodedPage> Pages { get; } = [];
        private int CurrentVertexBlobBytes { get; set; }
        private int CurrentIndexBytes { get; set; }
        private int PageCount { get; set; }
        private int CurrentVertexCount { get; set; }
        private int CurrentBytes { get; set; } = PageHeaderSize;

        public List<ClusterInfo> Write()
        {
            const int clustersPerBatch = 16;
            int batchCount = (MeshletBuild.Meshlets.Count + clustersPerBatch - 1) / clustersPerBatch;
            var workspaces = new ClusterEncodingWorkspace[batchCount];
            for (int batch = 0; batch < batchCount; batch++)
                workspaces[batch] = new ClusterEncodingWorkspace(
                    MeshInput with { Attributes = MeshInput.Attributes.CreateBatch() });
            var encoded = new EncodedCluster[MeshletBuild.Meshlets.Count];
            JobSystem.ExecuteParallel(new EncodeClusters(MeshletBuild, workspaces, encoded, clustersPerBatch),
                batchCount, 1);
            for (int index = 0; index < encoded.Length; index++)
            {
                AddCluster(MeshletBuild.Meshlets[index], encoded[index]);
            }

            FlushPage();
            JobSystem.ExecuteParallel(new FillPages(Pages, VertexEncodingHash), Pages.Count, 1);
            foreach (EncodedPage page in Pages)
                Stream.Write(page.Bytes);
            return ClusterInfos;
        }

    }

    private sealed partial class MeshPageWriter
    {


        private void AddCluster(ClusterMeshlet meshlet, EncodedCluster encoded)
        {
            EnsurePageSpace(ClusterPayloadSize(encoded));
            PageStarts starts = CurrentPageStarts();
            AppendLocalPayload(encoded);
            CurrentClusters.Add(CreatePackedCluster(meshlet, encoded, starts));
            ClusterInfos.Add(CreateClusterInfo(meshlet, encoded));
        }
    }

    private readonly record struct EncodeClusters(MeshletBuild Meshlets,
        ClusterEncodingWorkspace[] Workspaces, EncodedCluster[] Results, int ClustersPerBatch) : IJobParallelFor
    {
        public void Execute(int batch)
        {
            int end = Math.Min((batch + 1) * ClustersPerBatch, Results.Length);
            for (int index = batch * ClustersPerBatch; index < end; index++)
                Results[index] = Workspaces[batch].Encode(Meshlets.Meshlets[index],
                    CollectionsMarshal.AsSpan(Meshlets.GlobalIndices));
        }
    }

    private sealed partial class ClusterEncodingWorkspace(MeshInput meshInput)
    {
        private MeshInput MeshInput { get; } = meshInput;
        private Dictionary<uint, ushort> UsedMap { get; } = new(MaxVerticesPerMeshlet);
        private List<uint> LocalVertices { get; } = new(MaxVerticesPerMeshlet);
        private List<byte> LocalIndices { get; } = new(MaxTrianglesPerMeshlet * 3);

        internal EncodedCluster Encode(ClusterMeshlet meshlet, ReadOnlySpan<uint> globalIndices)
        {
            UsedMap.Clear();
            LocalVertices.Clear();
            LocalIndices.Clear();
            EncodedCluster encoded = EncodeLocalCluster(globalIndices.Slice(meshlet.IndicesOffset, meshlet.IndicesCount));
            ValidateVertReuseBatches(meshlet, encoded.VertexCount);
            return encoded;
        }

        private EncodedCluster EncodeLocalCluster(ReadOnlySpan<uint> meshletIndices)
        {
            int vertexCount = 0;
            foreach (uint globalIndex in meshletIndices)
            {
                if (!UsedMap.TryGetValue(globalIndex, out ushort localIndex))
                {
                    localIndex = (ushort)vertexCount++;
                    UsedMap[globalIndex] = localIndex;
                    LocalVertices.Add(globalIndex);
                }

                LocalIndices.Add((byte)localIndex);
            }

            EncodedClusterVertices output = MeshInput.Attributes.Encode(
                new ReadOnlySpan<Vector3>(MeshInput.Positions, 0, MeshInput.VertexCount),
                CollectionsMarshal.AsSpan(LocalVertices));
            if (output.Blob.IsEmpty)
                throw new InvalidDataException("A cluster vertex encoder returned an empty opaque blob.");
            ValidateEncodedBounds(output.BoundMin, output.BoundMax);
            byte[] data = new byte[16 + output.Blob.Length];
            int positionBytes = output.PositionBytes == 0 ? output.Blob.Length : output.PositionBytes;
            int attributesOffset = output.PositionBytes == 0 ? 16 : 16 + positionBytes;
            int attributesBytes = output.PositionBytes == 0 ? output.Blob.Length : output.Blob.Length - positionBytes;
            BinaryPrimitives.WriteUInt32LittleEndian(data, 16);
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4), (uint)positionBytes);
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(8), (uint)attributesOffset);
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(12), (uint)attributesBytes);
            output.Blob.Span.CopyTo(data.AsSpan(16));
            return new EncodedCluster(
                vertexCount,
                data,
                LocalIndices.ToArray(),
                output.BoundMin,
                output.BoundMax);
        }

        private static void ValidateEncodedBounds(Vector3 min, Vector3 max)
        {
            if (!float.IsFinite(min.X) || !float.IsFinite(min.Y) || !float.IsFinite(min.Z) ||
                !float.IsFinite(max.X) || !float.IsFinite(max.Y) || !float.IsFinite(max.Z) ||
                min.X > max.X || min.Y > max.Y || min.Z > max.Z)
            {
                throw new InvalidDataException(
                    $"A cluster vertex encoder returned invalid conservative bounds [{min}..{max}].");
            }
        }
    }

    private sealed partial class ClusterEncodingWorkspace
    {


        private void ValidateVertReuseBatches(ClusterMeshlet meshlet, int vertexCount)
        {
            int triangleCount = LocalIndices.Count / 3;
            Span<byte> indices = CollectionsMarshal.AsSpan(LocalIndices);
            Span<int> rangeEnds = stackalloc int[3]
            {
                meshlet.Range0End,
                meshlet.Range1End,
                triangleCount,
            };
            int rangeCount = meshlet.Range0End == triangleCount
                ? 1
                : (meshlet.Range1End == triangleCount ? 2 : 3);

            int triangleStart = 0;
            int flatBatchIndex = 0;
            for (int rangeIndex = 0; rangeIndex < rangeCount; rangeIndex++)
            {
                int batchCount = checked((int)ReadVertReuseBits(meshlet, rangeIndex * 4, 4));
                if (batchCount <= 0)
                    throw new InvalidDataException("A material range has no vertex-reuse batch.");

                int rangeEnd = rangeEnds[rangeIndex];
                for (int batchIndex = 0; batchIndex < batchCount; batchIndex++)
                {
                    int batchTriangleCount = checked((int)ReadVertReuseBits(
                        meshlet,
                        12 + flatBatchIndex * 5,
                        5)) + 1;
                    int batchEnd = checked(triangleStart + batchTriangleCount);
                    if (batchTriangleCount > 32 || batchEnd > rangeEnd)
                        throw new InvalidDataException("A vertex-reuse batch exceeds its material range.");

                    ValidateVertReuseBatch(
                        indices,
                        triangleStart,
                        batchEnd,
                        flatBatchIndex,
                        triangleCount,
                        vertexCount);
                    triangleStart = batchEnd;
                    flatBatchIndex++;
                }

                if (triangleStart != rangeEnd)
                    throw new InvalidDataException("Vertex-reuse batches do not exactly cover their material range.");
            }

            for (int rangeIndex = rangeCount; rangeIndex < 3; rangeIndex++)
            {
                if (ReadVertReuseBits(meshlet, rangeIndex * 4, 4) != 0)
                    throw new InvalidDataException("Unused material ranges contain vertex-reuse batch metadata.");
            }
        }

        private static uint ReadVertReuseBits(
            in ClusterMeshlet meshlet,
            int bitOffset,
            int bitCount)
        {
            if (bitOffset < 0 || bitCount <= 0 || bitCount > 31 || bitOffset + bitCount > 128)
                throw new InvalidDataException("Vertex-reuse batch metadata exceeds its inline encoding.");

            int wordIndex = bitOffset >> 5;
            int wordBit = bitOffset & 31;
            uint first = VertReuseWord(meshlet, wordIndex) >> wordBit;
            int lowBits = Math.Min(bitCount, 32 - wordBit);
            uint value = first;
            if (lowBits < bitCount)
                value |= VertReuseWord(meshlet, wordIndex + 1) << lowBits;
            return value & ((1u << bitCount) - 1u);
        }

        private static uint VertReuseWord(in ClusterMeshlet meshlet, int index)
            => index switch
            {
                0 => meshlet.VertReuseBatchWord0,
                1 => meshlet.VertReuseBatchWord1,
                2 => meshlet.VertReuseBatchWord2,
                3 => meshlet.VertReuseBatchWord3,
                _ => throw new ArgumentOutOfRangeException(nameof(index)),
            };

        private static void ValidateVertReuseBatch(
            Span<byte> indices,
            int batchStart,
            int batchEnd,
            int batchIndex,
            int triangleCount,
            int vertexCount)
        {
            ulong usedVertices = 0;
            for (int triangle = batchStart; triangle < batchEnd; triangle++)
            {
                for (int component = 0; component < 3; component++)
                {
                    byte vertex = indices[triangle * 3 + component];
                    if (vertex >= vertexCount)
                    {
                        throw new InvalidDataException(
                            $"Vertex-reuse batch {batchIndex} references local vertex {vertex}, " +
                            $"but the cluster has {vertexCount} vertices.");
                    }
                    usedVertices |= 1UL << vertex;
                }
            }

            int uniqueVertexCount = BitOperations.PopCount(usedVertices);
            if (uniqueVertexCount > 32)
            {
                throw new InvalidDataException(
                    $"Vertex-reuse batch {batchIndex} has {uniqueVertexCount} unique vertices " +
                    $"(max 32), cluster triangles={triangleCount}, vertices={vertexCount}.");
            }
        }
    }

    private sealed partial class MeshPageWriter
    {
        private static int ClusterPayloadSize(EncodedCluster encoded)
        {
            return checked(PackedCluster.SizeInBytes + encoded.Blob.Length + encoded.Indices.Length);
        }

        private void EnsurePageSpace(int bytesToAdd)
        {
            if (CurrentBytes + bytesToAdd > PageSize ||
                CurrentIndexBytes > MaxEncodedTriangleStart ||
                CurrentClusters.Count >= 0x1000)
            {
                FlushPage();
            }
            if (CurrentBytes + bytesToAdd > PageSize)
            {
                throw new InvalidDataException(
                    $"A single encoded cluster requires {bytesToAdd} payload bytes, exceeding the " +
                    $"{PageSize - PageHeaderSize}-byte page payload limit.");
            }
        }

        private PageStarts CurrentPageStarts()
        {
            uint vertexBlobStart = checked((uint)CurrentVertexBlobBytes);
            uint triangleStart = (uint)CurrentIndexBytes;
            ValidatePageStarts(triangleStart);
            return new PageStarts(vertexBlobStart, triangleStart);
        }

        private void ValidatePageStarts(uint triangleStart)
        {
            if (triangleStart > ushort.MaxValue)
            {
                throw new InvalidOperationException(
                    $"Cluster page triangle start exceeds encoded range: {triangleStart} > {ushort.MaxValue}");
            }

            if (CurrentClusters.Count > 0xFFF)
            {
                throw new InvalidOperationException(
                    $"Cluster page cluster start exceeds BVH leaf encoding range: {CurrentClusters.Count} > 4095");
            }
        }

        private void AppendLocalPayload(EncodedCluster encoded)
        {
            int payloadSize = ClusterPayloadSize(encoded);
            CurrentEncoded.Add(encoded);
            CurrentVertexBlobBytes = checked(CurrentVertexBlobBytes + encoded.Blob.Length);
            CurrentIndexBytes = checked(CurrentIndexBytes + encoded.Indices.Length);
            CurrentVertexCount = checked(CurrentVertexCount + encoded.VertexCount);
            CurrentBytes += payloadSize;
        }

        private PackedCluster CreatePackedCluster(
            ClusterMeshlet meshlet,
            EncodedCluster encoded,
            PageStarts starts)
        {
            return new PackedCluster
            {
                VertexBlobOffset = starts.VertexBlob,
                VertexBlobSize = checked((uint)encoded.Blob.Length),
                ExtensionOffset = uint.MaxValue,
                ExtensionSize = 0,
                LODCenter = meshlet.SelfLodCenter,
                LODRadius = meshlet.SelfLodRadius,
                LODErrorHalf = BitConverter.HalfToUInt16Bits(
                    (Half)EncodeFiniteLodError(meshlet.Error)),
                TriangleStart = (ushort)starts.Triangle,
                GroupId = (short)meshlet.GroupId,
                PackedCounts = PackedCounts(
                    encoded.VertexCount,
                    encoded.Indices.Length / 3,
                    meshlet.Level),
                PackedMaterials = PackedMaterials(meshlet),
                PackedRanges = PackedRanges(meshlet),
                MaterialTableOffset = 0xFFFFFFFF,
                VertReuseBatchWord0 = meshlet.VertReuseBatchWord0,
                VertReuseBatchWord1 = meshlet.VertReuseBatchWord1,
                VertReuseBatchWord2 = meshlet.VertReuseBatchWord2,
                VertReuseBatchWord3 = meshlet.VertReuseBatchWord3,
                BoundMin = encoded.BoundMin,
                BoundMax = encoded.BoundMax,
            };
        }
    }

    private sealed partial class MeshPageWriter
    {


        private static uint PackedCounts(int vertexCount, int triangleCount, int level)
            => (uint)vertexCount
                | ((uint)triangleCount << 8)
                | ((uint)(byte)level << 16);

        private static uint PackedMaterials(ClusterMeshlet meshlet)
            => (uint)meshlet.Mat0 | ((uint)meshlet.Mat1 << 8) | ((uint)meshlet.Mat2 << 16);

        private static uint PackedRanges(ClusterMeshlet meshlet)
            => (uint)meshlet.Range0End | ((uint)meshlet.Range1End << 8);

        private ClusterInfo CreateClusterInfo(ClusterMeshlet meshlet, EncodedCluster encoded)
        {
            return new ClusterInfo
            {
                BoundMin = encoded.BoundMin,
                BoundMax = encoded.BoundMax,
                LODSphere = new Vector4(meshlet.LodCenter.X, meshlet.LodCenter.Y, meshlet.LodCenter.Z, meshlet.LodRadius),
                LODError = EncodeFiniteLodError(meshlet.ParentError),
                PageIndex = checked((uint)PageCount),
                ClusterStart = (uint)(CurrentClusters.Count - 1),
                GroupId = meshlet.Level == 0 ? -1 : meshlet.GroupId,
                ParentGroupId = meshlet.ParentGroupId,
            };
        }

        private static float EncodeFiniteLodError(float value)
        {
            if (float.IsNaN(value) || value < 0.0f)
                throw new InvalidDataException($"Cluster LOD error {value} is invalid.");

            // The hierarchy uses float.MaxValue to mark a terminal/root LOD. Both the page
            // cluster format and the BVH need a finite value, while the largest finite half already
            // keeps that terminal LOD selected throughout the supported projection range.
            // The BVH rejects the finer group with the same error that the
            // packed coarse clusters accept. Different rounding opens a gap
            // or draws both groups at the LOD transition.
            return (float)(Half)MathF.Min(value, (float)Half.MaxValue);
        }

        private void FlushPage()
        {
            if (CurrentClusters.Count == 0)
            {
                return;
            }

            PageOffsets offsets = ComputePageOffsets();
            Pages.Add(new EncodedPage(CurrentClusters.ToArray(), CurrentEncoded.ToArray(),
                offsets, CurrentVertexCount, new byte[offsets.TotalSize]));
            PageCount++;
            ClearCurrentPage();
        }

        private PageOffsets ComputePageOffsets()
        {
            uint clustersOffset = PageHeaderSize;
            int clustersSize = checked(CurrentClusters.Count * PackedCluster.SizeInBytes);
            uint vertexBlobsOffset = checked(clustersOffset + (uint)clustersSize);
            int vertexBlobsSize = CurrentVertexBlobBytes;
            uint indicesOffset = checked(vertexBlobsOffset + (uint)vertexBlobsSize);
            int indicesSize = CurrentIndexBytes;
            uint extensionDataOffset = checked(indicesOffset + (uint)indicesSize);
            return new PageOffsets(
                clustersOffset,
                clustersSize,
                vertexBlobsOffset,
                vertexBlobsSize,
                indicesOffset,
                indicesSize,
                extensionDataOffset,
                0,
                checked((int)extensionDataOffset));
        }

    }

    private sealed partial class MeshPageWriter
    {


        private void ClearCurrentPage()
        {
            CurrentClusters.Clear();
            CurrentEncoded.Clear();
            CurrentVertexBlobBytes = 0;
            CurrentIndexBytes = 0;
            CurrentVertexCount = 0;
            CurrentBytes = PageHeaderSize;
        }
    }

    private sealed record EncodedPage(PackedCluster[] Clusters, EncodedCluster[] Encoded,
        PageOffsets Offsets, int VertexCount, byte[] Bytes);

    private readonly record struct FillPages(List<EncodedPage> Pages, ulong VertexEncodingHash) : IJobParallelFor
    {
        public void Execute(int index)
        {
            EncodedPage page = Pages[index];
            PageOffsets offsets = page.Offsets;
            Span<byte> bytes = page.Bytes;
            ref MeshPageHeader header = ref Unsafe.As<byte, MeshPageHeader>(ref bytes[0]);
            header.ClusterCount = (uint)page.Clusters.Length;
            header.TotalVertexCount = checked((uint)page.VertexCount);
            header.TotalTriangleCount = (uint)(offsets.IndicesSize / 3);
            header.VertexBlobBytes = checked((uint)offsets.VertexBlobsSize);
            header.ClustersOffset = offsets.ClustersOffset;
            header.VertexBlobsOffset = offsets.VertexBlobsOffset;
            header.IndicesOffset = offsets.IndicesOffset;
            header.ExtensionDataOffset = offsets.ExtensionDataOffset;
            header.VertexEncodingHash = VertexEncodingHash;
            MemoryMarshal.AsBytes(page.Clusters.AsSpan()).CopyTo(bytes[(int)offsets.ClustersOffset..]);
            int vertexOffset = (int)offsets.VertexBlobsOffset;
            int triangleOffset = (int)offsets.IndicesOffset;
            foreach (EncodedCluster cluster in page.Encoded)
            {
                cluster.Blob.CopyTo(bytes[vertexOffset..]);
                cluster.Indices.CopyTo(bytes[triangleOffset..]);
                vertexOffset += cluster.Blob.Length;
                triangleOffset += cluster.Indices.Length;
            }
        }
    }

    private readonly record struct MeshInput(
        Vector3[] Positions,
        uint[] Indices,
        int VertexCount,
        int IndexCount,
        VertexData Attributes,
        float[] MaterialIndices);

    private readonly record struct MeshletBuild(
        List<ClusterMeshlet> Meshlets,
        List<uint> GlobalIndices,
        MeshBoundsInfo Bounds);

    private readonly record struct MeshBoundsInfo(
        Vector3 Min,
        Vector3 Max,
        Vector3 Extent,
        float MaxExtent);

    private readonly record struct EncodedCluster(
        int VertexCount,
        byte[] Blob,
        byte[] Indices,
        Vector3 BoundMin,
        Vector3 BoundMax);

    private readonly record struct PageStarts(uint VertexBlob, uint Triangle);

    private readonly record struct PageOffsets(
        uint ClustersOffset,
        int ClustersSize,
        uint VertexBlobsOffset,
        int VertexBlobsSize,
        uint IndicesOffset,
        int IndicesSize,
        uint ExtensionDataOffset,
        int ExtensionDataSize,
        int TotalSize);
}
