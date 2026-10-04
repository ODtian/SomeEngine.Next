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
            return CreateSplitMeshAsset(prepared, meshletBuild, materialSlots, name, vertexEncoder, scratch.Remap.AsSpan(0, rawPos.Length));
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

    private static MeshAsset CreateSplitMeshAsset(PreparedRawMesh mesh, MeshletBuild build,
        IReadOnlyList<MeshMaterialSlot> materials, string name, IClusterVertexEncoder encoder, ReadOnlySpan<uint> remap)
    {
        uint[] sourceVertices = Enumerable.Repeat(uint.MaxValue, mesh.VertexCount).ToArray();
        for (int vertex = 0; vertex < remap.Length; vertex++)
            if (remap[vertex] != uint.MaxValue) sourceVertices[remap[vertex]] = Math.Min(sourceVertices[remap[vertex]], (uint)vertex);
        using var geometry = new ClusterGeometryBlock
        {
            Vertices = Enumerable.Range(0, mesh.VertexCount).Select(vertex => new ClusterVertexSource(0, vertex, sourceVertices[vertex])).ToArray(),
        };
        using var attributes = new ClusterVertexAttributesBlock { VertexIdentity = geometry.VertexIdentity };
        foreach (BuilderMeshlet meshlet in build.Meshlets)
        {
            ReadOnlySpan<uint> triangles = CollectionsMarshal.AsSpan(build.GlobalIndices).Slice(meshlet.IndicesOffset, meshlet.IndicesCount);
            Dictionary<uint, byte> local = [];
            List<uint> vertices = [];
            byte[] indices = new byte[triangles.Length];
            for (int corner = 0; corner < triangles.Length; corner++)
            {
                if (!local.TryGetValue(triangles[corner], out byte vertex))
                {
                    vertex = (byte)vertices.Count; local.Add(triangles[corner], vertex); vertices.Add(triangles[corner]);
                }
                indices[corner] = vertex;
            }
            Vector3 min = new(float.MaxValue), max = new(float.MinValue);
            foreach (uint vertex in vertices) { min = Vector3.Min(min, mesh.Positions[vertex]); max = Vector3.Max(max, mesh.Positions[vertex]); }
            Vector3 extent = max - min;
            float maximum = MathF.Max(extent.X, MathF.Max(extent.Y, extent.Z));
            float step = maximum == 0 ? 1 : MathF.Max(MathF.Pow(2, MathF.Ceiling(MathF.Log2(maximum / 65535))), 1e-12f);
            byte[] positions = new byte[vertices.Count * 6];
            for (int vertex = 0; vertex < vertices.Count; vertex++)
            {
                Vector3 value = (mesh.Positions[vertices[vertex]] - min) / step;
                System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(positions.AsSpan(vertex * 6), (ushort)MathF.Round(value.X));
                System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(positions.AsSpan(vertex * 6 + 2), (ushort)MathF.Round(value.Y));
                System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(positions.AsSpan(vertex * 6 + 4), (ushort)MathF.Round(value.Z));
            }
            int[] triangleMaterials = Enumerable.Range(0, indices.Length / 3).Select(triangle =>
                triangle < meshlet.Range0End ? (int)meshlet.Mat0 : triangle < meshlet.Range1End ? meshlet.Mat1 : meshlet.Mat2).ToArray();
            geometry.Clusters.Add(new()
            {
                Vertices = [.. vertices], Indices = [.. indices], TriangleMaterials = [.. triangleMaterials],
                PositionOrigin = CreateVec3(min), PositionStep = step, PositionData = [.. positions],
                Level = meshlet.Level, Error = meshlet.Error, ParentError = meshlet.ParentError,
                Group = meshlet.GroupId, ParentGroup = meshlet.ParentGroupId,
                Center = CreateVec3(meshlet.Center), Radius = meshlet.Radius + step,
                LodCenter = CreateVec3(meshlet.LodCenter), LodRadius = meshlet.LodRadius + step,
                SelfLodCenter = CreateVec3(meshlet.SelfLodCenter), SelfLodRadius = meshlet.SelfLodRadius + step,
            });
            if (encoder is not StandardClusterVertexEncoder)
            {
                EncodedClusterVertices encoded = encoder.Encode(mesh.Positions.AsSpan(0, mesh.VertexCount), mesh.Attributes, CollectionsMarshal.AsSpan(vertices));
                attributes.Encoding = encoder.Compatibility;
                attributes.Clusters.Add(new() { Data = [.. encoded.Blob.Span], Attributes = [new() { Name = "Encoded", ComponentCount = 1, Data = [.. encoded.Blob.Span] }] });
                continue;
            }
            List<ClusterVertexAttribute> streams = [];
            foreach (RawAttribute attribute in mesh.Attributes)
            {
                if (attribute.Name == "POSITION" || attribute.Name.StartsWith("JOINTS_", StringComparison.Ordinal) || attribute.Name.StartsWith("WEIGHTS_", StringComparison.Ordinal)) continue;
                byte[] bytes = new byte[vertices.Count * attribute.Dimension * sizeof(double)];
                for (int vertex = 0; vertex < vertices.Count; vertex++)
                    for (int component = 0; component < attribute.Dimension; component++)
                        System.Buffers.Binary.BinaryPrimitives.WriteDoubleLittleEndian(bytes.AsSpan((vertex * attribute.Dimension + component) * sizeof(double)),
                            attribute.Data[(int)vertices[vertex] * attribute.Dimension + component]);
                streams.Add(new() { Name = attribute.Name, ComponentCount = attribute.Dimension,
                    VertexComponentCounts = [.. Enumerable.Repeat(attribute.Dimension, vertices.Count)], Data = [.. bytes] });
            }
            attributes.Clusters.Add(ClusterVertexAttributes.Encode(streams));
        }
        using var shape = new MeshBlock { Bounds = CreateBounds(build.Bounds), Regions = CreateMeshRegions(materials.Count) };
        using var references = new MeshMaterialsBlock { Materials = materials.Select(material => new AssetId<MaterialAsset>(material.MaterialGuid)).ToArray() };
        var result = new MeshAsset { Name = name };
        try { result.SetBlock(shape); result.SetBlock(geometry); result.SetBlock(attributes); result.SetBlock(references); return result; }
        catch { result.Dispose(); throw; }
    }

    private readonly record struct PreparedRawMesh(Vector3[] Positions, uint[] Indices, int VertexCount, int IndexCount, List<RawAttribute> Attributes);
    private readonly record struct MeshletBuild(List<BuilderMeshlet> Meshlets, List<uint> GlobalIndices, MeshBoundsInfo Bounds);
    private readonly record struct MeshBoundsInfo(Vector3 Min, Vector3 Max, Vector3 Extent, float MaxExtent);
}
