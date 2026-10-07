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
using SomeEngine.Assets.Schema;
using SomeEngine.Resources;
using SomeEngine.Serialization.Containers;
using SomeEngine.Serialization.IO;
using MeshAsset = SomeEngine.Assets.Schema.MeshAsset;
using ValueType = SomeEngine.Resources.Geometry.ValueType;
using Stream = System.IO.Stream;

namespace SomeEngine.Assets.Importers;

public static partial class ClusterBuilder
{
    private static MeshAsset ProcessRawCore(
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
        using var stream = new MemoryStream();
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
            uint[] lowestDetailPageIndices = clusterInfos
                .Where(static cluster => cluster.LODError >= (float)Half.MaxValue)
                .Select(static cluster => cluster.PageIndex)
                .Distinct()
                .Order()
                .ToArray();
            if (lowestDetailPageIndices.Length == 0)
                lowestDetailPageIndices = [clusterInfos.Max(static cluster => cluster.PageIndex)];
            long bvhOffset = WriteBvh(stream, clusterInfos);
            byte[] content = stream.ToArray();
            return CreateMeshAsset(
                name,
                materialSlots,
                content,
                meshletBuild.Bounds,
                vertexEncoder.Compatibility,
                bvhOffset,
                lowestDetailPageIndices);
        }
        finally
        {
            scratch.Return();
        }
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
        Stream stream,
        byte[] pageBuffer,
        PreparedRawMesh prepared,
        MeshletBuild meshletBuild,
        IClusterVertexEncoder vertexEncoder)
    {
        var writer = new MeshPageWriter(stream, pageBuffer, prepared, meshletBuild, vertexEncoder);
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

    private static MeshAsset CreateMeshAsset(
        string name,
        IReadOnlyList<MeshMaterialSlot> materialSlots,
        ReadOnlyMemory<byte> payload,
        MeshBoundsInfo bounds,
        string vertexEncoding,
        long bvhOffset,
        uint[] lowestDetailPageIndices)
    {
        using var geometry = new MeshBlock
        {
            Bounds = CreateBounds(bounds),
            Regions = CreateMeshRegions(materialSlots.Count),
        };
        using var materials = new MeshMaterialsBlock
        {
            Materials = materialSlots
                .Select(static slot => new AssetId<MaterialAsset>(slot.MaterialGuid))
                .ToArray(),
        };
        using var clusters = new ClusterMeshBlock
        {
            VertexEncoding = vertexEncoding,
            LowestDetailPageIndices = lowestDetailPageIndices,
        };
        clusters.SetPayload(payload, checked((ulong)bvhOffset));
        var meshAsset = new MeshAsset { Name = name };
        try
        {
            meshAsset.SetBlock(geometry);
            meshAsset.SetBlock(clusters);
            meshAsset.SetBlock(materials);
            return meshAsset;
        }
        catch { meshAsset.Dispose(); throw; }
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
            Stream stream,
            byte[] pageBuffer,
            PreparedRawMesh mesh,
            MeshletBuild meshletBuild,
            IClusterVertexEncoder vertexEncoder)
        {
            Stream = stream;
            PageBuffer = pageBuffer;
            MeshInput = mesh;
            MeshletBuild = meshletBuild;
            VertexEncoder = vertexEncoder;
            VertexEncodingHash = VertexEncodingCompatibility.Hash(vertexEncoder.Compatibility);
        }

        private Stream Stream { get; }
        private byte[] PageBuffer { get; }
        private PreparedRawMesh MeshInput { get; }
        private MeshletBuild MeshletBuild { get; }
        private IClusterVertexEncoder VertexEncoder { get; }
        private ulong VertexEncodingHash { get; }
        private List<ClusterInfo> ClusterInfos { get; } = [];
        private List<PackedCluster> CurrentClusters { get; } = [];
        private List<byte> CurrentVertexBlobs { get; } = [];
        private List<byte> CurrentIndices { get; } = [];
        private List<uint> CurrentMaterialData { get; } = [];
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
            EnsurePageSpace(ClusterPayloadSize(encoded, meshlet));
            PageStarts starts = CurrentPageStarts();
            uint materialOffset = uint.MaxValue;
            uint batchOffset = uint.MaxValue;
            if (meshlet.MaterialRanges.Length > 3)
            {
                materialOffset = checked((uint)CurrentMaterialData.Count * sizeof(uint));
                CurrentMaterialData.AddRange(meshlet.MaterialRanges);
                batchOffset = checked((uint)CurrentMaterialData.Count * sizeof(uint));
                CurrentMaterialData.AddRange(meshlet.VertReuseBatchWords);
            }
            AppendLocalPayload(encoded, meshlet);
            CurrentClusters.Add(CreatePackedCluster(meshlet, encoded, starts, materialOffset, batchOffset));
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
                new ReadOnlySpan<Vector3>(MeshInput.Positions, 0, MeshInput.VertexCount),
                MeshInput.Attributes,
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
            int rangeCount = meshlet.MaterialRanges.Length;

            int triangleStart = 0;
            int flatBatchIndex = 0;
            for (int rangeIndex = 0; rangeIndex < rangeCount; rangeIndex++)
            {
                int batchCount = checked((int)ReadVertReuseBits(meshlet, rangeIndex * 4, 4));
                if (batchCount <= 0)
                    throw new InvalidDataException("A material range has no vertex-reuse batch.");

                uint range = meshlet.MaterialRanges[rangeIndex];
                int rangeEnd = checked((int)((range & 0xFFu) + ((range >> 8) & 0xFFu)));
                for (int batchIndex = 0; batchIndex < batchCount; batchIndex++)
                {
                    int batchTriangleCount = checked((int)ReadVertReuseBits(
                        meshlet,
                        Math.Max(rangeCount, 3) * 4 + flatBatchIndex * 5,
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
            if (bitOffset < 0 || bitCount <= 0 || bitCount > 31 ||
                bitOffset + bitCount > meshlet.VertReuseBatchWords.Length * 32)
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
            => meshlet.VertReuseBatchWords[index];

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
        private int ClusterPayloadSize(EncodedCluster encoded, BuilderMeshlet meshlet)
        {
            int materialBytes = meshlet.MaterialRanges.Length > 3
                ? checked((meshlet.MaterialRanges.Length + meshlet.VertReuseBatchWords.Length) * sizeof(uint) + 3)
                : 0;
            return checked(PackedCluster.SizeInBytes + encoded.Blob.Length + LocalIndices.Count + materialBytes);
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

        private void AppendLocalPayload(EncodedCluster encoded, BuilderMeshlet meshlet)
        {
            int payloadSize = ClusterPayloadSize(encoded, meshlet);
            CurrentVertexBlobs.AddRange(encoded.Blob);
            CurrentIndices.AddRange(LocalIndices);
            CurrentVertexCount = checked(CurrentVertexCount + encoded.VertexCount);
            CurrentBytes += payloadSize;
        }

        private PackedCluster CreatePackedCluster(
            BuilderMeshlet meshlet,
            EncodedCluster encoded,
            PageStarts starts, uint materialOffset, uint batchOffset)
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
                MaximumEdgeLengthHalf = EncodeMaximumTriangleEdgeLength(),
                PackedCounts = PackedCounts(
                    encoded.VertexCount,
                    meshlet.Level),
                PackedMaterials = PackedMaterials(meshlet),
                PackedRanges = PackedRanges(meshlet) | (materialOffset != uint.MaxValue
                    ? checked((uint)meshlet.MaterialRanges.Length << 16) : 0u),
                MaterialTableOffset = materialOffset,
                VertReuseBatchWord0 = materialOffset == uint.MaxValue ? meshlet.VertReuseBatchWords[0] : batchOffset,
                VertReuseBatchWord1 = materialOffset == uint.MaxValue ? meshlet.VertReuseBatchWords[1] : checked((uint)meshlet.MaterialRanges.Length),
                VertReuseBatchWord2 = materialOffset == uint.MaxValue ? meshlet.VertReuseBatchWords[2] : checked((uint)meshlet.VertReuseBatchWords.Length),
                VertReuseBatchWord3 = materialOffset == uint.MaxValue ? meshlet.VertReuseBatchWords[3] : 0u,
                BoundMin = encoded.BoundMin,
                BoundMax = encoded.BoundMax,
            };
        }

        private ushort EncodeMaximumTriangleEdgeLength()
        {
            double maximumSquared = 0.0;
            for (int index = 0; index < LocalIndices.Count; index += 3)
            {
                Vector3 first = MeshInput.Positions[LocalVertices[LocalIndices[index]]];
                Vector3 second = MeshInput.Positions[LocalVertices[LocalIndices[index + 1]]];
                Vector3 third = MeshInput.Positions[LocalVertices[LocalIndices[index + 2]]];
                maximumSquared = Math.Max(maximumSquared, CalculateSquaredEdgeLength(first, second));
                maximumSquared = Math.Max(maximumSquared, CalculateSquaredEdgeLength(second, third));
                maximumSquared = Math.Max(maximumSquared, CalculateSquaredEdgeLength(third, first));
            }
            double maximum = Math.Sqrt(maximumSquared);
            Half encoded = (Half)maximum;
            if ((double)encoded < maximum) encoded = Half.BitIncrement(encoded);
            return BitConverter.HalfToUInt16Bits(encoded);
        }

        private static double CalculateSquaredEdgeLength(Vector3 first, Vector3 second)
        {
            double x = (double)first.X - second.X;
            double y = (double)first.Y - second.Y;
            double z = (double)first.Z - second.Z;
            return x * x + y * y + z * z;
        }
    }

    private sealed partial class MeshPageWriter
    {


        private uint PackedCounts(int vertexCount, int level)
            => (uint)vertexCount
                | ((uint)(LocalIndices.Count / 3) << 8)
                | ((uint)(byte)level << 16);

        private static uint PackedMaterials(BuilderMeshlet meshlet)
        {
            uint value = 0;
            for (int range = 0; range < Math.Min(meshlet.MaterialRanges.Length, 3); range++)
                value |= ((meshlet.MaterialRanges[range] >> 16) & 0xFFu) << (range * 8);
            return value;
        }

        private static uint PackedRanges(BuilderMeshlet meshlet)
        {
            uint first = meshlet.MaterialRanges[0];
            uint second = meshlet.MaterialRanges[Math.Min(1, meshlet.MaterialRanges.Length - 1)];
            uint firstEnd = (first & 0xFFu) + ((first >> 8) & 0xFFu);
            uint secondEnd = (second & 0xFFu) + ((second >> 8) & 0xFFu);
            return firstEnd | (secondEnd << 8);
        }

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
                GroupId = meshlet.Level == 0 ? -1 : meshlet.GroupId,
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
            if (CurrentMaterialData.Count != 0)
                extensionDataOffset = checked((extensionDataOffset + 3u) & ~3u);
            int materialSize = checked(CurrentMaterialData.Count * sizeof(uint));
            return new PageOffsets(
                clustersOffset,
                clustersSize,
                vertexBlobsOffset,
                vertexBlobsSize,
                indicesOffset,
                indicesSize,
                extensionDataOffset,
                materialSize,
                checked((int)extensionDataOffset + materialSize));
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
            for (int index = 0; index < CurrentClusters.Count; index++)
            {
                PackedCluster cluster = CurrentClusters[index];
                if (cluster.MaterialTableOffset == uint.MaxValue) continue;
                cluster.MaterialTableOffset += offsets.ExtensionDataOffset;
                cluster.VertReuseBatchWord0 += offsets.ExtensionDataOffset;
                CurrentClusters[index] = cluster;
            }
            MemoryMarshal.Cast<PackedCluster, byte>(CollectionsMarshal.AsSpan(CurrentClusters))
                .CopyTo(pageSpan.Slice((int)offsets.ClustersOffset, offsets.ClustersSize));
            CollectionsMarshal.AsSpan(CurrentVertexBlobs)
                .CopyTo(pageSpan.Slice((int)offsets.VertexBlobsOffset, offsets.VertexBlobsSize));
            CollectionsMarshal.AsSpan(CurrentIndices)
                .CopyTo(pageSpan.Slice((int)offsets.IndicesOffset, offsets.IndicesSize));
            MemoryMarshal.AsBytes(CollectionsMarshal.AsSpan(CurrentMaterialData))
                .CopyTo(pageSpan.Slice((int)offsets.ExtensionDataOffset, offsets.ExtensionDataSize));
        }

        private void ClearCurrentPage()
        {
            CurrentClusters.Clear();
            CurrentVertexBlobs.Clear();
            CurrentIndices.Clear();
            CurrentMaterialData.Clear();
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
