using SomeEngine.Geometry.Cluster;
using System.Numerics;
using System.Runtime.InteropServices;
using SomeEngine.Resources.Geometry;
using SomeEngine.Assets.Importers;
using SomeEngine.Assets.Schema;
using ValueType = SomeEngine.Resources.Geometry.ValueType;

namespace SomeEngine.Assets.Tests.Assets;

public sealed class VertexReuseBatchTests
{
    [Theory]
    [InlineData(3, 1)]
    [InlineData(31, 1)]
    [InlineData(32, 3)]
    [InlineData(61, 3)]
    public void ImportedBatchesCoverEveryTriangleWithoutExceedingMeshOutputs(int rimCount, int materialCount)
    {
        var positions = new Vector3[rimCount + materialCount];
        var materialValues = new float[positions.Length];
        for (int material = 0; material < materialCount; material++)
        {
            positions[material] = new Vector3(0, 0, material * 0.1f);
            materialValues[material] = material;
        }
        for (int vertex = 0; vertex < rimCount; vertex++)
        {
            float angle = MathF.Tau * vertex / rimCount;
            positions[materialCount + vertex] = new Vector3(MathF.Cos(angle), MathF.Sin(angle), 0);
        }
        var indices = new List<uint>();
        for (int material = 0; material < materialCount; material++)
            for (int vertex = 0; vertex < rimCount; vertex++)
            {
                indices.Add((uint)material);
                indices.Add((uint)(materialCount + vertex));
                indices.Add((uint)(materialCount + (vertex + 1) % rimCount));
            }
        using var mesh = ClusterBuilder.ProcessRaw(positions,
            [new RawAttribute("_MATERIAL_INDEX", materialValues, 1, ValueType.UInt8, 1, false)],
            indices.ToArray(), ClusterBuilderTestMaterials.Slots(materialCount), "Reuse batch bounds");
        var bytes = mesh.GetBlock<ClusterMeshBlock>().Chunks[ClusterMeshBlock.PayloadChunkKey];
        ReadOnlySpan<byte> page = bytes.ToArray();
        MeshPageHeader header = MemoryMarshal.Read<MeshPageHeader>(page);
        ReadOnlySpan<PackedCluster> clusters = MemoryMarshal.Cast<byte, PackedCluster>(
            page.Slice((int)header.ClustersOffset, checked((int)header.ClusterCount * PackedCluster.SizeInBytes)));
        Assert.NotEmpty(clusters.ToArray());
        int checkedBatches = 0;
        foreach (PackedCluster cluster in clusters)
        {
            int vertexCount = (int)(cluster.PackedCounts & 255u);
            int triangleCount = (int)((cluster.PackedCounts >> 8) & 255u);
            ReadOnlySpan<byte> triangles = page.Slice(
                checked((int)header.IndicesOffset + cluster.TriangleStart), triangleCount * 3);
            int firstEnd = (int)(cluster.PackedRanges & 255u);
            int secondEnd = (int)((cluster.PackedRanges >> 8) & 255u);
            int ranges = firstEnd == triangleCount ? 1 : secondEnd == triangleCount ? 2 : 3;
            UInt128 encoded = cluster.VertReuseBatchWord0 | ((UInt128)cluster.VertReuseBatchWord1 << 32) |
                ((UInt128)cluster.VertReuseBatchWord2 << 64) | ((UInt128)cluster.VertReuseBatchWord3 << 96);
            int covered = 0;
            int flatBatch = 0;
            for (int range = 0; range < ranges; range++)
            {
                int rangeEnd = range == 0 ? firstEnd : range == 1 ? secondEnd : triangleCount;
                int batchCount = (int)((encoded >> (range * 4)) & 15);
                Assert.InRange(batchCount, 1, 15);
                for (int batch = 0; batch < batchCount; batch++, flatBatch++)
                {
                    Assert.InRange(12 + flatBatch * 5, 12, 123);
                    int count = (int)((encoded >> (12 + flatBatch * 5)) & 31) + 1;
                    Assert.InRange(count, 1, 32);
                    Assert.InRange(covered + count, covered + 1, rangeEnd);
                    var vertices = new HashSet<byte>();
                    foreach (byte vertex in triangles.Slice(covered * 3, count * 3))
                    {
                        Assert.InRange((int)vertex, 0, vertexCount - 1);
                        vertices.Add(vertex);
                    }
                    Assert.InRange(vertices.Count, 1, 32);
                    covered += count;
                    checkedBatches++;
                }
                Assert.Equal(rangeEnd, covered);
            }
            Assert.Equal(triangleCount, covered);
        }
        Assert.True(checkedBatches > 0);
    }
}
