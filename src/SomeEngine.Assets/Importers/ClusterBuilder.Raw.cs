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
using ValueType = SomeEngine.Assets.Data.ValueType;

namespace SomeEngine.Assets.Importers;

public static partial class ClusterBuilder
{
    private static MeshAsset ProcessRawCore(
        Vector3[] rawPos,
        List<RawAttribute> rawAttributes,
        uint[] rawIndices,
        List<string> regionNames,
        string name)
    {
        using FileStream stream = CreatePageStream();
        var scratch = new RawMeshScratch(rawPos.Length);
        try
        {
            PreparedRawMesh prepared = PrepareRawMesh(rawPos, rawAttributes, rawIndices, scratch);
            MeshletBuild meshletBuild = BuildMeshletData(prepared);
            SortMeshlets(meshletBuild.Meshlets, meshletBuild.Quantization);
            List<VertexAttributeDescriptor> descriptors = CreateVertexDescriptors(prepared.Attributes);
            PageBuildOutput pageOutput = WriteMeshPages(stream, scratch.PageBuffer, prepared, meshletBuild, descriptors);
            long bvhOffset = WriteBvh(stream, pageOutput.ClusterInfos);
            return CreateMeshAsset(name, regionNames, stream, meshletBuild.Quantization, descriptors, bvhOffset);
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

    private static MeshletBuild BuildMeshletData(PreparedRawMesh prepared)
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
        QuantizationInfo quantization = ComputeQuantization(prepared.Positions, prepared.VertexCount);
        return new MeshletBuild(meshlets, globalIndices, quantization);
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

    private static QuantizationInfo ComputeQuantization(Vector3[] positions, int vertexCount)
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
        float quantStep = MathF.Pow(2, MathF.Ceiling(MathF.Log2(maxExtent / 65535f)));
        return new QuantizationInfo(
            sceneMin,
            sceneMax,
            extent,
            maxExtent,
            Math.Max(quantStep, 1e-12f),
            sceneMin);
    }

    private static void SortMeshlets(
        List<BuilderMeshlet> meshlets,
        QuantizationInfo quantization)
    {
        meshlets.Sort((left, right) => CompareMeshlets(left, right, quantization));
    }

    private static int CompareMeshlets(
        BuilderMeshlet left,
        BuilderMeshlet right,
        QuantizationInfo quantization)
    {
        if (left.ParentGroupId != right.ParentGroupId)
        {
            return left.ParentGroupId.CompareTo(right.ParentGroupId);
        }

        uint leftCode = Morton3D((left.LodCenter - quantization.Min) / quantization.Extent);
        uint rightCode = Morton3D((right.LodCenter - quantization.Min) / quantization.Extent);
        return leftCode.CompareTo(rightCode);
    }

    private static List<VertexAttributeDescriptor> CreateVertexDescriptors(List<RawAttribute> attributes)
    {
        var descriptors = new List<VertexAttributeDescriptor>(attributes.Count);
        for (int index = 0; index < attributes.Count; index++)
        {
            RawAttribute attribute = attributes[index];
            descriptors.Add(
                new VertexAttributeDescriptor
                {
                    Name = attribute.Name,
                    Type = attribute.TargetType,
                    NumComponents = attribute.NumComponents,
                    IsNormalized = attribute.Normalized,
                    StreamIndex = (ushort)index,
                });
        }

        return descriptors;
    }

    private static PageBuildOutput WriteMeshPages(
        FileStream stream,
        byte[] pageBuffer,
        PreparedRawMesh prepared,
        MeshletBuild meshletBuild,
        List<VertexAttributeDescriptor> descriptors)
    {
        var writer = new MeshPageWriter(stream, pageBuffer, prepared, meshletBuild, descriptors);
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

    private static MeshAsset CreateMeshAsset(
        string name,
        List<string> regionNames,
        FileStream stream,
        QuantizationInfo quantization,
        List<VertexAttributeDescriptor> descriptors,
        long bvhOffset)
    {
        var meshAsset = new MeshAsset
        {
            Name = name,
            Bounds = CreateBounds(quantization),
            Payload = new byte[stream.Length],
            Attributes = CreateSchemaAttributes(descriptors),
            BvhOffset = (ulong)bvhOffset,
            QuantOrigin = CreateVec3(quantization.Origin),
            QuantStep = quantization.Step,
            Regions = CreateMeshRegions(regionNames),
        };
        stream.Seek(0, SeekOrigin.Begin);
        meshAsset.Payload!.Value.Span.Clear();
        stream.ReadExactly(meshAsset.Payload.Value.Span);
        return meshAsset;
    }

    private static SomeEngine.Assets.Schema.Bounds CreateBounds(QuantizationInfo quantization)
    {
        Vector3 center = (quantization.Min + quantization.Max) * 0.5f;
        return new SomeEngine.Assets.Schema.Bounds
        {
            Center = CreateVec3(center),
            Radius = quantization.MaxExtent * 0.5f,
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

    private static VertexAttribute[] CreateSchemaAttributes(
        List<VertexAttributeDescriptor> descriptors)
    {
        VertexAttribute[] schemaAttributes = new SomeEngine.Assets.Schema.VertexAttribute[descriptors.Count];
        for (int index = 0; index < descriptors.Count; index++)
        {
            VertexAttributeDescriptor descriptor = descriptors[index];
            schemaAttributes[index] = new SomeEngine.Assets.Schema.VertexAttribute
            {
                Name = descriptor.Name,
                Type = (SomeEngine.Assets.Schema.ValueType)descriptor.Type,
                Components = descriptor.NumComponents,
                Normalized = descriptor.IsNormalized,
                Offset = descriptor.StreamIndex,
            };
        }

        return schemaAttributes;
    }

    private static MeshRegion[] CreateMeshRegions(List<string> regionNames)
        => regionNames.Select(static regionName => new MeshRegion { Name = regionName }).ToArray();

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
            List<VertexAttributeDescriptor> descriptors)
        {
            Stream = stream;
            PageBuffer = pageBuffer;
            Mesh = mesh;
            MeshletBuild = meshletBuild;
            Descriptors = descriptors;
            CurrentStreams = CreateCurrentStreams(descriptors);
            LocalStreamBytes = CreateLocalStreams(descriptors);
        }

        private FileStream Stream { get; }
        private byte[] PageBuffer { get; }
        private PreparedRawMesh Mesh { get; }
        private MeshletBuild MeshletBuild { get; }
        private List<VertexAttributeDescriptor> Descriptors { get; }
        private List<MeshPageInfo> Pages { get; } = [];
        private List<ClusterInfo> ClusterInfos { get; } = [];
        private List<GPUCluster> CurrentClusters { get; } = [];
        private List<ushort> CurrentPositions { get; } = [];
        private List<byte>[] CurrentStreams { get; }
        private List<byte> CurrentIndices { get; } = [];
        private Dictionary<uint, ushort> UsedMap { get; } = new(MaxVerticesPerMeshlet);
        private List<ushort> LocalPositions { get; } = new(MaxVerticesPerMeshlet * 3);
        private List<byte> LocalIndices { get; } = new(MaxTrianglesPerMeshlet * 3);
        private List<byte>[] LocalStreamBytes { get; }
        private int CurrentBytes { get; set; } = PageHeaderSize;

        public PageBuildOutput Write()
        {
            Span<uint> globalIndices = CollectionsMarshal.AsSpan(MeshletBuild.GlobalIndices);
            foreach (BuilderMeshlet meshlet in MeshletBuild.Meshlets)
            {
                AddCluster(meshlet, globalIndices);
            }

            FlushPage();
            return new PageBuildOutput(Pages, ClusterInfos);
        }

        private static List<byte>[] CreateCurrentStreams(
            List<VertexAttributeDescriptor> descriptors)
        {
            List<byte>[] streams = new List<byte>[descriptors.Count];
            for (int index = 0; index < descriptors.Count; index++)
            {
                streams[index] = [];
            }

            return streams;
        }

        private static List<byte>[] CreateLocalStreams(
            List<VertexAttributeDescriptor> descriptors)
        {
            List<byte>[] streams = new List<byte>[descriptors.Count];
            for (int index = 0; index < descriptors.Count; index++)
            {
                streams[index] = new List<byte>(MaxVerticesPerMeshlet * descriptors[index].GetSize());
            }

            return streams;
        }
    }

    private sealed partial class MeshPageWriter
    {


        private void AddCluster(BuilderMeshlet meshlet, Span<uint> globalIndices)
        {
            ResetLocalBuffers();
            ReadOnlySpan<uint> meshletIndices = globalIndices.Slice(meshlet.IndicesOffset, meshlet.IndicesCount);
            QuantizedBounds intBounds = ComputeIntBounds(meshletIndices);
            EncodedCluster encoded = EncodeLocalCluster(meshletIndices, intBounds);
            ValidateVrbBatches(meshlet, encoded.VertexCount);
            EnsurePageSpace(ClusterPayloadSize());
            PageStarts starts = CurrentPageStarts();
            AppendLocalPayload();
            CurrentClusters.Add(CreateGpuCluster(meshlet, encoded, starts));
            ClusterInfos.Add(CreateClusterInfo(meshlet, encoded));
        }

        private void ResetLocalBuffers()
        {
            UsedMap.Clear();
            LocalPositions.Clear();
            LocalIndices.Clear();
            for (int index = 0; index < LocalStreamBytes.Length; index++)
            {
                LocalStreamBytes[index].Clear();
            }
        }

        private QuantizedBounds ComputeIntBounds(ReadOnlySpan<uint> meshletIndices)
        {
            int minX = int.MaxValue;
            int minY = int.MaxValue;
            int minZ = int.MaxValue;
            foreach (uint globalIndex in meshletIndices)
            {
                QuantizedPoint point = QuantizePoint(Mesh.Positions[(int)globalIndex]);
                minX = Math.Min(minX, point.X);
                minY = Math.Min(minY, point.Y);
                minZ = Math.Min(minZ, point.Z);
            }

            return new QuantizedBounds(minX, minY, minZ);
        }

        private EncodedCluster EncodeLocalCluster(
            ReadOnlySpan<uint> meshletIndices,
            QuantizedBounds bounds)
        {
            int vertexCount = 0;
            Vector3 decodedMin = new(float.MaxValue);
            Vector3 decodedMax = new(float.MinValue);
            foreach (uint globalIndex in meshletIndices)
            {
                if (!UsedMap.TryGetValue(globalIndex, out ushort localIndex))
                {
                    localIndex = (ushort)vertexCount++;
                    UsedMap[globalIndex] = localIndex;
                    AddLocalVertex(globalIndex, bounds, ref decodedMin, ref decodedMax);
                }

                LocalIndices.Add((byte)localIndex);
            }

            return new EncodedCluster(vertexCount, bounds.X, bounds.Y, bounds.Z, decodedMin, decodedMax);
        }

        private void AddLocalVertex(
            uint globalIndex,
            QuantizedBounds bounds,
            ref Vector3 decodedMin,
            ref Vector3 decodedMax)
        {
            QuantizedPoint point = QuantizePoint(Mesh.Positions[(int)globalIndex]);
            AddLocalPosition(point, bounds);
            Vector3 decoded = Decode(point);
            decodedMin = Vector3.Min(decodedMin, decoded);
            decodedMax = Vector3.Max(decodedMax, decoded);
            for (int index = 0; index < Mesh.Attributes.Count; index++)
            {
                PackAttribute(LocalStreamBytes[index], Mesh.Attributes[index], (int)globalIndex);
            }
        }

        private void AddLocalPosition(QuantizedPoint point, QuantizedBounds bounds)
        {
            int localX = point.X - bounds.X;
            int localY = point.Y - bounds.Y;
            int localZ = point.Z - bounds.Z;
            if ((uint)localX > ushort.MaxValue || (uint)localY > ushort.MaxValue || (uint)localZ > ushort.MaxValue)
            {
                throw new InvalidOperationException(
                    $"Cluster local quantized position exceeds encoded range: ({localX}, {localY}, {localZ}).");
            }

            LocalPositions.Add((ushort)localX);
            LocalPositions.Add((ushort)localY);
            LocalPositions.Add((ushort)localZ);
        }

        private QuantizedPoint QuantizePoint(Vector3 point)
        {
            QuantizationInfo quantization = MeshletBuild.Quantization;
            return new QuantizedPoint(
                (int)MathF.Round((point.X - quantization.Origin.X) / quantization.Step),
                (int)MathF.Round((point.Y - quantization.Origin.Y) / quantization.Step),
                (int)MathF.Round((point.Z - quantization.Origin.Z) / quantization.Step));
        }

        private Vector3 Decode(QuantizedPoint point)
        {
            QuantizationInfo quantization = MeshletBuild.Quantization;
            return new Vector3(
                point.X * quantization.Step + quantization.Origin.X,
                point.Y * quantization.Step + quantization.Origin.Y,
                point.Z * quantization.Step + quantization.Origin.Z);
        }
    }

    private sealed partial class MeshPageWriter
    {


        private void ValidateVrbBatches(BuilderMeshlet meshlet, int vertexCount)
        {
            int triangleCount = LocalIndices.Count / 3;
            Span<byte> indexSpan = CollectionsMarshal.AsSpan(LocalIndices);
            int encodedBatchCount = (int)((meshlet.VRBBatchInfo >> 25) & 0x7) + 1;
            int batchStart = 0;
            for (int batchIndex = 0; batchIndex < encodedBatchCount; batchIndex++)
            {
                int batchEnd = Math.Min(batchStart + BatchTriangleCount(meshlet.VRBBatchInfo, batchIndex), triangleCount);
                ValidateVrbBatch(indexSpan, batchStart, batchEnd, batchIndex, triangleCount, vertexCount);
                batchStart = batchEnd;
            }
        }

        private static int BatchTriangleCount(uint vrb, int batchIndex)
            => (int)((vrb >> (batchIndex * 5)) & 0x1F) + 1;

        private static void ValidateVrbBatch(
            Span<byte> indices,
            int batchStart,
            int batchEnd,
            int batchIndex,
            int triangleCount,
            int vertexCount)
        {
            uint baseVertex = VrbBaseVertex(indices, batchStart, batchEnd);
            ulong usedMask = VrbUsedMask(indices, batchStart, batchEnd, baseVertex, batchIndex, triangleCount, vertexCount);
            int uniqueVertexCount = BitOperations.PopCount(usedMask);
            if (uniqueVertexCount > 32)
            {
                throw new Exception(
                    $"[VRB] UNIQUE VERT OVERFLOW: batch {batchIndex} [{batchStart}..{batchEnd}) " +
                    $"has {uniqueVertexCount} unique local verts (max 32). cluster tris={triangleCount} verts={vertexCount}.");
            }
        }

        private static uint VrbBaseVertex(Span<byte> indices, int batchStart, int batchEnd)
        {
            uint baseVertex = 255;
            for (int triangle = batchStart; triangle < batchEnd; triangle++)
            {
                baseVertex = Math.Min(baseVertex, indices[triangle * 3 + 0]);
                baseVertex = Math.Min(baseVertex, indices[triangle * 3 + 1]);
                baseVertex = Math.Min(baseVertex, indices[triangle * 3 + 2]);
            }

            return baseVertex;
        }

        private static ulong VrbUsedMask(
            Span<byte> indices,
            int batchStart,
            int batchEnd,
            uint baseVertex,
            int batchIndex,
            int triangleCount,
            int vertexCount)
        {
            ulong usedMask = 0;
            for (int triangle = batchStart; triangle < batchEnd; triangle++)
            {
                AddVrbTriangle(indices, triangle, baseVertex, batchIndex, triangleCount, vertexCount, ref usedMask);
            }

            return usedMask;
        }
    }

    private sealed partial class MeshPageWriter
    {


        private static void AddVrbTriangle(
            Span<byte> indices,
            int triangle,
            uint baseVertex,
            int batchIndex,
            int triangleCount,
            int vertexCount,
            ref ulong usedMask)
        {
            for (int component = 0; component < 3; component++)
            {
                uint rebased = (uint)indices[triangle * 3 + component] - baseVertex;
                if (rebased >= 64)
                {
                    throw new Exception(
                        $"[VRB] SPAN: rebased={rebased} (>=64) batch {batchIndex}, cluster tris={triangleCount} verts={vertexCount}");
                }

                usedMask |= 1UL << (int)rebased;
            }
        }

        private int ClusterPayloadSize()
        {
            int attributeSize = 0;
            for (int index = 0; index < LocalStreamBytes.Length; index++)
            {
                attributeSize += LocalStreamBytes[index].Count;
            }

            return Unsafe.SizeOf<GPUCluster>() + LocalPositions.Count * 2 + attributeSize + LocalIndices.Count;
        }

        private void EnsurePageSpace(int bytesToAdd)
        {
            if (CurrentBytes + bytesToAdd > PageSize || CurrentIndices.Count > MaxEncodedTriangleStart)
            {
                FlushPage();
            }
        }

        private PageStarts CurrentPageStarts()
        {
            uint vertexStart = (uint)(CurrentPositions.Count / 3);
            uint triangleStart = (uint)CurrentIndices.Count;
            ValidatePageStarts(vertexStart, triangleStart);
            return new PageStarts(vertexStart, triangleStart);
        }

        private void ValidatePageStarts(uint vertexStart, uint triangleStart)
        {
            if (vertexStart > ushort.MaxValue)
            {
                throw new InvalidOperationException(
                    $"Cluster page vertex start exceeds encoded range: {vertexStart} > {ushort.MaxValue}");
            }

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

        private void AppendLocalPayload()
        {
            int payloadSize = ClusterPayloadSize();
            CurrentPositions.AddRange(LocalPositions);
            for (int index = 0; index < CurrentStreams.Length; index++)
            {
                CurrentStreams[index].AddRange(LocalStreamBytes[index]);
            }

            CurrentIndices.AddRange(LocalIndices);
            CurrentBytes += payloadSize;
        }

        private GPUCluster CreateGpuCluster(
            BuilderMeshlet meshlet,
            EncodedCluster encoded,
            PageStarts starts)
        {
            ClusterCenterEncoding center = EncodeClusterCenter(meshlet, encoded);
            return new GPUCluster
            {
                IntBaseX = encoded.BaseX,
                IntBaseY = encoded.BaseY,
                IntBaseZ = encoded.BaseZ,
                PackedCenterXY = GPUCluster.PackU16(center.X, center.Y),
                LODCenter = meshlet.SelfLodCenter,
                LODRadius = meshlet.SelfLodRadius,
                PackedCenterZRadius = GPUCluster.PackU16(center.Z, center.Radius),
                LODErrorHalf = BitConverter.HalfToUInt16Bits((Half)meshlet.Error),
                VertexStart = (ushort)starts.Vertex,
                TriangleStart = (ushort)starts.Triangle,
                GroupId = (short)meshlet.GroupId,
                PackedCounts = PackedCounts(encoded.VertexCount, meshlet.Level),
                PackedMaterials = PackedMaterials(meshlet),
                PackedRanges = PackedRanges(meshlet),
                MaterialTableOffset = 0xFFFFFFFF,
                VRBBatchInfo = meshlet.VRBBatchInfo,
                BoundMin = encoded.DecodedMin,
                BoundMax = encoded.DecodedMax,
            };
        }
    }

    private sealed partial class MeshPageWriter
    {


        private ClusterCenterEncoding EncodeClusterCenter(
            BuilderMeshlet meshlet,
            EncodedCluster encoded)
        {
            float radius = meshlet.Radius < 1e-6f ? MeshletBuild.Quantization.Step : meshlet.Radius;
            QuantizedPoint center = QuantizePoint(meshlet.Center);
            float centerQuantError = MeshletBuild.Quantization.Step * MathF.Sqrt(3.0f) * 0.5f;
            return new ClusterCenterEncoding(
                (ushort)Math.Clamp(center.X - encoded.BaseX, 0, 65535),
                (ushort)Math.Clamp(center.Y - encoded.BaseY, 0, 65535),
                (ushort)Math.Clamp(center.Z - encoded.BaseZ, 0, 65535),
                (ushort)Math.Clamp(
                    (int)MathF.Ceiling((radius + centerQuantError) / MeshletBuild.Quantization.Step),
                    1,
                    65535));
        }

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
                BoundMin = encoded.DecodedMin,
                BoundMax = encoded.DecodedMax,
                LODSphere = new Vector4(meshlet.LodCenter.X, meshlet.LodCenter.Y, meshlet.LodCenter.Z, meshlet.LodRadius),
                LODError = (float)(Half)meshlet.ParentError,
                PageIndex = (uint)Pages.Count,
                ClusterStart = (uint)(CurrentClusters.Count - 1),
                ParentGroupId = meshlet.ParentGroupId,
            };
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
            Pages.Add(CreatePageInfo(offsets));
            ClearCurrentPage();
        }

        private PageOffsets ComputePageOffsets()
        {
            uint clustersOffset = PageHeaderSize;
            int clustersSize = CurrentClusters.Count * Unsafe.SizeOf<GPUCluster>();
            uint positionsOffset = clustersOffset + (uint)clustersSize;
            int positionsSize = CurrentPositions.Count * sizeof(ushort);
            uint attributesOffset = positionsOffset + (uint)positionsSize;
            int attributesSize = CurrentAttributeSize();
            uint indicesOffset = attributesOffset + (uint)attributesSize;
            int indicesSize = CurrentIndices.Count;
            return new PageOffsets(
                clustersOffset,
                clustersSize,
                positionsOffset,
                positionsSize,
                attributesOffset,
                attributesSize,
                indicesOffset,
                indicesSize,
                (int)indicesOffset + indicesSize);
        }

        private int CurrentAttributeSize()
        {
            int size = 0;
            for (int index = 0; index < CurrentStreams.Length; index++)
            {
                size += CurrentStreams[index].Count;
            }

            return size;
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
            header.TotalVertexCount = (uint)(CurrentPositions.Count / 3);
            header.TotalTriangleCount = (uint)(CurrentIndices.Count / 3);
            header.QuantOriginX = MeshletBuild.Quantization.Origin.X;
            header.ClustersOffset = offsets.ClustersOffset;
            header.PositionsOffset = offsets.PositionsOffset;
            header.AttributesOffset = offsets.AttributesOffset;
            header.IndicesOffset = offsets.IndicesOffset;
            header.QuantOriginY = MeshletBuild.Quantization.Origin.Y;
            header.QuantOriginZ = MeshletBuild.Quantization.Origin.Z;
            header.QuantStep = MeshletBuild.Quantization.Step;
        }
    }

    private sealed partial class MeshPageWriter
    {


        private void WritePagePayload(Span<byte> pageSpan, PageOffsets offsets)
        {
            MemoryMarshal.Cast<GPUCluster, byte>(CollectionsMarshal.AsSpan(CurrentClusters))
                .CopyTo(pageSpan.Slice((int)offsets.ClustersOffset, offsets.ClustersSize));
            MemoryMarshal.Cast<ushort, byte>(CollectionsMarshal.AsSpan(CurrentPositions))
                .CopyTo(pageSpan.Slice((int)offsets.PositionsOffset, offsets.PositionsSize));
            WriteAttributeStreams(pageSpan, offsets.AttributesOffset);
            CollectionsMarshal.AsSpan(CurrentIndices)
                .CopyTo(pageSpan.Slice((int)offsets.IndicesOffset, offsets.IndicesSize));
        }

        private void WriteAttributeStreams(Span<byte> pageSpan, uint attributesOffset)
        {
            int writeOffset = (int)attributesOffset;
            for (int index = 0; index < CurrentStreams.Length; index++)
            {
                Span<byte> streamSpan = CollectionsMarshal.AsSpan(CurrentStreams[index]);
                streamSpan.CopyTo(pageSpan.Slice(writeOffset, streamSpan.Length));
                writeOffset += streamSpan.Length;
            }
        }

        private MeshPageInfo CreatePageInfo(PageOffsets offsets)
        {
            return new MeshPageInfo
            {
                ClusterCount = (uint)CurrentClusters.Count,
                TotalVertexCount = (uint)(CurrentPositions.Count / 3),
                TotalTriangleCount = (uint)(CurrentIndices.Count / 3),
                ClustersOffset = offsets.ClustersOffset,
                PositionsOffset = offsets.PositionsOffset,
                AttributesOffset = offsets.AttributesOffset,
                IndicesOffset = offsets.IndicesOffset,
                FileOffset = Stream.Position - offsets.TotalSize,
            };
        }

        private void ClearCurrentPage()
        {
            CurrentClusters.Clear();
            CurrentPositions.Clear();
            for (int index = 0; index < CurrentStreams.Length; index++)
            {
                CurrentStreams[index].Clear();
            }

            CurrentIndices.Clear();
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
        QuantizationInfo Quantization);

    private readonly record struct QuantizationInfo(
        Vector3 Min,
        Vector3 Max,
        Vector3 Extent,
        float MaxExtent,
        float Step,
        Vector3 Origin);

    private readonly record struct PageBuildOutput(
        List<MeshPageInfo> Pages,
        List<ClusterInfo> ClusterInfos);

    private readonly record struct QuantizedPoint(int X, int Y, int Z);

    private readonly record struct QuantizedBounds(int X, int Y, int Z);

    private readonly record struct EncodedCluster(
        int VertexCount,
        int BaseX,
        int BaseY,
        int BaseZ,
        Vector3 DecodedMin,
        Vector3 DecodedMax);

    private readonly record struct ClusterCenterEncoding(
        ushort X,
        ushort Y,
        ushort Z,
        ushort Radius);

    private readonly record struct PageStarts(uint Vertex, uint Triangle);

    private readonly record struct PageOffsets(
        uint ClustersOffset,
        int ClustersSize,
        uint PositionsOffset,
        int PositionsSize,
        uint AttributesOffset,
        int AttributesSize,
        uint IndicesOffset,
        int IndicesSize,
        int TotalSize);
}
