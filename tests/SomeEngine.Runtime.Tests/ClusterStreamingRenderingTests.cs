using System.Numerics;

namespace SomeEngine.Runtime.Tests;

[Trait("Category", "Hardware")]
[Collection("Scene content")]
public sealed class ClusterStreamingRenderingTests(SceneContentFixture content)
{
    [Fact]
    public async Task StreamedGeometryRemainsCoveredWhenAnotherEntityReusesItsMesh()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"SomeEngine-ClusterStreaming-{Guid.NewGuid():N}");
        try
        {
            await SomeEngine.SceneContent.BehaviorScenes.CreateStreamingSphereAsync(content.DirectoryPath, directory);
            var images = new List<SceneImage>();
            await SceneImages.RunAsync("d3d12", new SceneScript(directory, 16,
            [
                new(3, scene => scene.CaptureImage(images.Add)),
                new(7, scene => scene.CaptureImage(images.Add)),
                new(8, scene =>
                {
                    var original = scene.MeshEntities.Single();
                    scene.AddEntity(original, new(new Vector3(0, 0, 6), Quaternion.Identity, 2));
                    scene.DeleteEntity(original);
                }),
                new(15, scene => scene.CaptureImage(images.Add)),
            ]) { GeometryMemoryBytes = 262144, RasterEdgePixels = 4 }, "--no-taa", "--no-vsm");
            Assert.Equal(3, images.Count);
            foreach (SceneImage image in images)
                SceneImages.AssertColorRegion(image, 28, 28, 8, 8, 186, 0, 0);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }
}
