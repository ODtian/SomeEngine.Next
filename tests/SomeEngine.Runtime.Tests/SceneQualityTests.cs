using SomeEngine.Render.Cluster.Pipeline;
using SomeEngine.Render.Shadows;

namespace SomeEngine.Runtime.Tests;

[Trait("Category", "Hardware")]
[Collection("Rendering content")]
public sealed class SceneQualityTests(RenderingContentFixture content)
{
    [SceneTheory]
    [InlineData("d3d12")]
    [InlineData("vulkan")]
    public async Task LinearCapturesReportCompletedQualityForTheirActualSubmittedFrameAfterCancellation(string backend)
    {
        SceneLinearImage? first = null, resumed = null;
        await SceneImages.RunAsync(backend, new SceneScript(content.Shadows, 4,
        [
            new(1, edit => edit.CaptureLinearImage(image => first = image)),
            new(2, edit => edit.CancelFrame()),
            new(3, edit => edit.CaptureLinearImage(image => resumed = image)),
        ]) { ShadowSettings = new VirtualShadowMapSettings { DynamicResolutionEnabled = true } }, "--no-debug-ui");
        Assert.Equal(1u, first!.Frame.SubmittedFrame);
        Assert.Equal(2u, resumed!.Frame.SubmittedFrame);
        SceneRenderingQuality before = Assert.IsType<SceneRenderingQuality>(first.Frame.Quality);
        SceneRenderingQuality after = Assert.IsType<SceneRenderingQuality>(resumed.Frame.Quality);
        Assert.NotNull(before.Shadows);
        Assert.NotNull(after.Shadows);
        Assert.Equal(before.Shadows.RenderingFrame + 1, after.Shadows.RenderingFrame);
        Assert.Equal(ClusterWorkOverflow.None, after.Shadows.WorkOverflow);
        Assert.True(after.Shadows.RequestedPages > 0, "The completed demand must be captured before its request queue is reset.");
        Assert.All(after.Shadows.Views, view =>
        {
            Assert.True(float.IsFinite(view.AppliedResolutionLodBias));
            Assert.NotEmpty(view.Mips);
            Assert.True(view.Mips.All(mip => mip.Resolution > 0));
        });
        Assert.True(after.Shadows.Views.Sum(view => view.Mips.Sum(mip => (long)mip.WholeDepthPages + mip.ReceiverDepthPages)) > 0);
        Assert.Equal(after.Residency.RegisteredMeshes, after.Residency.PublishedMeshes);
    }
}
