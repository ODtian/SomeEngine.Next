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

        QueueCompletion[] completions;
        using (var graph = new SomeEngine.RenderGraph.RenderGraph(backend, device))
        {
            TextureHandle target = graph.Import(image);
            TextureViewHandle targetView = graph.CreateTextureView(
                target,
                null,
                GraphTextureViewUsage.ColorAttachment,
                name: "validated graph backbuffer view");
            using (IRasterRenderGraphBuilder builder = graph.AddRasterRenderPass<ClearPassData>(
                       "clear validated swapchain image",
                       out _))
            {
                builder.SetRenderAttachment(
                    targetView,
                    0,
                    GraphAccess.WriteAll,
                    LoadType.Clear,
                    new Vector4(0.125f, 0.25f, 0.5f, 1));
                builder.SetRenderFunc<ClearPassData>(static (_, _) => { });
            }
            completions = graph.Execute();
        }

        Assert.Equal(SwapchainImageStatus.Submitted, image.Status);
        Queue queue = backend.GetQueue(device, QueueType.Graphics);
        PresentStatus status = backend.Present(queue, image);
        Assert.Contains(
            status,
            new[] { PresentStatus.Success, PresentStatus.Suboptimal, PresentStatus.Occluded });
        foreach (ref readonly QueueCompletion completion in completions.AsSpan())
        {
            Assert.Equal(
                WaitStatus.Completed,
                backend.WaitCpu(completion, TimeSpan.FromSeconds(10)));
        }
        backend.CollectCompleted(device);
    }

    private sealed class ClearPassData
    {
    }
}
