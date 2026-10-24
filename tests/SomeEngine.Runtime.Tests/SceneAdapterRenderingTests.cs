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
    public async Task StartupFinishesAtTheFirstSceneFrameContainingGeometry()
    {
        SceneImage? image = null;
        var script = new SceneScript(content.DirectoryPath, 3, [new(1, edit => edit.CaptureImage(value => image = value))]);
        var result = await Application.RunAsync(["--hardware-adapter", "--no-taa", "--no-vsm"], script);
        Assert.True(result.Succeeded, result.Error?.ToString());
        Assert.Equal(image!.Frame, result.Startup!.FirstSceneFrame);
        SceneImages.AssertColorRegion(image, 37, 29, 6, 6, 186, 0, 0);
    }

    [Fact]
    [Trait("Category", "Hardware")]
    public async Task StartupFinishesAtTheFirstPresentedFrameAfterCancellation()
    {
        var script = new SceneScript(content.DirectoryPath, 3, [new(0, edit => edit.CancelFrame())]);
        var result = await Application.RunAsync(["--hardware-adapter", "--no-taa", "--no-vsm"], script);
        Assert.True(result.Succeeded, result.Error?.ToString());
        Assert.InRange(result.Startup!.FirstSceneFrame, 1, 2);
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
