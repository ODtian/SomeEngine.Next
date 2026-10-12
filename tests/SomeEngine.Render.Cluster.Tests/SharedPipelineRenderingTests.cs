using System.Numerics;
using SomeEngine.Graphics;
using SomeEngine.Render.Cluster.Pipeline;
using SomeEngine.Render.Instances;
using SomeEngine.Render.Systems;
using SomeEngine.RenderGraph;
using SomeEngine.Resources;

namespace SomeEngine.Render.Cluster.Tests;

[Trait("Category", "Hardware")]
public sealed class SharedPipelineRenderingTests
{
    [Fact]
    public async Task SharedGeometrySurvivesASecondViewAndItsRemovalAfterSubmission()
    {
        await using var scene = await FrameStageScene.CreateAsync("D3D12", false,
            new ClusterPipelineOptions { MaxCandidates = 4096, EnableFrameMetricsReadback = true });
        scene.UseMaterial("Unlit");
        scene.OpenAdditionalView();
        await scene.DiscardNextFrameAsync();
        Assert.Equal(0ul, scene.FeedbackStatistics.ReceivedFrames);
        RasterImage first = await scene.ReadNextRasterImageAsync();
        Assert.Equal(1ul, scene.FeedbackStatistics.ReceivedFrames);
        Assert.Equal(first.Color, await scene.ReadAdditionalColorAsync());

        scene.SetUnlitTint(new Vector4(.2f, .8f, .3f, 1));
        RasterImage tinted = await scene.ReadNextRasterImageAsync();
        Assert.NotEqual(first.Color, tinted.Color);
        Assert.Equal(tinted.Color, await scene.ReadAdditionalColorAsync());
        scene.UseMaterial(1, scene.CreateShadeMaterial(new Vector4(1, 0, 0, 1)));
        RasterImage replaced = await scene.ReadNextRasterImageAsync();
        Assert.NotEqual(tinted.Color, replaced.Color);
        Assert.Equal(replaced.Color, await scene.ReadAdditionalColorAsync());
        RenderGraphStatistics twoViews = scene.CurrentStatistics;

        FrameStageSubmission submitted = await scene.SubmitNextFrameAsync();
        scene.CloseAdditionalView();
        scene.WaitForFrame(submitted);
        scene.CollectCompletedFrames();
        Assert.Equal(7ul, scene.FeedbackStatistics.ReceivedFrames);
        RasterImage remaining = await scene.ReadNextRasterImageAsync();
        RasterImage.AssertEqual(replaced, remaining);
        Assert.Equal(8ul, scene.FeedbackStatistics.ReceivedFrames);
        Assert.True(scene.CurrentStatistics.DeclaredPassCount < twoViews.DeclaredPassCount);
        Assert.True(scene.CurrentStatistics.BufferCount < twoViews.BufferCount);
        Assert.True(scene.CurrentStatistics.TextureCount < twoViews.TextureCount);
    }
}

internal sealed partial class FrameStageScene
{
    private Func<ClusterPipelineSystem>? _createAdditionalPipeline;
    private ClusterPipelineSystem? _additionalPipeline;
    private ClusterPipelineSystem? _readbackPipeline;
    private RenderFrameSystems? _additionalFrames;
    internal RenderGraphStatistics CurrentStatistics => _statistics;

    private void InitializeAdditionalViews(ClusterMaterialAssignments assignments,
        IRenderInstanceBatchSource<RenderInstanceSingleGroup> instances, ClusterShaders shaders,
        IGraphResourceProvider resources, ClusterGraphRegions regions)
    {
        _createAdditionalPipeline = () => new ClusterPipelineSystem(_backend, _device, _renderDevice,
            _cluster, instances, ClusterRenderFeature.InstanceLayout, assignments, shaders, _graph,
            regions, new ClusterPipelineOutputDescription(Width, Height, 1), _feedback, resources,
            new ClusterPipelineOptions { MaxCandidates = 4096 });
    }

    internal void OpenAdditionalView()
    {
        _additionalPipeline = _createAdditionalPipeline!();
        _additionalFrames = new RenderFrameSystems(_world, _instances);
        _additionalFrames.Add(_additionalPipeline);
        _additionalFrames.Initialize();
    }

    internal async Task<byte[]> ReadAdditionalColorAsync()
    {
        _readbackPipeline = _additionalPipeline ?? throw new InvalidOperationException("No additional view is open.");
        try { await RenderAsync(); return ReadColor(); }
        finally { _readbackPipeline = null; }
    }

    internal void CloseAdditionalView()
    {
        if (_additionalPipeline is null) return;
        _readbackPipeline = null;
        _additionalPipeline.UninstallGraph();
        _additionalFrames!.Dispose();
        _additionalFrames = null;
        _additionalPipeline = null;
    }
}
