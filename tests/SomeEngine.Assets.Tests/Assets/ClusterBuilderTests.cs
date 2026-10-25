using SomeEngine.Geometry.Cluster;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using SomeEngine.Assets.Importers;
using SomeEngine.Assets.Schema;
using SomeEngine.Resources.Geometry;
using Mesh = SomeEngine.Resources.Mesh;
using ValueType = SomeEngine.Resources.Geometry.ValueType;

namespace SomeEngine.Assets.Tests.Assets;

public sealed class ClusterBuilderTests
{
    [Fact]
    public async Task IndependentEncodingSurvivesAssetReopen()
    {
        const int triangles = 8192;
        var positions = new Vector3[triangles * 3];
        uint[] indices = Enumerable.Range(0, positions.Length).Select(value => (uint)value).ToArray();
        for (int triangle = 0; triangle < triangles; triangle++)
        {
            Vector3 origin = new(triangle % 32 * 3, triangle / 32 * 3, 0);
            positions[triangle * 3] = origin;
            positions[triangle * 3 + 1] = origin + Vector3.UnitX;
            positions[triangle * 3 + 2] = origin + Vector3.UnitY;
        }
        using var firstEntered = new ManualResetEventSlim();
        using var secondEntered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        int entered = 0;
        var encoder = new SourceVertexEncoder(() =>
        {
            int index = Interlocked.Increment(ref entered);
            if (index > 2) return;
            (index == 1 ? firstEntered : secondEntered).Set();
            release.Wait();
        });
        Task<MeshAsset> build = Task.Run(() => ClusterBuilder.ProcessRaw(positions, [], indices,
            ClusterBuilderTestMaterials.Slots(), "source grid", encoder));
        try
        {
            Assert.True(firstEntered.Wait(TimeSpan.FromSeconds(10)));
            Assert.True(secondEntered.Wait(TimeSpan.FromSeconds(10)));
            release.Set();
            MeshAsset asset = await build.WaitAsync(TimeSpan.FromSeconds(10));
            string directory = Path.Combine(Path.GetTempPath(), $"SomeEngine-Cluster-Encoding-{Guid.NewGuid():N}");
            Directory.CreateDirectory(directory);
            try
            {
                string path = Path.Combine(directory, "grid.mesh.asset");
                await asset.SaveAsync(path);
                using MeshAsset reopened = await MeshAsset.OpenAsync(path);
                ClusterGeometryBlock geometry = await reopened.GetBlockAsync<ClusterGeometryBlock>();
                ClusterVertexAttributesBlock attributes = await reopened.GetBlockAsync<ClusterVertexAttributesBlock>();
                Assert.True(geometry.Clusters.Count > 1);
                Assert.Equal(geometry.Clusters.Count, attributes.Clusters.Count);
                for (int cluster = 0; cluster < geometry.Clusters.Count; cluster++)
                {
                    uint vertex = geometry.Vertices[(int)geometry.Clusters[cluster].Vertices[0]].SourceVertex;
                    Assert.Equal(vertex, BinaryPrimitives.ReadUInt32LittleEndian(attributes.Clusters[cluster].Data.AsSpan()));
                }
                MeshBlock shape = await reopened.GetBlockAsync<MeshBlock>();
                Mesh mesh = await geometry.CreateAsync(shape, attributes, "source grid");
                await geometry.LoadPagesAsync(mesh, attributes, Enumerable.Range(0, mesh.PageCount));
                Assert.True(mesh.PageCount > 1);
                for (int pageIndex = 0; pageIndex < mesh.PageCount; pageIndex++)
                {
                    Assert.True(mesh.TryGetPage(pageIndex, out var page));
                    var header = MemoryMarshal.Read<MeshPageHeader>(page.Span);
                    for (int cluster = 0; cluster < header.ClusterCount; cluster++)
                    {
                        var record = MemoryMarshal.Read<PackedCluster>(page.Span[
                            ((int)header.ClustersOffset + cluster * PackedCluster.SizeInBytes)..]);
                        int blob = checked((int)(header.VertexBlobsOffset + record.VertexBlobOffset));
                        int values = blob + BinaryPrimitives.ReadInt32LittleEndian(page.Span[(blob + 8)..]);
                        uint source = mesh.ClusterGeometryIndices[(int)header.FirstCluster + cluster];
                        uint vertex = geometry.Vertices[(int)geometry.Clusters[(int)source].Vertices[0]].SourceVertex;
                        Assert.Equal(vertex, BinaryPrimitives.ReadUInt32LittleEndian(page.Span[values..]));
                    }
                }
            }
            finally { Directory.Delete(directory, true); }
        }
        finally
        {
            release.Set();
            (await build.WaitAsync(TimeSpan.FromSeconds(10))).Dispose();
        }
    }

    private sealed class SourceVertexEncoder(Action onEncode) : IClusterVertexEncoder
    {
        public string Compatibility => "test.cluster.source-vertex.v1";
        public IClusterVertexEncoder CreateBatchEncoder() => this;
        public EncodedClusterVertices Encode(ReadOnlySpan<Vector3> positions,
            IReadOnlyList<RawAttribute> attributes, ReadOnlySpan<uint> clusterVertices)
        {
            onEncode();
            byte[] bytes = new byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(bytes, clusterVertices[0]);
            Vector3 min = new(float.PositiveInfinity), max = new(float.NegativeInfinity);
            foreach (uint vertex in clusterVertices)
            {
                min = Vector3.Min(min, positions[(int)vertex]);
                max = Vector3.Max(max, positions[(int)vertex]);
            }
            return new(bytes, min, max);
        }
    }

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
