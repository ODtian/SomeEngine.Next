using System.IO;
using SomeEngine.Assets;
using SomeEngine.Assets.Importers;
using SomeEngine.Assets.Pipeline;
using SomeEngine.Assets.Schema;
using SomeEngine.Resources;
using SharpGLTF.Geometry;
using SharpGLTF.Geometry.VertexTypes;
using SharpGLTF.Materials;
using SharpGLTF.Scenes;
using System.Linq;

namespace SomeEngine.Tests;

public class IcoSphereTest
{
    [Fact]
    public async Task TestIcoSphereGeneration()
    {
        // Level 3 subdivision -> 1280 triangles, 642 vertices
        // Level 4 subdivision -> 5120 triangles, 2562 vertices
        // Level 5 subdivision -> 20480 triangles, 10242 vertices
        var (vertices, indices, attributes) =
            PrimitiveMeshGenerator.CreateIcoSphere(5);

        Assert.Equal(20480 * 3, indices.Length);
        Assert.Equal(10242, vertices.Length);

        // Process through ClusterBuilder
        using var meshAsset = ClusterBuilder.ProcessRaw(
            vertices,
            attributes,
            indices,
            SomeEngine.Assets.Tests.Assets.ClusterBuilderTestMaterials.Slots(),
            "IcoSphere_LOD5"
        );

        ClusterGeometryBlock geometry = meshAsset.GetBlock<ClusterGeometryBlock>();
        ClusterVertexAttributesBlock shading = meshAsset.GetBlock<ClusterVertexAttributesBlock>();
        Assert.Equal(geometry.VertexIdentity, shading.VertexIdentity);
        Assert.Equal(20480, geometry.Clusters.Where(cluster => cluster.Level == 0).Sum(cluster => cluster.Indices.Length / 3));
        using var directory = new SomeEngine.Assets.Tests.Assets.AssetTestDirectory();
        string outputPath = directory.File("icosphere.mesh.asset");

        await meshAsset.SaveAsync(outputPath);
        using MeshAsset roundTrip = await MeshAsset.OpenAsync(outputPath);
        ClusterGeometryBlock reopened = roundTrip.GetBlock<ClusterGeometryBlock>();
        Assert.Equal(geometry.Clusters.Count, reopened.Clusters.Count);
        for (int cluster = 0; cluster < geometry.Clusters.Count; cluster++)
            Assert.Equal(geometry.Clusters[cluster].PositionData.ToArray(), reopened.Clusters[cluster].PositionData.ToArray());
        Assert.Equal(shading.Encoding, (await roundTrip.GetBlockAsync<ClusterVertexAttributesBlock>()).Encoding);
    }

}
