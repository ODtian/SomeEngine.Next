using System.Numerics;
using SomeEngine.Assets.Schema;
using SomeEngine.Graphics;
using SomeEngine.Render.Components;
using SomeEngine.Render.Lighting;
using SomeEngine.Render.Shadows;
using SomeEngine.RenderGraph;
using SomeEngine.Resources;

namespace SomeEngine.Render.Tests;

public sealed class VirtualShadowViewTests(RenderTestDevice graphics) : IClassFixture<RenderTestDevice>
{
    [Fact]
    public async Task ProjectionHistoryKeepsCachedViewsAndMovingALightInvalidatesOnlyItsViews()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Content", "Shaders", "vsm_frame_reset.shader.asset");
        using ShaderAsset asset = await ShaderAsset.OpenAsync(path);
        Shader shader = await asset.GetBlock<ShaderBlock>().CreateAsync(asset.Name);
        var entry = new ShaderEntry(shader, "ResetVirtualShadowFrame", ShaderStage.Compute);
        using var shaders = new VirtualShadowMapShaders
        {
            ResetFrame = entry, ResetReceiverMasks = entry, MarkPages = entry, MarkCoarsePages = entry,
            AllocatePages = entry, ClearPages = entry, InitializeState = entry, InvalidateCasters = entry,
            BuildPagePool = entry, CompactViews = entry, InitializePageHiZ = entry,
            ReducePageHiZ = entry, DynamicResolution = entry,
        };
        using var shadows = new VirtualShadowMap(graphics.Backend, graphics.Device, shaders,
            new VirtualShadowMapSettings
            {
                VirtualResolution = 512, AtlasSize = 256, MaxShadowLights = 2, DirectionalClipmapLevels = 2,
            });
        using var graph = new RenderGraph.RenderGraph(graphics.Backend, graphics.Device,
            [graphics.Backend.GetQueue(graphics.Device, QueueType.Graphics)]);
        using (RenderGraphEdit edit = graph.BeginEdit())
        {
            edit.AddCopyFramePass("commit shadow view state", PassQueueSelection.AnyOfType(QueueType.Graphics), 0,
                new PassOptions(Culling: PassCullingMode.NeverCull),
                static (ref PassDefinition _, ref int _) => { },
                static (ref CopyPassCommandScope _, in int _) => { });
            edit.Commit();
        }
        var view = new RenderView(Matrix4x4.Identity,
            Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI / 2, 1, 0.1f, 100), 128, 128, false);
        var lights = new RenderLightSet();
        uint[]? firstGenerations = null;
        var completions = new QueueCompletion[graph.MaximumQueueCompletionCount];
        for (int frameIndex = 0; frameIndex < 6; frameIndex++)
        {
            lights.Clear();
            lights.AddDirectional(new RenderDirectionalLight(Vector3.UnitY, Vector3.One, 1, uint.MaxValue));
            lights.AddPoint(new RenderPointLight(frameIndex == 5 ? Vector3.UnitX : Vector3.Zero,
                10, Vector3.One, 1, uint.MaxValue));
            shadows.BeginFrame();
            VirtualShadowView[] views = shadows.PrepareViews(lights, view, 1, 1, 1).ToArray();
            shadows.AcquireFrameResources();
            if (firstGenerations is null) firstGenerations = views.Select(value => value.CacheGeneration).ToArray();
            else
                for (int index = 0; index < views.Length; index++)
                    if (frameIndex == 5 && index >= 2)
                        Assert.NotEqual(firstGenerations[index], views[index].CacheGeneration);
                    else
                        Assert.Equal(firstGenerations[index], views[index].CacheGeneration);
            using RenderGraphFrame frame = graph.BeginFrame();
            frame.ObserveExecution(new ViewCommit(shadows));
            int count = frame.Execute(completions);
            for (int index = 0; index < count; index++)
                Assert.Equal(WaitStatus.Completed, graphics.Backend.WaitCpu(completions[index], TimeSpan.FromSeconds(10)));
        }
    }

    private sealed class ViewCommit(VirtualShadowMap shadows) : IRenderGraphFrameExecutionObserver
    {
        public void OnRenderGraphFrameFinished(RenderGraphFrameResult result) =>
            shadows.FinishFrame(result, contentComplete: true)?.Dispose();
    }
}
