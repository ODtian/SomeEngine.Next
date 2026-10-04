using SomeEngine.Assets.Importers;
using SomeEngine.Assets.Schema;

namespace SomeEngine.Assets.Tests.Assets;

public sealed class ClusterLodLevelTests
{
    [Fact]
    public void CoarseAndFineGeometryMeetAtTheSameLodError()
    {
        var (vertices, indices, attributes) = PrimitiveMeshGenerator.CreateIcoSphere(5);
        using var asset = ClusterBuilder.ProcessRaw(vertices, attributes, indices, ClusterBuilderTestMaterials.Slots(), "LOD transitions");
        var clusters = asset.GetBlock<ClusterGeometryBlock>().Clusters;
        var coarse = clusters.Where(cluster => cluster.Level > 0).GroupBy(cluster => cluster.Group).ToDictionary(group => group.Key, group => group.First().Error);
        var transitions = clusters.Where(cluster => cluster.ParentGroup >= 0).ToArray();
        Assert.NotEmpty(transitions);
        foreach (ClusterGeometry cluster in transitions) Assert.Equal(coarse[cluster.ParentGroup], cluster.ParentError);
    }
    [Fact]
    public void ProcessedIcoSphereContainsLowerLodLevels()
    {
        var (vertices, indices, attributes) = PrimitiveMeshGenerator.CreateIcoSphere(5);
        using var asset = ClusterBuilder.ProcessRaw(vertices, attributes, indices, ClusterBuilderTestMaterials.Slots(), "LOD levels");
        int[] levels = asset.GetBlock<ClusterGeometryBlock>().Clusters.Select(cluster => cluster.Level).Distinct().Order().ToArray();
        Assert.True(levels.Length >= 4);
        Assert.Equal(Enumerable.Range(0, levels.Length), levels);
    }
}
