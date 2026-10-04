using System.Numerics;
using SomeEngine.Assets.Importers;
using SomeEngine.Assets.Schema;
using ValueType = SomeEngine.Resources.Geometry.ValueType;

namespace SomeEngine.Assets.Tests.Assets;

public sealed class ClusterBuilderTests
{
    [Fact]
    public void RawMeshSeparatesQuantizedPositionsAndAllShadingAttributes()
    {
        Vector3[] positions = [Vector3.Zero, Vector3.UnitX, Vector3.UnitY];
        using var asset = ClusterBuilder.ProcessRaw(positions,
            [new("NORMAL", [0,0,1, 0,0,1, 0,0,1], 3, ValueType.Int8, 3, true),
             new("TEXCOORD_7", [0,0, 1,0, 0,1], 2, ValueType.Float32, 2, false),
             new("_CUSTOM", [3,5,7], 1, ValueType.Float32, 1, false)],
            [0,1,2], ClusterBuilderTestMaterials.Slots(), "triangle");
        ClusterGeometryBlock geometry = asset.GetBlock<ClusterGeometryBlock>();
        ClusterVertexAttributesBlock attributes = asset.GetBlock<ClusterVertexAttributesBlock>();
        Assert.Equal(geometry.VertexIdentity, attributes.VertexIdentity);
        ClusterGeometry cluster = Assert.Single(geometry.Clusters);
        for (int vertex = 0; vertex < cluster.Vertices.Length; vertex++)
            Assert.InRange(Vector3.Distance(positions[cluster.Vertices[vertex]], cluster.DecodePosition(vertex)), 0, cluster.PositionStep * 2);
        Assert.Equal(["NORMAL", "TEXCOORD_7", "_CUSTOM"], Assert.Single(attributes.Clusters).Attributes.Select(value => value.Name));
        ClusterVertexAttribute custom = attributes.Clusters[0].Attributes.Single(value => value.Name == "_CUSTOM");
        for (int vertex = 0; vertex < cluster.Vertices.Length; vertex++)
            Assert.Equal(new double[] {3,5,7}[cluster.Vertices[vertex]], custom.ReadComponent(vertex, 0));
    }

    [Fact]
    public void ReplacementEncoderControlsItsAttributeBytesAndCompatibility()
    {
        using var asset = ClusterBuilder.ProcessRaw([Vector3.Zero, Vector3.UnitX, Vector3.UnitY], [], [0,1,2],
            ClusterBuilderTestMaterials.Slots(), "custom attributes", new SentinelVertexEncoder());
        ClusterVertexAttributesBlock attributes = asset.GetBlock<ClusterVertexAttributesBlock>();
        Assert.Equal(SentinelVertexEncoder.CompatibilityName, attributes.Encoding);
        Assert.Equal(new byte[] {0xD7, 3}, Assert.Single(Assert.Single(attributes.Clusters).Attributes).Data.ToArray());
        Assert.Equal(Vector3.UnitX, asset.GetBlock<ClusterGeometryBlock>().Clusters[0].DecodePosition(1));
    }
    private sealed class SentinelVertexEncoder : IClusterVertexEncoder
    {
        internal const string CompatibilityName = "test.vertex.sentinel.v1";

        public string Compatibility => CompatibilityName;

        public EncodedClusterVertices Encode(
            ReadOnlySpan<Vector3> positions,
            IReadOnlyList<RawAttribute> attributes,
            ReadOnlySpan<uint> clusterVertices)
        {
            Assert.Empty(attributes);
            Assert.False(clusterVertices.IsEmpty);
            Vector3 min = new(float.PositiveInfinity);
            Vector3 max = new(float.NegativeInfinity);
            foreach (uint sourceVertex in clusterVertices)
            {
                Vector3 position = positions[checked((int)sourceVertex)];
                min = Vector3.Min(min, position);
                max = Vector3.Max(max, position);
            }

            return new EncodedClusterVertices(
                new byte[] { 0xD7, checked((byte)clusterVertices.Length) },
                min,
                max);
        }
    }

}
