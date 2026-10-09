using System.Numerics;
using System.Runtime.InteropServices;
using SomeEngine.Assets.Schema;
using SomeEngine.Resources.Geometry;
using ValueType = SomeEngine.Resources.Geometry.ValueType;

namespace SomeEngine.Assets.Tests.Assets;

public sealed class ClusterLodErrorTests
{
    [Fact]
    public void ReducedMeshRetainsTheGreatestSurfaceError()
    {
        var sphere = PrimitiveMeshGenerator.CreateIcoSphere(2);
        var random = new Random(7);
        Vector3[] positions = sphere.Vertices
            .Select(position => position * (float)(.5 + random.NextDouble())).ToArray();
        using MeshAsset asset = ClusterBuilder.ProcessRaw(positions,
            [sphere.Attributes.Single(attribute => attribute.Name == "NORMAL")],
            sphere.Indices, ClusterBuilderTestMaterials.Slots(), "Uneven sphere");

        // The reference reduction reaches this error before its final triangle count.
        Assert.InRange(FirstReducedError(asset), .11055f, .11065f);
    }

    [Fact]
    public void RepeatedEqualAttributeVerticesKeepTheSameSurfaceDetail()
    {
        var sphere = PrimitiveMeshGenerator.CreateIcoSphere(3);
        RawAttribute normal = sphere.Attributes.Single(attribute => attribute.Name == "NORMAL");
        using MeshAsset shared = ClusterBuilder.ProcessRaw(sphere.Vertices,
            [normal, new("COLOR_0", new float[sphere.Vertices.Length], 1, ValueType.Float32, 1, false)],
            sphere.Indices, ClusterBuilderTestMaterials.Slots(), "Shared sphere", new NormalVertexEncoder());

        Vector3[] positions = sphere.Indices.Select(index => sphere.Vertices[index]).ToArray();
        float[] normals = positions.SelectMany(position => new[] { position.X, position.Y, position.Z }).ToArray();
        // Equal float values with distinct source bytes also occur when separate primitives are joined.
        float[] colors = Enumerable.Range(0, positions.Length).Select(index => index % 2 == 0 ? 0f : -0f).ToArray();
        uint[] indices = Enumerable.Range(0, positions.Length).Select(index => (uint)index).ToArray();
        using MeshAsset repeated = ClusterBuilder.ProcessRaw(positions,
            [new("NORMAL", normals, 3, ValueType.Int8, 3, true), new("COLOR_0", colors, 1, ValueType.Float32, 1, false)],
            indices, ClusterBuilderTestMaterials.Slots(), "Repeated sphere", new NormalVertexEncoder());

        Assert.InRange(FirstReducedError(repeated), 0, FirstReducedError(shared) * 2);
    }

    private static float FirstReducedError(MeshAsset asset)
    {
        ClusterMeshBlock geometry = asset.GetBlock<ClusterMeshBlock>();
        ReadOnlySpan<byte> payload = geometry.Chunks[ClusterMeshBlock.PayloadChunkKey].Span;
        int firstLevel = int.MaxValue;
        float error = 0;
        foreach (var description in geometry.PageDigests)
        {
            ReadOnlySpan<byte> page = payload.Slice((int)description.Offset, (int)description.Length);
            MeshPageHeader header = MemoryMarshal.Read<MeshPageHeader>(page);
            foreach (PackedCluster cluster in MemoryMarshal.Cast<byte, PackedCluster>(
                page.Slice((int)header.ClustersOffset, (int)header.ClusterCount * PackedCluster.SizeInBytes)))
            {
                int level = (int)((cluster.PackedCounts >> 16) & 255);
                if (level == 0 || level > firstLevel) continue;
                if (level < firstLevel) { firstLevel = level; error = 0; }
                error = Math.Max(error, (float)BitConverter.UInt16BitsToHalf(cluster.LODErrorHalf));
            }
        }
        Assert.True(firstLevel < int.MaxValue);
        return error;
    }

    private sealed class NormalVertexEncoder : IClusterVertexEncoder
    {
        public string Compatibility => "test.lod.normal.v1";

        public EncodedClusterVertices Encode(ReadOnlySpan<Vector3> positions,
            IReadOnlyList<RawAttribute> attributes, ReadOnlySpan<uint> clusterVertices)
            => StandardClusterVertexEncoder.Instance.Encode(positions,
                attributes.Where(attribute => attribute.Name != "COLOR_0").ToArray(), clusterVertices);
    }
}
