using System.Numerics;
using SomeEngine.Graphics.Direct3D12;
using SomeEngine.Graphics.Validation;
using SomeEngine.RenderGraph;
using Xunit;

namespace SomeEngine.Graphics.Direct3D12.Tests;

public sealed class WarpRenderGraphPresentationTests
{
    [Fact]
    public void Validated_render_graph_imports_acquired_image_submits_and_presents()
    {
        using D3D12TestWindow window = new();
        D3D12ValidationOptions nativeValidation = new(
            DisableGpuBasedValidation: true,
            DisableSynchronizedQueueValidation: true);
        using var backend = new ValidationLayer(
            new D3D12Backend(new D3D12BackendOptions(nativeValidation)));
        using Device device = D3D12TestSupport.CreateWarpDevice(backend);
        using Surface surface = backend.CreateSurface(new SurfaceDesc(
            NativeWindowType.Win32,
            window.Handle,
            Label: "validated graph surface"));
        SwapchainConfig config = new(
            64,
            64,
            Format.R8G8B8A8UNorm,
            ColorSpace.Srgb,
            PresentType.Fifo,
            AllowTearing: false,
            MaximumFrameLatency: 2);
        using Swapchain swapchain = backend.CreateSwapchain(
            device,
            new SwapchainDesc(
                surface,
                2,
                TextureUsages.ColorAttachment,
                config,
                "validated graph swapchain"));

        Assert.Equal(
            SwapchainAcquireStatus.Success,
            backend.Acquire(
                swapchain,
                new SwapchainAcquireOptions(TimeSpan.FromSeconds(2), PreserveContents: false),
                out SwapchainImage image));

        Queue graphicsQueue = backend.GetQueue(device, QueueType.Graphics);
        using var graph = new SomeEngine.RenderGraph.RenderGraph(
            backend,
            device,
            [graphicsQueue]);
        var completions = new QueueCompletion[graph.MaximumQueueCompletionCount];
        int completionCount;
        using (RenderGraphFrame frame = graph.BeginFrame())
        {
            GraphTextureId target = frame.Import(image, graphicsQueue);
            TextureInfo texture = image.Texture.Info;
            TextureSubresourceRange range = new(
                0,
                texture.MipLevelCount,
                0,
                texture.ArrayLayerCount,
                TextureAspects.Color);
            GraphColorAttachmentViewId targetView = frame.CreateColorAttachmentView(
                target,
                range,
                texture.Format,
                TextureViewDimension.Texture2D,
                "validated graph backbuffer view");
            _ = frame.AddRasterPass(
                "clear validated swapchain image",
                PassQueueSelection.Exact(graphicsQueue),
                new ClearPassData(targetView),
                default,
                static (ref PassDefinition access, ref ClearPassData data) =>
                    access.ColorAttachment(
                        0,
                        data.Target,
                    LoadType.Clear,
                        StoreType.Store,
                        WriteCoverage.Complete,
                        new Vector4(0.125f, 0.25f, 0.5f, 1)),
                static (ref RasterPassCommandScope _, in ClearPassData _) => { });
            completionCount = frame.Execute(completions);
        }

        Assert.Equal(SwapchainImageStatus.Submitted, image.Status);
        PresentStatus status = backend.Present(graphicsQueue, image);
        Assert.Contains(
            status,
            new[] { PresentStatus.Success, PresentStatus.Suboptimal, PresentStatus.Occluded });
        foreach (ref readonly QueueCompletion completion in completions.AsSpan(0, completionCount))
        {
            Assert.Equal(
                WaitStatus.Completed,
                backend.WaitCpu(completion, TimeSpan.FromSeconds(10)));
        }
        backend.CollectCompleted(device);
    }

    private readonly record struct ClearPassData(GraphColorAttachmentViewId Target);
}
