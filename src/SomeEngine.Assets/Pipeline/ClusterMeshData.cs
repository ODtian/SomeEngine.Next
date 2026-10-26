using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.InteropServices;
using SomeEngine.Assets.Schema;
using SomeEngine.Resources;
using SomeEngine.Resources.Geometry;

namespace SomeEngine.Assets.Pipeline;

// Builds the renderer's topology and byte views once per pair of Block versions.
// Attribute bytes remain opaque here; their importer and material shader define their format.
internal sealed class ClusterMeshData
{
    internal Mesh Mesh { get; }
    private readonly ClusterGeometry[] _clusters;
    private readonly ClusterVertexAttributes[] _attributes;
    private readonly Guid _vertexIdentity;
    private ClusterMeshData(Mesh mesh, ClusterGeometryBlock geometry, ClusterVertexAttributesBlock attributes)
    {
        Mesh = mesh;
        _clusters = geometry.Clusters.ToArray();
        _attributes = attributes.Clusters.ToArray();
        _vertexIdentity = geometry.VertexIdentity;
    }
    internal bool Matches(ClusterGeometryBlock geometry, ClusterVertexAttributesBlock attributes)
        => _vertexIdentity == geometry.VertexIdentity && _vertexIdentity == attributes.VertexIdentity
            && Mesh.VertexEncoding == geometry.PositionEncoding + "+" + attributes.Encoding
            && geometry.Clusters.SequenceEqual(_clusters) && attributes.Clusters.SequenceEqual(_attributes);
    internal static ClusterMeshData Build(ClusterGeometryBlock geometry, ClusterVertexAttributesBlock attributes)
    {
        if (geometry.VertexIdentity != attributes.VertexIdentity || geometry.Clusters.Count != attributes.Clusters.Count)
            throw new InvalidDataException("Cluster geometry and attributes must describe the same vertices.");
        var encoded = new List<SomeEngine.Geometry.Cluster.EncodedClusterGeometry>(geometry.Clusters.Count);
        Vector3 minimum = new(float.MaxValue), maximum = new(float.MinValue);
        for (int index = 0; index < geometry.Clusters.Count; index++)
        {
            ClusterGeometry source = geometry.Clusters[index];
            ReadOnlySpan<byte> positions = source.PositionData.AsSpan();
            ReadOnlySpan<byte> values = attributes.Clusters[index].Data.AsSpan();
            int attributeOffset = 16 + ((positions.Length + 3) & ~3);
            byte[] blob = new byte[attributeOffset + values.Length];
            BinaryPrimitives.WriteUInt32LittleEndian(blob, 16);
            BinaryPrimitives.WriteUInt32LittleEndian(blob.AsSpan(4), (uint)positions.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(blob.AsSpan(8), (uint)attributeOffset);
            BinaryPrimitives.WriteUInt32LittleEndian(blob.AsSpan(12), (uint)values.Length);
            positions.CopyTo(blob.AsSpan(16));
            values.CopyTo(blob.AsSpan(attributeOffset));
            var materials = source.TriangleRegions.IsEmpty ? source.TriangleMaterials : source.TriangleRegions;
            uint[] regions = materials.Select(static value => unchecked((uint)value)).ToArray();
            Vector3 low = Vector(source.BoundMin), high = Vector(source.BoundMax);
            minimum = Vector3.Min(minimum, low); maximum = Vector3.Max(maximum, high);
            double edgeSquared = 0;
            for (int triangle = 0; triangle < source.Indices.Length; triangle += 3)
            {
                Vector3 first = source.DecodePosition(source.Indices[triangle]);
                Vector3 second = source.DecodePosition(source.Indices[triangle + 1]);
                Vector3 third = source.DecodePosition(source.Indices[triangle + 2]);
                edgeSquared = Math.Max(edgeSquared, Math.Max(SquaredEdge(first, second),
                    Math.Max(SquaredEdge(second, third), SquaredEdge(third, first))));
            }
            encoded.Add(new()
            {
                VertexBlob = blob, TriangleIndices = source.Indices.ToArray(), TriangleMaterials = regions,
                VertexCount = source.Vertices.Length, BoundMin = low, BoundMax = high,
                Center = Vector(source.Center),
                LodSphere = new(Vector(source.LodCenter), source.LodRadius),
                SelfLodSphere = new(Vector(source.SelfLodCenter), source.SelfLodRadius),
                Error = source.Error, ParentError = source.ParentError, Level = source.Level,
                Group = source.Group, ParentGroup = source.ParentGroup, SourceClusterIndex = (uint)index,
                MaximumEdgeLength = Math.Sqrt(edgeSquared),
            });
        }
        Vector3 center = (minimum + maximum) * .5f;
        var bounds = new Bounds { Center = new() { X = center.X, Y = center.Y, Z = center.Z },
            Radius = Vector3.Distance(minimum, maximum) * .5f };
        Mesh mesh = SomeEngine.Geometry.Cluster.ClusterMeshPages.Build("Cluster geometry",
            geometry.PositionEncoding + "+" + attributes.Encoding, encoded, bounds);
        return new(mesh, geometry, attributes);
    }

    private static Vector3 Vector(Vec3 value) => new(value.X, value.Y, value.Z);
    private static double SquaredEdge(Vector3 first, Vector3 second)
    {
        double x = (double)first.X - second.X, y = (double)first.Y - second.Y, z = (double)first.Z - second.Z;
        return x * x + y * y + z * z;
    }
}
