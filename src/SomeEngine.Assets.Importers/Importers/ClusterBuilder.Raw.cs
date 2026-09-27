using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using MeshOptimizer;
using SomeEngine.Assets.Data;
using SomeEngine.Assets.Schema;
using Mesh = SomeEngine.Assets.Schema.Mesh;
using ValueType = SomeEngine.Assets.Data.ValueType;

namespace SomeEngine.Assets.Importers;

public static partial class ClusterBuilder
{
    private static Mesh ProcessRawCore(
        Vector3[] rawPos,
        List<RawAttribute> rawAttributes,
        uint[] rawIndices,
        IReadOnlyList<MeshMaterialSlot> materialSlots,
        string name,
        IClusterVertexEncoder vertexEncoder)
    {
        ArgumentNullException.ThrowIfNull(vertexEncoder);
        ArgumentException.ThrowIfNullOrWhiteSpace(vertexEncoder.Compatibility);
        if (materialSlots.Count is 0 or > 256)
            throw new ArgumentOutOfRangeException(
                nameof(materialSlots),
                "Cluster meshes require between one and 256 material/deform cook slots.");
        using FileStream stream = CreatePageStream();
        var scratch = new RawMeshScratch(rawPos.Length);
        try
        {
            PreparedRawMesh prepared = PrepareRawMesh(rawPos, rawAttributes, rawIndices, scratch);
            MeshletBuild meshletBuild = BuildMeshletData(prepared, materialSlots);
            SortMeshlets(meshletBuild.Meshlets, meshletBuild.Bounds);
            List<ClusterInfo> clusterInfos = WriteMeshPages(
                stream,
                scratch.PageBuffer,
                prepared,
                meshletBuild,
                vertexEncoder);
            long bvhOffset = WriteBvh(stream, clusterInfos);
            return CreateMeshAsset(
                name,
                materialSlots,
                stream,
                meshletBuild.Bounds,
                vertexEncoder.Compatibility,
                bvhOffset);
        }
        finally
        {
            scratch.Return();
        }
    }

    private static FileStream CreatePageStream()
    {
        return new FileStream(
            Path.GetTempFileName(),
            FileMode.Create,
            FileAccess.ReadWrite,
            FileShare.None,
            4096,
            FileOptions.DeleteOnClose);
    }

    private static PreparedRawMesh PrepareRawMesh(
        Vector3[] rawPositions,
        List<RawAttribute> rawAttributes,
        uint[] rawIndices,
        RawMeshScratch scratch)
    {
        nuint vertexCount = BuildRemap(
            scratch.Remap.AsSpan(0, rawPositions.Length),
            rawIndices.AsSpan(),
            rawPositions,
            rawAttributes);
        scratch.Positions = ArrayPool<Vector3>.Shared.Rent((int)vertexCount);
        scratch.Indices = ArrayPool<uint>.Shared.Rent(rawIndices.Length);
        RemapPositions(rawPositions, scratch.Remap, vertexCount, scratch.Positions);
        RemapIndices(rawIndices, scratch.Remap.AsSpan(0, rawPositions.Length), scratch.Indices);
        List<RawAttribute> attributes = RemapAttributes(rawPositions.Length, rawAttributes, scratch, vertexCount);
        Meshopt.OptimizeVertexCache(
            scratch.Indices.AsSpan(0, rawIndices.Length),
            scratch.Indices.AsSpan(0, rawIndices.Length),
            vertexCount);
        return new PreparedRawMesh(
            scratch.Positions,
            scratch.Indices,
            (int)vertexCount,
            rawIndices.Length,
            attributes);
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

    private static List<RawAttribute> RemapAttributes(
        int rawVertexCount,
        List<RawAttribute> rawAttributes,
        RawMeshScratch scratch,
        nuint vertexCount)
    {
        var attributes = new List<RawAttribute>(rawAttributes.Count);
        foreach (RawAttribute attribute in rawAttributes)
        {
            float[] data = RemapAttributeData(rawVertexCount, attribute, scratch, vertexCount);
            attributes.Add(
                new RawAttribute(
                    attribute.Name,
                    data,
                    attribute.Dimension,
                    attribute.TargetType,
                    attribute.NumComponents,
                    attribute.Normalized));
        }

        return attributes;
    }

    private static float[] RemapAttributeData(
        int rawVertexCount,
        RawAttribute attribute,
        RawMeshScratch scratch,
        nuint vertexCount)
    {
        float[] destination = ArrayPool<float>.Shared.Rent((int)vertexCount * attribute.Dimension);
        scratch.AttributeBuffers.Add(destination);
        for (int oldIndex = 0; oldIndex < rawVertexCount; oldIndex++)
        {
            uint newIndex = scratch.Remap[oldIndex];
            if (newIndex != uint.MaxValue && newIndex < vertexCount)
            {
                CopyAttributeValues(attribute, oldIndex, destination, (int)newIndex);
            }
        }

        return destination;
    }

    private static void CopyAttributeValues(
        RawAttribute attribute,
        int sourceIndex,
        float[] destination,
        int destinationIndex)
    {
        int sourceBase = sourceIndex * attribute.Dimension;
        int destinationBase = destinationIndex * attribute.Dimension;
        for (int component = 0; component < attribute.Dimension; component++)
        {
            destination[destinationBase + component] = attribute.Data[sourceBase + component];
        }
    }

    private static MeshletBuild BuildMeshletData(
        PreparedRawMesh prepared,
        IReadOnlyList<MeshMaterialSlot> materialSlots)
    {
        var meshlets = new List<BuilderMeshlet>();
        var globalIndices = new List<uint>();
        float[] materialIndices = ExtractMaterialIndices(prepared);
        BuildClusterLod(
            ClusterLodConfig.GetDefault() with { ClusterSpatial = true },
            new ReadOnlySpan<Vector3>(prepared.Positions, 0, prepared.VertexCount),
            new ReadOnlySpan<uint>(prepared.Indices, 0, prepared.IndexCount),
            materialIndices,
            meshlets,
            globalIndices);
        RemoveInternalMaterialAttribute(prepared.Attributes);
        MeshBoundsInfo bounds = ComputeMeshBounds(prepared.Positions, prepared.VertexCount);
        return new MeshletBuild(meshlets, globalIndices, bounds);
    }

    private static float[] ExtractMaterialIndices(PreparedRawMesh prepared)
    {
        RawAttribute? materialIndex = prepared.Attributes.FirstOrDefault(static attr => attr.Name == "_MATERIAL_INDEX");
        return materialIndex?.Data ?? new float[prepared.VertexCount];
    }

    private static void RemoveInternalMaterialAttribute(List<RawAttribute> attributes)
    {
        RawAttribute? materialIndex = attributes.FirstOrDefault(static attr => attr.Name == "_MATERIAL_INDEX");
        if (materialIndex != null)
        {
            attributes.Remove(materialIndex);
        }
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
        List<BuilderMeshlet> meshlets,
        MeshBoundsInfo bounds)
    {
        meshlets.Sort((left, right) => CompareMeshlets(left, right, bounds));
    }

    private static int CompareMeshlets(
        BuilderMeshlet left,
        BuilderMeshlet right,
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
        FileStream stream,
        byte[] pageBuffer,
        PreparedRawMesh prepared,
        MeshletBuild meshletBuild,
        IClusterVertexEncoder vertexEncoder)
    {
        var writer = new MeshPageWriter(stream, pageBuffer, prepared, meshletBuild, vertexEncoder);
        return writer.Write();
    }

    private static long WriteBvh(FileStream stream, List<ClusterInfo> clusterInfos)
    {
        List<ClusterBVHNode> bvhNodes = BuildBvh(clusterInfos);
        long bvhOffset = stream.Position;
        Span<ClusterBVHNode> bvhSpan = CollectionsMarshal.AsSpan(bvhNodes);
        ReadOnlySpan<byte> bvhBytes = MemoryMarshal.Cast<ClusterBVHNode, byte>(bvhSpan);
        stream.Write(bvhBytes);
        return bvhOffset;
    }

    private static Mesh CreateMeshAsset(
        string name,
        IReadOnlyList<MeshMaterialSlot> materialSlots,
        FileStream stream,
        MeshBoundsInfo bounds,
        string vertexEncoding,
        long bvhOffset)
    {
        var meshAsset = new Mesh
        {
            Name = name,
            Bounds = CreateBounds(bounds),
            VertexEncoding = vertexEncoding,
            Payload = new byte[stream.Length],
            BvhOffset = (ulong)bvhOffset,
            Regions = CreateMeshRegions(materialSlots.Count),
            MaterialGuids = materialSlots
                .Where(static slot => !slot.MaterialGuid.IsEmpty)
                .Select(static slot => slot.MaterialGuid.ToFlatString())
                .Distinct(StringComparer.Ordinal)
                .ToArray(),
        };
        stream.Seek(0, SeekOrigin.Begin);
        meshAsset.Payload!.Value.Span.Clear();
        stream.ReadExactly(meshAsset.Payload.Value.Span);
        return meshAsset;
    }

    private static SomeEngine.Assets.Schema.Bounds CreateBounds(MeshBoundsInfo bounds)
    {
        Vector3 center = (bounds.Min + bounds.Max) * 0.5f;
        return new SomeEngine.Assets.Schema.Bounds
        {
            Center = CreateVec3(center),
            Radius = bounds.MaxExtent * 0.5f,
        };
    }

    private static SomeEngine.Assets.Schema.Vec3 CreateVec3(Vector3 value)
    {
        return new SomeEngine.Assets.Schema.Vec3
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
            PageBuffer = ArrayPool<byte>.Shared.Rent(PageSize + 65536);
            Remap = ArrayPool<uint>.Shared.Rent(rawVertexCount);
        }

        public byte[] PageBuffer { get; }
        public uint[] Remap { get; }
        public Vector3[]? Positions { get; set; }
        public uint[]? Indices { get; set; }
        public List<float[]> AttributeBuffers { get; } = [];

        public void Return()
        {
            ArrayPool<byte>.Shared.Return(PageBuffer);
            ArrayPool<uint>.Shared.Return(Remap);
            ReturnPositions();
            ReturnIndices();
            ReturnAttributes();
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

        private void ReturnAttributes()
        {
            foreach (float[] buffer in AttributeBuffers)
            {
                ArrayPool<float>.Shared.Return(buffer);
            }
        }
    }

    private sealed partial class MeshPageWriter
    {
        public MeshPageWriter(
            FileStream stream,
            byte[] pageBuffer,
            PreparedRawMesh mesh,
            MeshletBuild meshletBuild,
            IClusterVertexEncoder vertexEncoder)
        {
            Stream = stream;
            PageBuffer = pageBuffer;
            Mesh = mesh;
            MeshletBuild = meshletBuild;
            VertexEncoder = vertexEncoder;
            VertexEncodingHash = VertexEncodingCompatibility.Hash(vertexEncoder.Compatibility);
        }

        private FileStream Stream { get; }
        private byte[] PageBuffer { get; }
        private PreparedRawMesh Mesh { get; }
        private MeshletBuild MeshletBuild { get; }
        private IClusterVertexEncoder VertexEncoder { get; }
        private ulong VertexEncodingHash { get; }
        private List<ClusterInfo> ClusterInfos { get; } = [];
        private List<PackedCluster> CurrentClusters { get; } = [];
        private List<byte> CurrentVertexBlobs { get; } = [];
        private List<byte> CurrentIndices { get; } = [];
        private Dictionary<uint, ushort> UsedMap { get; } = new(MaxVerticesPerMeshlet);
        private List<uint> LocalVertices { get; } = new(MaxVerticesPerMeshlet);
        private List<byte> LocalIndices { get; } = new(MaxTrianglesPerMeshlet * 3);
        private int PageCount { get; set; }
        private int CurrentVertexCount { get; set; }
        private int CurrentBytes { get; set; } = PageHeaderSize;

        public List<ClusterInfo> Write()
        {
            Span<uint> globalIndices = CollectionsMarshal.AsSpan(MeshletBuild.GlobalIndices);
            foreach (BuilderMeshlet meshlet in MeshletBuild.Meshlets)
            {
                AddCluster(meshlet, globalIndices);
            }

            FlushPage();
            return ClusterInfos;
        }

    }

    private sealed partial class MeshPageWriter
    {


        private void AddCluster(BuilderMeshlet meshlet, Span<uint> globalIndices)
        {
            ResetLocalBuffers();
            ReadOnlySpan<uint> meshletIndices = globalIndices.Slice(meshlet.IndicesOffset, meshlet.IndicesCount);
            EncodedCluster encoded = EncodeLocalCluster(meshletIndices);
            ValidateVertReuseBatches(meshlet, encoded.VertexCount);
            EnsurePageSpace(ClusterPayloadSize(encoded));
            PageStarts starts = CurrentPageStarts();
            AppendLocalPayload(encoded);
            CurrentClusters.Add(CreatePackedCluster(meshlet, encoded, starts));
            ClusterInfos.Add(CreateClusterInfo(meshlet, encoded));
        }

        private void ResetLocalBuffers()
        {
            UsedMap.Clear();
            LocalVertices.Clear();
            LocalIndices.Clear();
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

            EncodedClusterVertices output = VertexEncoder.Encode(
                new ReadOnlySpan<Vector3>(Mesh.Positions, 0, Mesh.VertexCount),
                Mesh.Attributes,
                CollectionsMarshal.AsSpan(LocalVertices));
            if (output.Blob.IsEmpty)
                throw new InvalidDataException("A cluster vertex encoder returned an empty opaque blob.");
            ValidateEncodedBounds(output.BoundMin, output.BoundMax);
            return new EncodedCluster(
                vertexCount,
                output.Blob.ToArray(),
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

    private sealed partial class MeshPageWriter
    {


        private void ValidateVertReuseBatches(BuilderMeshlet meshlet, int vertexCount)
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
            in BuilderMeshlet meshlet,
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

        private static uint VertReuseWord(in BuilderMeshlet meshlet, int index)
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
        private int ClusterPayloadSize(EncodedCluster encoded)
        {
            return checked(PackedCluster.SizeInBytes + encoded.Blob.Length + LocalIndices.Count);
        }

        private void EnsurePageSpace(int bytesToAdd)
        {
            if (CurrentBytes + bytesToAdd > PageSize ||
                CurrentIndices.Count > MaxEncodedTriangleStart ||
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
            uint vertexBlobStart = checked((uint)CurrentVertexBlobs.Count);
            uint triangleStart = (uint)CurrentIndices.Count;
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
            CurrentVertexBlobs.AddRange(encoded.Blob);
            CurrentIndices.AddRange(LocalIndices);
            CurrentVertexCount = checked(CurrentVertexCount + encoded.VertexCount);
            CurrentBytes += payloadSize;
        }

        private PackedCluster CreatePackedCluster(
            BuilderMeshlet meshlet,
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


        private uint PackedCounts(int vertexCount, int level)
            => (uint)vertexCount
                | ((uint)(LocalIndices.Count / 3) << 8)
                | ((uint)(byte)level << 16);

        private static uint PackedMaterials(BuilderMeshlet meshlet)
            => (uint)meshlet.Mat0 | ((uint)meshlet.Mat1 << 8) | ((uint)meshlet.Mat2 << 16);

        private static uint PackedRanges(BuilderMeshlet meshlet)
            => (uint)meshlet.Range0End | ((uint)meshlet.Range1End << 8);

        private ClusterInfo CreateClusterInfo(BuilderMeshlet meshlet, EncodedCluster encoded)
        {
            return new ClusterInfo
            {
                BoundMin = encoded.BoundMin,
                BoundMax = encoded.BoundMax,
                LODSphere = new Vector4(meshlet.LodCenter.X, meshlet.LodCenter.Y, meshlet.LodCenter.Z, meshlet.LodRadius),
                LODError = EncodeFiniteLodError(meshlet.ParentError),
                PageIndex = checked((uint)PageCount),
                ClusterStart = (uint)(CurrentClusters.Count - 1),
                ParentGroupId = meshlet.ParentGroupId,
            };
        }

        private static float EncodeFiniteLodError(float value)
        {
            if (float.IsNaN(value) || value < 0.0f)
                throw new InvalidDataException($"Cluster LOD error {value} is invalid.");

            // The hierarchy uses float.MaxValue to mark a terminal/root LOD. Both the page
            // cluster ABI and the BVH need a finite value, while the largest finite half already
            // keeps that terminal LOD selected throughout the supported projection range.
            return MathF.Min(value, (float)Half.MaxValue);
        }

        private void FlushPage()
        {
            if (CurrentClusters.Count == 0)
            {
                return;
            }

            PageOffsets offsets = ComputePageOffsets();
            if (offsets.TotalSize > PageBuffer.Length)
            {
                throw new Exception($"Page buffer overflow: {offsets.TotalSize} > {PageBuffer.Length}");
            }

            WritePage(offsets);
            PageCount++;
            ClearCurrentPage();
        }

        private PageOffsets ComputePageOffsets()
        {
            uint clustersOffset = PageHeaderSize;
            int clustersSize = checked(CurrentClusters.Count * PackedCluster.SizeInBytes);
            uint vertexBlobsOffset = checked(clustersOffset + (uint)clustersSize);
            int vertexBlobsSize = CurrentVertexBlobs.Count;
            uint indicesOffset = checked(vertexBlobsOffset + (uint)vertexBlobsSize);
            int indicesSize = CurrentIndices.Count;
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

        private void WritePage(PageOffsets offsets)
        {
            Array.Clear(PageBuffer, 0, offsets.TotalSize);
            var pageSpan = new Span<byte>(PageBuffer);
            WritePageHeader(pageSpan, offsets);
            WritePagePayload(pageSpan, offsets);
            Stream.Write(PageBuffer, 0, offsets.TotalSize);
        }

        private void WritePageHeader(Span<byte> pageSpan, PageOffsets offsets)
        {
            ref MeshPageHeader header = ref Unsafe.As<byte, MeshPageHeader>(ref pageSpan[0]);
            header.ClusterCount = (uint)CurrentClusters.Count;
            header.TotalVertexCount = checked((uint)CurrentVertexCount);
            header.TotalTriangleCount = (uint)(CurrentIndices.Count / 3);
            header.VertexBlobBytes = checked((uint)CurrentVertexBlobs.Count);
            header.ClustersOffset = offsets.ClustersOffset;
            header.VertexBlobsOffset = offsets.VertexBlobsOffset;
            header.IndicesOffset = offsets.IndicesOffset;
            header.ExtensionDataOffset = offsets.ExtensionDataOffset;
            header.VertexEncodingHash = VertexEncodingHash;
            header.ExtensionDataSize = checked((uint)offsets.ExtensionDataSize);
            header.Reserved = 0;
        }
    }

    private sealed partial class MeshPageWriter
    {


        private void WritePagePayload(Span<byte> pageSpan, PageOffsets offsets)
        {
            MemoryMarshal.Cast<PackedCluster, byte>(CollectionsMarshal.AsSpan(CurrentClusters))
                .CopyTo(pageSpan.Slice((int)offsets.ClustersOffset, offsets.ClustersSize));
            CollectionsMarshal.AsSpan(CurrentVertexBlobs)
                .CopyTo(pageSpan.Slice((int)offsets.VertexBlobsOffset, offsets.VertexBlobsSize));
            CollectionsMarshal.AsSpan(CurrentIndices)
                .CopyTo(pageSpan.Slice((int)offsets.IndicesOffset, offsets.IndicesSize));
        }

        private void ClearCurrentPage()
        {
            CurrentClusters.Clear();
            CurrentVertexBlobs.Clear();
            CurrentIndices.Clear();
            CurrentVertexCount = 0;
            CurrentBytes = PageHeaderSize;
        }
    }

    private readonly record struct PreparedRawMesh(
        Vector3[] Positions,
        uint[] Indices,
        int VertexCount,
        int IndexCount,
        List<RawAttribute> Attributes);

    private readonly record struct MeshletBuild(
        List<BuilderMeshlet> Meshlets,
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
