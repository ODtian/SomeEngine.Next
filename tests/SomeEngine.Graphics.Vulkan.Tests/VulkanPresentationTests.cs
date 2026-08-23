namespace SomeEngine.Graphics.Vulkan.Tests;

using System.Numerics;
using Xunit;

public sealed class VulkanPresentationTests
{
    [Fact]
    public void Win32_swapchain_acquire_submit_and_present_use_gpu_semaphores()
    {
        if (!OperatingSystem.IsWindows())
            return;
        using var window = new VulkanTestWindow();
        using IGraphicsBackend backend = VulkanGraphicsBackend.Create();
        using Surface surface = backend.CreateSurface(new SurfaceDesc(
            NativeWindowType.Win32,
            window.Handle));
        DeviceQueueDesc[] queues = [new DeviceQueueDesc(QueueType.Graphics)];
        using Device device = backend.CreateDevice(new DeviceDesc(
            default,
            queues,
            requiredFeatures: DeviceFeatures.Presentation));
        Queue queue = backend.GetQueue(device, QueueType.Graphics);
        using Swapchain swapchain = backend.CreateSwapchain(device, new SwapchainDesc(
            surface,
            3,
            TextureUsages.ColorAttachment,
            new SwapchainConfig(
                160,
                120,
                Format.R8G8B8A8UNorm,
                ColorSpace.Srgb,
                PresentType.Fifo,
                AllowTearing: false,
                MaximumFrameLatency: 2)));
        Assert.Equal(
            SwapchainAcquireStatus.Success,
            backend.Acquire(
                swapchain,
                new SwapchainAcquireOptions(TimeSpan.FromSeconds(2)),
                out SwapchainImage image));
        TextureSubresourceRange range = new(0, 1, 0, 1, TextureAspects.Color);
        using ColorAttachmentView attachment = backend.CreateColorAttachmentView(
            device,
            new ColorAttachmentViewDesc(
                image.Texture,
                range,
                swapchain.Info.Config.Format,
                TextureViewDimension.Texture2D));
        using CommandContext context = backend.CreateCommandContext(
            device,
            new CommandContextDesc(QueueType.Graphics, 0, 1));
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
        ColorAttachmentDesc[] colors =
        [
            new(attachment, LoadType.Clear, StoreType.Store, new Vector4(0, 1, 0, 1)),
        ];
        backend.BeginRendering(context, new RenderingDesc(colors, null, 160, 120));
        backend.EndRendering(context);
        backend.Barrier(context, new TextureBarrier(
            image.Texture,
            range,
            PipelineSync.RenderTarget,
            PipelineSync.None,
            ResourceAccess.RenderTarget,
            ResourceAccess.NoAccess,
            TextureLayout.RenderTarget,
            TextureLayout.Present));
        using RecordedCommands commands = backend.End(context);
        QueueCompletion completion = backend.Submit(
            queue,
            new QueueSubmitDesc([], [], [commands], [image], []));
        PresentStatus present = backend.Present(queue, image);
        Assert.True(present is PresentStatus.Success or PresentStatus.Suboptimal);
        Assert.Equal(WaitStatus.Completed, backend.WaitCpu(completion, TimeSpan.FromSeconds(2)));
    }
}
