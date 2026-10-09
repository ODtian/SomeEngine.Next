using System.Numerics;
using System.Runtime.InteropServices;
using SomeEngine.Assets.Schema;
using SomeEngine.Resources.Geometry;
using ValueType = SomeEngine.Resources.Geometry.ValueType;

namespace SomeEngine.Assets.Tests.Assets;

public sealed class ClusterLodErrorTests
{
    [Fact]
    public void ReducedFlatMeshReportsOriginalNormalDetail()
    {
        var (positions, indices) = CreateFlatGrid();
        float[] normals = Enumerable.Range(0, positions.Length).SelectMany(index =>
            index % 2 == 0 ? new[] { 0f, 0f, 1f } : new[] { .6f, 0f, .8f }).ToArray();
        using MeshAsset asset = ClusterBuilder.ProcessRaw(positions,
            [new("NORMAL", normals, 3, ValueType.Int8, 3, true)], indices,
            ClusterBuilderTestMaterials.Slots(), "Flat normal detail", new NormalVertexEncoder());
        using MeshAsset flat = ClusterBuilder.ProcessRaw(positions,
            [new("NORMAL", positions.SelectMany(_ => new[] { 0f, 0f, 1f }).ToArray(), 3, ValueType.Int8, 3, true)],
            indices, ClusterBuilderTestMaterials.Slots(), "Flat normal", new NormalVertexEncoder());

        Assert.True(FirstReducedError(asset) > FirstReducedError(flat));
    }

    [Fact]
    public void ReducedFlatMeshReportsOriginalTextureDetail()
    {
        var (positions, indices) = CreateFlatGrid();
        float[] normals = positions.SelectMany(_ => new[] { 0f, 0f, 1f }).ToArray();
        float[] uv = positions.SelectMany(position => new[] { (position.X % 2) * .5f, position.Y / 16 }).ToArray();
        using MeshAsset asset = ClusterBuilder.ProcessRaw(positions,
            [new("NORMAL", normals, 3, ValueType.Int8, 3, true), new("TEXCOORD_0", uv, 2, ValueType.Float16, 2, false)],
            indices, ClusterBuilderTestMaterials.Slots(), "Flat texture detail", new NormalVertexEncoder());
        using MeshAsset flat = ClusterBuilder.ProcessRaw(positions,
            [new("NORMAL", normals, 3, ValueType.Int8, 3, true), new("TEXCOORD_0", new float[positions.Length * 2], 2,
                ValueType.Float16, 2, false)], indices, ClusterBuilderTestMaterials.Slots(), "Flat texture", new NormalVertexEncoder());

        Assert.True(FirstReducedError(asset) > FirstReducedError(flat));
    }

    private static (Vector3[] Positions, uint[] Indices) CreateFlatGrid()
    {
        const int size = 16;
        Vector3[] positions = Enumerable.Range(0, size * size)
            .Select(index => new Vector3(index % size, index / size, 0)).ToArray();
        uint[] indices = Enumerable.Range(0, (size - 1) * (size - 1)).SelectMany(index =>
        {
            uint first = (uint)(index % (size - 1) + index / (size - 1) * size);
            return new[] { first, first + 1, first + size, first + 1, first + size + 1, first + size };
        }).ToArray();
        return (positions, indices);
    }

    [Fact]
    public void ReducedMeshKeepsOriginalVertexPositionsAndAttributes()
    {
        var sphere = PrimitiveMeshGenerator.CreateIcoSphere(2);
        var random = new Random(7);
        Vector3[] positions = sphere.Vertices
            .Select(position => position * (float)(.5 + random.NextDouble())).ToArray();
        RawAttribute normals = sphere.Attributes.Single(attribute => attribute.Name == "NORMAL");
        var sourceVertices = positions.Select((position, index) => (position,
            new Vector3(normals.Data[index * 3], normals.Data[index * 3 + 1], normals.Data[index * 3 + 2]))).ToHashSet();
        using MeshAsset asset = ClusterBuilder.ProcessRaw(positions,
            [normals], sphere.Indices, ClusterBuilderTestMaterials.Slots(), "Uneven sphere",
            new SourceVertexEncoder(sourceVertices));

        Assert.True(FirstReducedError(asset) > 0);
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

    private sealed class SourceVertexEncoder(HashSet<(Vector3 Position, Vector3 Normal)> sourceVertices) : IClusterVertexEncoder
    {
        public string Compatibility => "test.lod.source-vertices.v1";

        public EncodedClusterVertices Encode(ReadOnlySpan<Vector3> positions,
            IReadOnlyList<RawAttribute> attributes, ReadOnlySpan<uint> clusterVertices)
        {
            RawAttribute normals = attributes.Single(attribute => attribute.Name == "NORMAL");
            foreach (uint vertex in clusterVertices)
            {
                int offset = checked((int)vertex * 3);
                Vector3 normal = new(normals.Data[offset], normals.Data[offset + 1], normals.Data[offset + 2]);
                Assert.Contains((positions[(int)vertex], normal), sourceVertices);
            }
            return StandardClusterVertexEncoder.Instance.Encode(positions, attributes, clusterVertices);
        }
    }
}
