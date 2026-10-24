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
using ValueType = SomeEngine.Resources.Geometry.ValueType;
using Stream = System.IO.Stream;

namespace SomeEngine.Geometry.Cluster;

public static partial class ClusterMeshBuilder
{
    private static Mesh BuildCore(Vector3[] positions, VertexData attributes, uint[] indices,
        uint[] materialIndices, string name, ClusterBuildStrategy strategy, int? materialCount = null)
    {
        var scratch = new RawMeshScratch(positions.Length);
        try
        {
            MeshInput mesh = PrepareMesh(positions, attributes, indices, materialIndices, strategy, scratch);
            MeshletBuild meshlets = BuildMeshletData(mesh, strategy);
            var encoded = new List<EncodedClusterGeometry>(meshlets.Meshlets.Count);
            for (int index = 0; index < meshlets.Meshlets.Count; index++)
            {
                ClusterMeshlet cluster = meshlets.Meshlets[index];
                uint[] vertices = cluster.LocalVertices;
                byte[] local = cluster.LocalIndices;
                uint[] materials = new uint[local.Length / 3];
                double edgeSquared = 0;
                for (int triangle = 0; triangle < local.Length; triangle += 3)
                {
                    materials[triangle / 3] = mesh.MaterialIndices[vertices[local[triangle]]];
                    Vector3 first = mesh.Positions[vertices[local[triangle]]];
                    Vector3 second = mesh.Positions[vertices[local[triangle + 1]]];
                    Vector3 third = mesh.Positions[vertices[local[triangle + 2]]];
                    edgeSquared = Math.Max(edgeSquared, Math.Max(SquaredEdge(first, second),
                        Math.Max(SquaredEdge(second, third), SquaredEdge(third, first))));
                }
                EncodedClusterVertices data = mesh.Attributes.Encode(mesh.Positions.AsSpan(0, mesh.VertexCount), vertices.AsSpan());
                byte[] blob = new byte[16 + data.Blob.Length];
                int positionBytes = data.PositionBytes == 0 ? data.Blob.Length : data.PositionBytes;
                BinaryPrimitives.WriteUInt32LittleEndian(blob, 16);
                BinaryPrimitives.WriteUInt32LittleEndian(blob.AsSpan(4), (uint)positionBytes);
                BinaryPrimitives.WriteUInt32LittleEndian(blob.AsSpan(8), (uint)(data.PositionBytes == 0 ? 16 : 16 + positionBytes));
                BinaryPrimitives.WriteUInt32LittleEndian(blob.AsSpan(12), (uint)(data.PositionBytes == 0 ? data.Blob.Length : data.Blob.Length - positionBytes));
                data.Blob.Span.CopyTo(blob.AsSpan(16));
                encoded.Add(new()
                {
                    VertexBlob = blob, TriangleIndices = local, TriangleMaterials = materials, VertexCount = vertices.Length,
                    BoundMin = data.BoundMin, BoundMax = data.BoundMax, Center = cluster.Center,
                    LodSphere = new(cluster.LodCenter, cluster.LodRadius),
                    SelfLodSphere = new(cluster.SelfLodCenter, cluster.SelfLodRadius),
                    Level = cluster.Level, Error = cluster.Error, ParentError = cluster.ParentError,
                    Group = cluster.GroupId, ParentGroup = strategy == ClusterBuildStrategy.Realtime ? index : cluster.ParentGroupId,
                    SourceClusterIndex = (uint)index, MaximumEdgeLength = Math.Sqrt(edgeSquared),
                });
            }
            int regionCount = materialCount ?? checked((int)((ulong)materialIndices.Max() + 1));
            MeshRegion[] regions = Enumerable.Range(0, regionCount).Select(static index => new MeshRegion { Name = "Material " + index.ToString(System.Globalization.CultureInfo.InvariantCulture) }).ToArray();
            return ClusterMeshPages.Build(name, attributes.Compatibility, encoded, CreateBounds(meshlets.Bounds), regions);
        }
        finally { scratch.Return(); }
    }

    private static double SquaredEdge(Vector3 first, Vector3 second)
    {
        double x = (double)first.X - second.X, y = (double)first.Y - second.Y, z = (double)first.Z - second.Z;
        return x * x + y * y + z * z;
    }

    private static MeshInput PrepareMesh(Vector3[] positions, VertexData attributes,
        uint[] indices, uint[] materialIndices, ClusterBuildStrategy strategy, RawMeshScratch scratch)
    {
        if (strategy == ClusterBuildStrategy.Realtime)
            return new(positions, indices, positions.Length, indices.Length, attributes, materialIndices);

        nuint vertexCount = BuildRemap(scratch.Remap.AsSpan(0, positions.Length), indices, positions, attributes, materialIndices);
        scratch.Positions = ArrayPool<Vector3>.Shared.Rent((int)vertexCount);
        scratch.Indices = ArrayPool<uint>.Shared.Rent(indices.Length);
        RemapPositions(positions, scratch.Remap, vertexCount, scratch.Positions);
        RemapIndices(indices, scratch.Remap.AsSpan(0, positions.Length), scratch.Indices);
        VertexData remapped = attributes.Remap(scratch.Remap.AsSpan(0, positions.Length), (int)vertexCount);
        uint[] remappedMaterialIndices = RemapValues(materialIndices, scratch.Remap.AsSpan(0, positions.Length), (int)vertexCount);
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
            BuildLod(config, positions, indices, mesh.MaterialIndices, meshlets, globalIndices, vertexAttributes: mesh.Attributes.RawAttributes);
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
        if (strategy == ClusterBuildStrategy.Realtime) ConstrainMeshlets(meshlets, globalIndices, mesh.MaterialIndices);
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

    private readonly record struct MeshInput(
        Vector3[] Positions,
        uint[] Indices,
        int VertexCount,
        int IndexCount,
        VertexData Attributes,
        uint[] MaterialIndices);

    private readonly record struct MeshletBuild(
        List<ClusterMeshlet> Meshlets,
        List<uint> GlobalIndices,
        MeshBoundsInfo Bounds);

    private readonly record struct MeshBoundsInfo(
        Vector3 Min,
        Vector3 Max,
        Vector3 Extent,
        float MaxExtent);

}
