using System.Numerics;
using SomeEngine.Render.Cluster.Pipeline;
using Xunit.Abstractions;

namespace SomeEngine.Render.Cluster.Tests;

public sealed class FrameResourceRenderingTests(ITestOutputHelper testOutput)
{
    [Theory]
    [InlineData("D3D12")]
    [InlineData("Vulkan")]
    public async Task ScalarChangesReachTheNextFrameAndMatchFreshMaterial(string backend)
    {
        var options = new ClusterPipelineOptions { MaxCandidates = 4096, EnableFrameMetricsReadback = true };
        var tint = new Vector4(.1f, .8f, .2f, 1);
        RasterImage expected;
        await using (var fresh = await FrameStageScene.CreateAsync(backend, false, options))
        {
            fresh.UseMaterial("Unlit");
            fresh.SetUnlitTint(tint);
            expected = await fresh.ReadRasterImageAsync();
        }
        await using var changed = await FrameStageScene.CreateAsync(backend, false, options);
        changed.UseMaterial("Unlit");
        RasterImage previous = await changed.ReadRasterImageAsync();
        changed.SetUnlitTint(tint);
        RasterImage current = await changed.ReadNextRasterImageAsync();
        Assert.NotEqual(previous.Color, current.Color);
        Assert.Equal(expected.Color, current.Color);
        Assert.Equal(expected.Depth, current.Depth);
        Assert.True(current.Metrics.FrameIndex > previous.Metrics.FrameIndex);
        testOutput.WriteLine($"Scalar update: frame {previous.Metrics.FrameIndex} -> {current.Metrics.FrameIndex}; color and depth match fresh material.");
    }

    [Theory]
    [InlineData("D3D12", false)]
    [InlineData("D3D12", true)]
    [InlineData("Vulkan", false)]
    [InlineData("Vulkan", true)]
    public async Task FrameMetricReadbackFollowsItsConfiguration(string backend, bool metrics)
    {
        var options = new ClusterPipelineOptions { MaxCandidates = 4096, EnableFrameMetricsReadback = metrics };
        await using var scene = await FrameStageScene.CreateAsync(backend, false, options);
        scene.UseMaterial("Unlit");
        if (metrics)
        {
            RasterImage image = await scene.ReadRasterImageAsync();
            Assert.True(image.Metrics.FrameIndex > 0);
            Assert.True(image.Metrics.ResidencyEpoch > 0);
            Assert.Equal(scene.FeedbackStatistics.LastEpoch, image.Metrics.ResidencyEpoch);
            Assert.True(image.Metrics.CandidateCount > 0);
            Assert.True(image.Metrics.ShadingWorkItems > 0);
            Assert.Contains("Read back Cluster frame metrics", scene.PassLabels);
            testOutput.WriteLine($"Metrics: frame={image.Metrics.FrameIndex}, epoch={image.Metrics.ResidencyEpoch}, candidates={image.Metrics.CandidateCount}, shadeItems={image.Metrics.ShadingWorkItems}.");
        }
        else
        {
            await scene.ReadClusterColorAsync();
            Assert.DoesNotContain("Read back Cluster frame metrics", scene.PassLabels);
            Assert.DoesNotContain("Copy Cluster frame metrics", scene.PassLabels);
            Assert.DoesNotContain("Copy primary Cluster candidate metrics", scene.PassLabels);
            testOutput.WriteLine("Metric readback and collection pass count: 0.");
        }
    }
}
