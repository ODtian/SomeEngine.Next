namespace SomeEngine.Runtime.Tests;

[Collection("Scene content")]
public sealed class SceneAdapterRenderingTests(SceneContentFixture content)
{
    [Fact(Skip = "#128: WARP Cluster first-frame completion times out with Mesh shaders.")]
    [Trait("Category", "Software")]
    public async Task SoftwareFrameLoopWithGraphicsQueueDeliversRequestedImages()
    {
        SceneImage? image = null;
        var script = new SceneScript(content.DirectoryPath, 4, [new(3, edit => edit.CaptureImage(value => image = value))]);
        var result = await Application.RunAsync(["--no-async-compute", "--no-taa", "--no-vsm"], script);
        Assert.True(result.Succeeded, result.Error?.ToString());
        Assert.Equal(4, result.CompletedFrames);
        Assert.Equal(3, image!.Frame);
    }

    [Fact(Skip = "#128: WARP Cluster first-frame completion times out with vertex/pixel raster.")]
    [Trait("Category", "Software")]
    public async Task SoftwareFrameLoopWithVertexRasterDeliversRequestedImages()
    {
        SceneImage? image = null;
        var script = new SceneScript(content.DirectoryPath, 4, [new(3, edit => edit.CaptureImage(value => image = value))]);
        var result = await Application.RunAsync(["--no-mesh-shaders", "--no-async-compute", "--no-taa", "--no-vsm"], script);
        Assert.True(result.Succeeded, result.Error?.ToString());
        Assert.Equal(4, result.CompletedFrames);
        Assert.Equal(3, image!.Frame);
    }

    [Fact]
    [Trait("Category", "Hardware")]
    public async Task InsufficientGeometryWorkMemoryFailsInsteadOfPresentingAnIncompleteScene()
    {
        SceneImage? incomplete = null;
        var script = new SceneScript(content.DirectoryPath, 4,
            [new(0, edit => edit.CaptureImage(image => incomplete = image))]) { ClusterWorkCapacity = 1 };
        var result = await Application.RunAsync(["--hardware-adapter", "--no-taa", "--no-vsm"], script);
        Assert.Equal(ApplicationFailure.Rendering, result.Failure);
        var overflow = Assert.IsType<SomeEngine.Render.Cluster.Pipeline.ClusterWorkOverflowException>(result.Error!.GetBaseException());
        Assert.Equal(1u, overflow.Capacity);
        Assert.NotEqual(SomeEngine.Render.Cluster.Pipeline.ClusterWorkOverflow.None, overflow.Work);
        Assert.Null(incomplete);
    }

    [Fact]
    [Trait("Category", "Hardware")]
    public async Task HardwareFrameLoopDeliversRequestedImages()
    {
        SceneImage? image = null;
        var script = new SceneScript(content.DirectoryPath, 4, [new(3, edit => edit.CaptureImage(value => image = value))]);
        var result = await Application.RunAsync(["--hardware-adapter", "--no-taa", "--no-vsm"], script);
        Assert.True(result.Succeeded, result.Error?.ToString());
        Assert.Equal(4, result.CompletedFrames);
        Assert.Equal(3, image!.Frame);
        Assert.Equal(64 * 64 * 4, image.Pixels.Length);
    }
}
