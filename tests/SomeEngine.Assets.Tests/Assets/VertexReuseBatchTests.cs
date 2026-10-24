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
    public void ImportedGeometryRetainsEveryTriangleAndMaterial(int rimCount, int materialCount)
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
        ClusterGeometryBlock geometry = mesh.GetBlock<ClusterGeometryBlock>();
        ClusterGeometry[] baseClusters = geometry.Clusters.Where(cluster => cluster.Level == 0).ToArray();
        Assert.NotEmpty(baseClusters);
        Assert.Equal(rimCount * materialCount, baseClusters.Sum(cluster => cluster.Indices.Length / 3));
        foreach (ClusterGeometry cluster in baseClusters)
        {
            Assert.Equal(cluster.Indices.Length / 3, cluster.TriangleMaterials.Length);
            Assert.All(cluster.Indices, index => Assert.InRange(index, 0, cluster.Vertices.Length - 1));
        }
        foreach (int material in Enumerable.Range(0, materialCount))
            Assert.Equal(rimCount, baseClusters.Sum(cluster => cluster.TriangleMaterials.Count(value => value == material)));
    }
}
