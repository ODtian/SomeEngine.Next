using SomeEngine.Render.Cluster.Pipeline;

namespace SomeEngine.Runtime.Tests;

[Trait("Category", "Hardware")]
[Collection("Rendering content")]
public sealed class RasterWorkFailureTests(RenderingContentFixture content)
{
    [SceneTheory]
    [InlineData("d3d12")]
    [InlineData("vulkan")]
    public async Task InsufficientSurfaceWorkMemoryDoesNotDeliverTruncatedGeometry(string backend)
    {
        SceneImage? incomplete = null;
        var script = new SceneScript(content.Tessellation, 4,
            [new(0, edit => edit.CaptureImage(image => incomplete = image))]) { ClusterWorkCapacity = 4096 };
        ApplicationResult result = await Application.RunAsync(
            ["--hardware-adapter", "--graphics-backend", backend, "--no-taa", "--no-vsm"], script);
        Assert.Equal(ApplicationFailure.Rendering, result.Failure);
        var overflow = Assert.IsType<ClusterWorkOverflowException>(result.Error!.GetBaseException());
        Assert.Equal(4096u, overflow.Capacity);
        Assert.Equal(ClusterWorkOverflow.Tessellation, overflow.Work);
        Assert.Null(incomplete);
    }
}
