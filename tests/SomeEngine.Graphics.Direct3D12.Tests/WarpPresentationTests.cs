using SomeEngine.Graphics.Direct3D12;
using Xunit;

namespace SomeEngine.Graphics.Direct3D12.Tests;

public sealed class WarpPresentationTests
{
    [Fact]
    public void Acquire_submit_present_and_reconfigure_enforce_sequence_and_commit_boundaries()
    {
        SwapchainImage invalid = default;
        Assert.Throws<InvalidOperationException>(() => _ = invalid.Texture);
        Assert.Throws<InvalidOperationException>(() => _ = invalid.Status);

        using D3D12TestWindow window = new();
        using IGraphicsBackend backend = new D3D12Backend();
        using Device device = D3D12TestSupport.CreateWarpDevice(backend);
        using Surface surface = backend.CreateSurface(new SurfaceDesc(
            NativeWindowType.Win32,
            window.Handle));
        SwapchainConfig config = new(
            Width: 64,
            Height: 64,
            Format.R8G8B8A8UNorm,
            ColorSpace.Srgb,
            PresentType.Mailbox,
            AllowTearing: false,
            MaximumFrameLatency: 2);
        using Swapchain swapchain = backend.CreateSwapchain(
            device,
            new SwapchainDesc(
                surface,
                ImageCount: 2,
                TextureUsages.ColorAttachment,
                config));
        using CommandContext context = backend.CreateCommandContext(
            device,
            new CommandContextDesc(QueueType.Graphics, 0, 1));
        Queue queue = backend.GetQueue(device, QueueType.Graphics);

        Assert.Throws<ArgumentOutOfRangeException>(() => backend.Acquire(
            swapchain,
            new SwapchainAcquireOptions(TimeSpan.FromMilliseconds(-2)),
            out _));
        Assert.Equal(
            SwapchainAcquireStatus.Success,
            backend.Acquire(
                swapchain,
                new SwapchainAcquireOptions(TimeSpan.FromSeconds(2), PreserveContents: true),
                out SwapchainImage image));
        Assert.Equal(SwapchainImageStatus.Acquired, image.Status);
        Assert.Equal(TextureLayout.Undefined, image.InitialLayout);
        Assert.Equal(ResourceAccess.NoAccess, image.InitialAccess);
        Assert.Same(swapchain, image.Swapchain);

        Assert.Equal(
            SwapchainAcquireStatus.Timeout,
            backend.Acquire(
                swapchain,
                new SwapchainAcquireOptions(TimeSpan.Zero),
                out SwapchainImage timedOut));
        Assert.Throws<InvalidOperationException>(() => _ = timedOut.Texture);

        ulong originalGeneration = swapchain.Info.Generation;
        Assert.Equal(ReconfigureStatus.Busy, backend.Reconfigure(swapchain, config));
        Assert.Equal(originalGeneration, swapchain.Info.Generation);
        Assert.Equal(SwapchainImageStatus.Acquired, image.Status);
        Assert.Throws<InvalidOperationException>(() => backend.Present(queue, image));
        Assert.Equal(SwapchainImageStatus.Acquired, image.Status);

        TextureSubresourceRange range = new(0, 1, 0, 1, TextureAspects.Color);
        backend.Begin(context);
        backend.Barrier(context, new TextureBarrier(
            image.Texture,
            range,
            image.InitialSync,
            PipelineSync.RenderTarget,
            image.InitialAccess,
            ResourceAccess.RenderTarget,
            image.InitialLayout,
            TextureLayout.RenderTarget));
        using (RecordedCommands notPresentReady = backend.End(context))
        {
            Assert.Throws<InvalidOperationException>(() => backend.Submit(
                queue,
                new QueueSubmitDesc([], [], [notPresentReady], [image], [])));
            Assert.Equal(SwapchainImageStatus.Acquired, image.Status);
        }

        backend.Begin(context);
        backend.Barrier(context, new TextureBarrier(
            image.Texture,
            range,
            image.InitialSync,
            PipelineSync.None,
            image.InitialAccess,
            ResourceAccess.NoAccess,
            image.InitialLayout,
            TextureLayout.Present));
        using RecordedCommands presentReady = backend.End(context);
        QueueCompletion completion = backend.Submit(
            queue,
            new QueueSubmitDesc([], [], [presentReady], [image], []));
        Assert.Equal(SwapchainImageStatus.Submitted, image.Status);
        PresentStatus present = backend.Present(queue, image);
        Assert.Contains(
            present,
            new[] { PresentStatus.Success, PresentStatus.Suboptimal, PresentStatus.Occluded });
        Assert.Equal(SwapchainImageStatus.Presented, image.Status);
        Assert.Throws<InvalidOperationException>(() => backend.Present(queue, image));
        Assert.Equal(
            WaitStatus.Completed,
            backend.WaitCpu(completion, TimeSpan.FromSeconds(10)));

        SwapchainConfig resized = config with { Width = 96, Height = 80 };
        Assert.Equal(ReconfigureStatus.Success, backend.Reconfigure(swapchain, resized));
        Assert.Equal(originalGeneration + 1, swapchain.Info.Generation);
        Assert.Equal(96u, swapchain.Info.Config.Width);
        Assert.Equal(80u, swapchain.Info.Config.Height);
        Assert.Throws<InvalidOperationException>(() => _ = image.Texture);
        Assert.Throws<InvalidOperationException>(() => _ = image.Status);

        ulong resizedGeneration = swapchain.Info.Generation;
        SwapchainConfig unsupported = resized with
        {
            Format = Format.R8G8B8A8UNorm,
            ColorSpace = ColorSpace.Hdr10,
        };
        Assert.Equal(ReconfigureStatus.Unsupported, backend.Reconfigure(swapchain, unsupported));
        Assert.Equal(resizedGeneration, swapchain.Info.Generation);
    }
}
