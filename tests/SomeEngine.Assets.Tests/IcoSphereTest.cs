using System.IO;
using SomeEngine.Assets;
using SomeEngine.Assets.Importers;
using SomeEngine.Assets.Pipeline;
using SomeEngine.Assets.Schema;
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
        var meshAsset = ClusterBuilder.ProcessRaw(
            vertices,
            attributes,
            indices,
            SomeEngine.Assets.Tests.Assets.ClusterBuilderTestMaterials.Slots(),
            "IcoSphere_LOD5"
        );

        Assert.NotNull(meshAsset.Payload);
        Assert.True(meshAsset.Payload.Value.Length > 0);

        // The runtime contract is one opaque raw vertex record, not a fixed attribute schema.
        Assert.True(meshAsset.VertexStride > 0);

        // Save to test output, not the excluded samples workspace.
        string outputPath = Path.Combine(TestProjectPaths.ProjectRoot(), "TestResults", "SomeEngine.Assets.Tests", "IcoSphere.mesh.asset");
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

        AssetWriter.Write(meshAsset, outputPath);
        Mesh roundTrip = await Mesh.ReadAsync(outputPath);
        Assert.Equal(meshAsset.Payload.Value.ToArray(), roundTrip.Payload!.Value.ToArray());
    }

}
