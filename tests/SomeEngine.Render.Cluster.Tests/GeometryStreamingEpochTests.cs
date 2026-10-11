using SomeEngine.Render.Cluster.Pipeline;

namespace SomeEngine.Render.Cluster.Tests;

[Trait("Category", "Hardware")]
public sealed class GeometryStreamingEpochTests
{
    [Fact]
    public async Task FeedbackFromDisposedSceneIsRejectedBeforeNewSceneRenders()
    {
        var feedback = new ClusterStreamingFeedbackChannel();
        var options = new ClusterPipelineOptions
        {
            MaxCandidates = 1024,
            ForceHardwareRaster = true,
            EnableFrameMetricsReadback = true,
        };
        RasterImage previous;
        await using (var scene = await FrameStageScene.CreateAsync("D3D12", false, options,
            await RasterTestGeometry.CreatePlaneAsync(), feedbackChannel: feedback))
        {
            // Completion publishes a real packet. Leave it unread when this scene ends.
            previous = await scene.ReadNextRasterImageAsync();
        }

        await using var next = await FrameStageScene.CreateAsync("D3D12", false, options,
            await RasterTestGeometry.CreatePlaneAsync(), feedbackChannel: feedback);
        ArgumentException error = Assert.Throws<ArgumentException>(next.PumpStreaming);
        Assert.Equal("ticket", error.ParamName);
        RasterImage.AssertEqual(previous, await next.ReadNextRasterImageAsync());
    }
}
