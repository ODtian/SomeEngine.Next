using System.Diagnostics;
using System.Numerics;
using SomeEngine.Graphics.Validation;
using Xunit;

namespace SomeEngine.Graphics.Vulkan.Tests;

public sealed class VulkanPresentationTests
{
    [WindowsFact]
    public void Requested_presentation_exposes_the_typed_capability()
    {
        using IGraphicsBackend backend = VulkanGraphicsBackend.Create();
        using Device device = CreateDevice(backend);
        Assert.True(backend.TryGetCapability(device, out Presentation? presentation));
        Assert.NotNull(presentation);
        Assert.Equal(DeviceStatus.Active, device.Status);
    }

    [WindowsFact]
    public void Presentation_and_indirect_commands_support_the_application_queue_set()
    {
        using IGraphicsBackend backend = VulkanGraphicsBackend.Create();
        AdapterEnumerationOptions options = new(AdapterPreference.HighPerformance);
        backend.TryEnumerateAdapters(options, [], out int count);
        var adapters = new AdapterInfo[count];
        Assert.True(backend.TryEnumerateAdapters(options, adapters, out _));
        AdapterInfo adapter = adapters.First(item => item.HardwareAccelerated);
        using Device device = backend.CreateDevice(new DeviceDesc(adapter.Id,
            [new DeviceQueueDesc(QueueType.Graphics), new DeviceQueueDesc(QueueType.Compute),
             new DeviceQueueDesc(QueueType.Copy)],
            requiredFeatures: DeviceFeatures.Presentation | DeviceFeatures.IndirectCommands));

        Assert.True(backend.TryGetCapability(device, out Presentation? presentation));
        Assert.NotNull(presentation);
        Assert.True(backend.TryGetCapability(device, out IndirectCommands? indirect));
        Assert.NotNull(indirect);
        Assert.Equal(QueueType.Graphics, backend.GetQueue(device, QueueType.Graphics).Type);
        Assert.Equal(QueueType.Compute, backend.GetQueue(device, QueueType.Compute).Type);
        Assert.Equal(QueueType.Copy, backend.GetQueue(device, QueueType.Copy).Type);
        Assert.Equal(DeviceStatus.Active, device.Status);
    }

    [WindowsFact]
    public void Presentation_requires_a_graphics_queue()
    {
        using IGraphicsBackend backend = VulkanGraphicsBackend.Create();
        Assert.Throws<NotSupportedException>(() => backend.CreateDevice(new DeviceDesc(
            default, [new DeviceQueueDesc(QueueType.Compute)],
            requiredFeatures: DeviceFeatures.Presentation)));
    }

    [WindowsFact]
    public void Repeated_presented_frames_keep_their_rendered_color_and_image_lifecycle()
    {
        using IGraphicsBackend backend = new ValidationLayer(
            VulkanGraphicsBackend.Create(), new ValidationOptions(NativeValidationDisabled: true));
        using VulkanTestWindow window = new(64, 64);
        using Device device = CreateDevice(backend);
        using Surface surface = backend.CreateSurface(new SurfaceDesc(NativeWindowType.Win32, window.Handle));
        using Swapchain swapchain = backend.CreateSwapchain(device, new SwapchainDesc(
            surface, 3, TextureUsages.ColorAttachment | TextureUsages.CopySource, Config(64, 64)));
        using CommandContext context = backend.CreateCommandContext(device, new CommandContextDesc(QueueType.Graphics, 0, 1));
        using Buffer readback = backend.CreateBuffer(device,
            new BufferDesc(64 * 64 * 4, BufferUsages.CopyDestination), MemoryType.Readback);
        Queue queue = backend.GetQueue(device, QueueType.Graphics);
        TextureSubresourceRange range = new(0, 1, 0, 1, TextureAspects.Color);

        for (int frame = 0; frame < 24; frame++)
        {
            SwapchainImage image = Acquire(backend, swapchain);
            Assert.Equal(SwapchainImageStatus.Acquired, image.Status);
            Assert.Equal(TextureLayout.Undefined, image.InitialLayout);
            using ColorAttachmentView attachment = backend.CreateColorAttachmentView(device,
                new ColorAttachmentViewDesc(image.Texture, range, Format.R8G8B8A8UNorm, TextureViewDimension.Texture2D));
            Vector4 color = frame % 2 == 0 ? new(1, 0, 0, 1) : new(0, 1, 0, 1);
            backend.Begin(context);
            backend.Barrier(context, new TextureBarrier(image.Texture, range,
                image.InitialSync, PipelineSync.RenderTarget, image.InitialAccess, ResourceAccess.RenderTarget,
                image.InitialLayout, TextureLayout.RenderTarget));
            backend.BeginRendering(context, new RenderingDesc(
                [new ColorAttachmentDesc(attachment, LoadType.Clear, StoreType.Store, color)], null, 64, 64));
            backend.EndRendering(context);
            backend.Barrier(context, new TextureBarrier(image.Texture, range,
                PipelineSync.RenderTarget, PipelineSync.Copy, ResourceAccess.RenderTarget, ResourceAccess.CopySource,
                TextureLayout.RenderTarget, TextureLayout.CopySource));
            backend.CopyTextureToBuffer(context, new BufferTextureCopy(readback, 0, 64 * 4, 64,
                image.Texture, 0, 0, TextureAspects.Color, 0, 0, 0, 64, 64, 1));
            backend.Barrier(context, new TextureBarrier(image.Texture, range,
                PipelineSync.Copy, PipelineSync.None, ResourceAccess.CopySource, ResourceAccess.NoAccess,
                TextureLayout.CopySource, TextureLayout.Present));
            using RecordedCommands commands = backend.End(context);
            QueueCompletion completion = backend.Submit(queue, new QueueSubmitDesc([], [], [commands], [image], []));
            Assert.Equal(SwapchainImageStatus.Submitted, image.Status);
            Assert.Contains(backend.Present(queue, image), new[] { PresentStatus.Success, PresentStatus.Suboptimal });
            Assert.Equal(SwapchainImageStatus.Presented, image.Status);
            Assert.Throws<InvalidOperationException>(() => backend.Present(queue, image));
            Assert.Equal(WaitStatus.Completed, backend.WaitCpu(completion, TimeSpan.FromSeconds(5)));
            using MappedBuffer pixels = backend.Map(readback, MapType.Read, BufferRange.Whole);
            pixels.Invalidate(pixels.Range);
            byte[] expected = new byte[64 * 64 * 4];
            for (int offset = 0; offset < expected.Length; offset += 4)
            {
                expected[offset] = frame % 2 == 0 ? (byte)255 : (byte)0;
                expected[offset + 1] = frame % 2 == 0 ? (byte)0 : (byte)255;
                expected[offset + 3] = 255;
            }
            ImageArtifacts.AssertEqual(new(64, 64, Format.R8G8B8A8UNorm, expected),
                new(64, 64, Format.R8G8B8A8UNorm, pixels.Bytes.ToArray()));
            Assert.Equal(DeviceStatus.Active, device.Status);
        }
    }

    [WindowsFact]
    public void Exhausted_image_capacity_times_out_and_recovers_after_present()
    {
        using IGraphicsBackend backend = new ValidationLayer(
            VulkanGraphicsBackend.Create(), new ValidationOptions(NativeValidationDisabled: true));
        using VulkanTestWindow window = new(64, 64);
        using Device device = CreateDevice(backend);
        using Surface surface = backend.CreateSurface(new SurfaceDesc(NativeWindowType.Win32, window.Handle));
        using Swapchain swapchain = backend.CreateSwapchain(device,
            new SwapchainDesc(surface, 3, TextureUsages.ColorAttachment,
                Config(64, 64) with { MaximumFrameLatency = 3 }));
        var images = new List<SwapchainImage>();
        bool capacityReached = false;
        for (int attempt = 0; attempt <= swapchain.Info.ImageCount; attempt++)
        {
            SwapchainAcquireStatus status = backend.Acquire(swapchain,
                new SwapchainAcquireOptions(Timeout.InfiniteTimeSpan), out SwapchainImage image);
            if (status == SwapchainAcquireStatus.Timeout)
            {
                capacityReached = true;
                break;
            }
            Assert.Equal(SwapchainAcquireStatus.Success, status);
            images.Add(image);
        }
        Assert.True(capacityReached);
        Assert.NotEmpty(images);
        using CommandContext context = backend.CreateCommandContext(device, new CommandContextDesc(QueueType.Graphics, 0, 1));
        Queue queue = backend.GetQueue(device, QueueType.Graphics);
        foreach (SwapchainImage image in images)
        {
            backend.Begin(context);
            backend.Barrier(context, PresentBarrier(image));
            using RecordedCommands commands = backend.End(context);
            QueueCompletion completion = backend.Submit(queue, new QueueSubmitDesc([], [], [commands], [image], []));
            Assert.Contains(backend.Present(queue, image), new[] { PresentStatus.Success, PresentStatus.Suboptimal });
            Assert.Equal(WaitStatus.Completed, backend.WaitCpu(completion, TimeSpan.FromSeconds(5)));
        }
        SwapchainImage next = Acquire(backend, swapchain);
        Assert.Equal(SwapchainImageStatus.Acquired, next.Status);
        Assert.Equal(DeviceStatus.Active, device.Status);
    }

    [WindowsFact]
    public void Acquired_images_keep_reconfiguration_busy_without_changing_the_generation()
    {
        using IGraphicsBackend backend = VulkanGraphicsBackend.Create();
        using VulkanTestWindow window = new(64, 64);
        using Device device = CreateDevice(backend);
        using Surface surface = backend.CreateSurface(new SurfaceDesc(NativeWindowType.Win32, window.Handle));
        using Swapchain swapchain = backend.CreateSwapchain(device,
            new SwapchainDesc(surface, 2, TextureUsages.ColorAttachment, Config(64, 64)));
        SwapchainImage image = Acquire(backend, swapchain);
        ulong generation = swapchain.Info.Generation;
        Assert.Equal(ReconfigureStatus.Busy, backend.Reconfigure(swapchain, Config(64, 64)));
        Assert.Equal(generation, swapchain.Info.Generation);
        Assert.Equal(SwapchainImageStatus.Acquired, image.Status);
    }

    [Theory]
    [InlineData(PresentType.Fifo, true)]
    [InlineData(PresentType.Mailbox, true)]
    [InlineData(PresentType.Immediate, false)]
    public void Unsupported_tearing_policy_preserves_a_usable_image_and_configuration(
        PresentType presentType, bool allowTearing)
    {
        using IGraphicsBackend backend = VulkanGraphicsBackend.Create();
        using VulkanTestWindow window = new(64, 64);
        using Device device = CreateDevice(backend);
        using Surface surface = backend.CreateSurface(new SurfaceDesc(NativeWindowType.Win32, window.Handle));
        using Swapchain swapchain = backend.CreateSwapchain(device,
            new SwapchainDesc(surface, 2, TextureUsages.ColorAttachment, Config(64, 64)));
        SwapchainInfo info = swapchain.Info;
        SwapchainImage image = Acquire(backend, swapchain);
        Assert.Equal(ReconfigureStatus.Unsupported, backend.Reconfigure(swapchain,
            info.Config with { PresentType = presentType, AllowTearing = allowTearing }));
        Assert.Same(info, swapchain.Info);
        Assert.Equal(SwapchainImageStatus.Acquired, image.Status);
        using CommandContext context = backend.CreateCommandContext(device, new CommandContextDesc(QueueType.Graphics, 0, 1));
        backend.Begin(context);
        backend.Barrier(context, PresentBarrier(image));
        using RecordedCommands commands = backend.End(context);
        Queue queue = backend.GetQueue(device, QueueType.Graphics);
        backend.Submit(queue, new QueueSubmitDesc([], [], [commands], [image], []));
        Assert.Contains(backend.Present(queue, image), new[] { PresentStatus.Success, PresentStatus.Suboptimal });
    }

    [WindowsFact]
    public void Window_resize_publishes_a_new_generation_with_presentable_images_of_the_new_extent()
    {
        using IGraphicsBackend backend = VulkanGraphicsBackend.Create();
        using VulkanTestWindow window = new(64, 64);
        using Device device = CreateDevice(backend);
        using Surface surface = backend.CreateSurface(new SurfaceDesc(NativeWindowType.Win32, window.Handle));
        using Swapchain swapchain = backend.CreateSwapchain(device,
            new SwapchainDesc(surface, 2, TextureUsages.ColorAttachment | TextureUsages.CopySource, Config(64, 64)));
        using CommandContext context = backend.CreateCommandContext(device, new CommandContextDesc(QueueType.Graphics, 0, 1));
        Queue queue = backend.GetQueue(device, QueueType.Graphics);
        SwapchainInfo previous = swapchain.Info;
        SwapchainImage first = Acquire(backend, swapchain);
        backend.Begin(context);
        backend.Barrier(context, PresentBarrier(first));
        using RecordedCommands commands = backend.End(context);
        backend.Submit(queue, new QueueSubmitDesc([], [], [commands], [first], []));
        Assert.Contains(backend.Present(queue, first), new[] { PresentStatus.Success, PresentStatus.Suboptimal });
        window.ResizeClient(96, 80);
        Reconfigure(backend, swapchain, Config(96, 80));
        Assert.Equal(previous.Generation + 1, swapchain.Info.Generation);
        Assert.Equal(64u, previous.Config.Width);
        Assert.Equal(96u, swapchain.Info.Config.Width);
        Assert.Equal(80u, swapchain.Info.Config.Height);
        Assert.True(swapchain.Info.ImageCount >= 2);
        Assert.Throws<InvalidOperationException>(() => _ = first.Texture);
        Assert.Throws<InvalidOperationException>(() => _ = first.Status);
        SwapchainImage resized = Acquire(backend, swapchain);
        Assert.Equal(96u, resized.Texture.Info.Width);
        Assert.Equal(80u, resized.Texture.Info.Height);
        TextureSubresourceRange range = new(0, 1, 0, 1, TextureAspects.Color);
        using ColorAttachmentView attachment = backend.CreateColorAttachmentView(device,
            new ColorAttachmentViewDesc(resized.Texture, range, Format.R8G8B8A8UNorm, TextureViewDimension.Texture2D));
        using Buffer readback = backend.CreateBuffer(device,
            new BufferDesc(96 * 80 * 4, BufferUsages.CopyDestination), MemoryType.Readback);
        backend.Begin(context);
        backend.Barrier(context, new TextureBarrier(resized.Texture, range,
            resized.InitialSync, PipelineSync.RenderTarget, resized.InitialAccess, ResourceAccess.RenderTarget,
            resized.InitialLayout, TextureLayout.RenderTarget));
        backend.BeginRendering(context, new RenderingDesc(
            [new ColorAttachmentDesc(attachment, LoadType.Clear, StoreType.Store, new Vector4(0, 0, 1, 1))], null, 96, 80));
        backend.EndRendering(context);
        backend.Barrier(context, new TextureBarrier(resized.Texture, range,
            PipelineSync.RenderTarget, PipelineSync.Copy, ResourceAccess.RenderTarget, ResourceAccess.CopySource,
            TextureLayout.RenderTarget, TextureLayout.CopySource));
        backend.CopyTextureToBuffer(context, new BufferTextureCopy(readback, 0, 96 * 4, 80,
            resized.Texture, 0, 0, TextureAspects.Color, 0, 0, 0, 96, 80, 1));
        backend.Barrier(context, new TextureBarrier(resized.Texture, range,
            PipelineSync.Copy, PipelineSync.None, ResourceAccess.CopySource, ResourceAccess.NoAccess,
            TextureLayout.CopySource, TextureLayout.Present));
        using RecordedCommands nextCommands = backend.End(context);
        QueueCompletion completion = backend.Submit(queue, new QueueSubmitDesc([], [], [nextCommands], [resized], []));
        Assert.Contains(backend.Present(queue, resized), new[] { PresentStatus.Success, PresentStatus.Suboptimal });
        Assert.Equal(WaitStatus.Completed, backend.WaitCpu(completion, TimeSpan.FromSeconds(5)));
        using MappedBuffer pixels = backend.Map(readback, MapType.Read, BufferRange.Whole);
        pixels.Invalidate(pixels.Range);
        byte[] expected = new byte[96 * 80 * 4];
        for (int offset = 0; offset < expected.Length; offset += 4)
            expected[offset + 2] = expected[offset + 3] = 255;
        ImageArtifacts.AssertEqual(new(96, 80, Format.R8G8B8A8UNorm, expected),
            new(96, 80, Format.R8G8B8A8UNorm, pixels.Bytes.ToArray()));
        Assert.Equal(DeviceStatus.Active, device.Status);
    }

    [WindowsFact]
    public void A_minimized_window_recovers_from_out_of_date_after_restore()
    {
        using IGraphicsBackend backend = VulkanGraphicsBackend.Create();
        using VulkanTestWindow window = new(64, 64);
        using Device device = CreateDevice(backend);
        using Surface surface = backend.CreateSurface(new SurfaceDesc(NativeWindowType.Win32, window.Handle));
        using Swapchain swapchain = backend.CreateSwapchain(device,
            new SwapchainDesc(surface, 2, TextureUsages.ColorAttachment, Config(64, 64)));
        ulong generation = swapchain.Info.Generation;
        window.Minimize();
        Assert.True(window.IsMinimized);
        Assert.Equal(SwapchainAcquireStatus.OutOfDate, backend.Acquire(swapchain,
            new SwapchainAcquireOptions(TimeSpan.Zero), out _));
        Assert.Equal(ReconfigureStatus.Busy, backend.Reconfigure(swapchain, Config(64, 64)));
        Assert.Equal(generation, swapchain.Info.Generation);
        window.Restore();
        Assert.False(window.IsMinimized);
        Reconfigure(backend, swapchain, Config(64, 64));
        Assert.Equal(generation + 1, swapchain.Info.Generation);
        SwapchainImage image = Acquire(backend, swapchain);
        using CommandContext context = backend.CreateCommandContext(device, new CommandContextDesc(QueueType.Graphics, 0, 1));
        backend.Begin(context);
        backend.Barrier(context, PresentBarrier(image));
        using RecordedCommands commands = backend.End(context);
        Queue queue = backend.GetQueue(device, QueueType.Graphics);
        backend.Submit(queue, new QueueSubmitDesc([], [], [commands], [image], []));
        Assert.Contains(backend.Present(queue, image), new[] { PresentStatus.Success, PresentStatus.Suboptimal });
        Assert.Equal(DeviceStatus.Active, device.Status);
    }

    [WindowsFact]
    public void Only_graphics_queue_zero_can_submit_and_present_an_acquired_image()
    {
        using IGraphicsBackend backend = VulkanGraphicsBackend.Create();
        using VulkanTestWindow window = new(64, 64);
        using Device device = backend.CreateDevice(new DeviceDesc(default,
            [new DeviceQueueDesc(QueueType.Graphics, Count: 2)], requiredFeatures: DeviceFeatures.Presentation));
        using Surface surface = backend.CreateSurface(new SurfaceDesc(NativeWindowType.Win32, window.Handle));
        using Swapchain swapchain = backend.CreateSwapchain(device,
            new SwapchainDesc(surface, 2, TextureUsages.ColorAttachment, Config(64, 64)));
        SwapchainImage image = Acquire(backend, swapchain);
        Queue other = backend.GetQueue(device, QueueType.Graphics, 1);
        Assert.Throws<ArgumentException>(() => backend.Present(other, image));
        Assert.Equal(SwapchainImageStatus.Acquired, image.Status);
        using CommandContext context = backend.CreateCommandContext(device, new CommandContextDesc(QueueType.Graphics, 1, 1));
        backend.Begin(context);
        backend.Barrier(context, PresentBarrier(image));
        using RecordedCommands commands = backend.End(context);
        Assert.Throws<ArgumentException>(() => backend.Submit(other,
            new QueueSubmitDesc([], [], [commands], [image], [])));
        Assert.Equal(SwapchainImageStatus.Acquired, image.Status);
    }

    [Theory]
    [InlineData(PresentedTeardown.Swapchain)]
    [InlineData(PresentedTeardown.Surface)]
    [InlineData(PresentedTeardown.Device)]
    [InlineData(PresentedTeardown.Backend)]
    public void Disposing_a_presented_swapchain_or_parent_invalidates_its_image(PresentedTeardown teardown)
    {
        using IGraphicsBackend backend = new ValidationLayer(
            VulkanGraphicsBackend.Create(), new ValidationOptions(NativeValidationDisabled: true));
        using VulkanTestWindow window = new(64, 64);
        using Device device = CreateDevice(backend);
        using Surface surface = backend.CreateSurface(new SurfaceDesc(NativeWindowType.Win32, window.Handle));
        using Swapchain swapchain = backend.CreateSwapchain(device,
            new SwapchainDesc(surface, 2, TextureUsages.ColorAttachment, Config(64, 64)));
        using CommandContext context = backend.CreateCommandContext(device, new CommandContextDesc(QueueType.Graphics, 0, 1));
        SwapchainImage image = Acquire(backend, swapchain);
        backend.Begin(context);
        backend.Barrier(context, PresentBarrier(image));
        using RecordedCommands commands = backend.End(context);
        Queue queue = backend.GetQueue(device, QueueType.Graphics);
        backend.Submit(queue, new QueueSubmitDesc([], [], [commands], [image], []));
        Assert.Contains(backend.Present(queue, image), new[] { PresentStatus.Success, PresentStatus.Suboptimal });
        switch (teardown)
        {
            case PresentedTeardown.Swapchain: swapchain.Dispose(); break;
            case PresentedTeardown.Surface: surface.Dispose(); break;
            case PresentedTeardown.Device: device.Dispose(); break;
            case PresentedTeardown.Backend: backend.Dispose(); break;
        }
        Assert.Equal(SwapchainImageStatus.Invalidated, image.Status);
        Assert.Throws<InvalidOperationException>(() => _ = image.Texture);
    }

    public enum PresentedTeardown { Swapchain, Surface, Device, Backend }

    private static Device CreateDevice(IGraphicsBackend backend) => backend.CreateDevice(new DeviceDesc(
        default, [new DeviceQueueDesc(QueueType.Graphics)], requiredFeatures: DeviceFeatures.Presentation));

    private static SwapchainConfig Config(uint width, uint height) => new(
        width, height, Format.R8G8B8A8UNorm, ColorSpace.Srgb, PresentType.Fifo, false, 2);

    private static TextureBarrier PresentBarrier(SwapchainImage image) => new(
        image.Texture, new TextureSubresourceRange(0, 1, 0, 1, TextureAspects.Color),
        image.InitialSync, PipelineSync.None, image.InitialAccess, ResourceAccess.NoAccess,
        image.InitialLayout, TextureLayout.Present);

    private static SwapchainImage Acquire(IGraphicsBackend backend, Swapchain swapchain)
    {
        long started = Stopwatch.GetTimestamp();
        while (true)
        {
            SwapchainAcquireStatus status = backend.Acquire(swapchain,
                new SwapchainAcquireOptions(TimeSpan.FromSeconds(1)), out SwapchainImage image);
            if (status == SwapchainAcquireStatus.Success) return image;
            Assert.Equal(SwapchainAcquireStatus.Timeout, status);
            Assert.True(Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(5), "Swapchain acquisition timed out.");
            Thread.Yield();
        }
    }

    private static void Reconfigure(IGraphicsBackend backend, Swapchain swapchain, SwapchainConfig config)
    {
        long started = Stopwatch.GetTimestamp();
        while (true)
        {
            ReconfigureStatus status = backend.Reconfigure(swapchain, config);
            if (status == ReconfigureStatus.Success) return;
            Assert.Equal(ReconfigureStatus.Busy, status);
            Assert.True(Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(5), "Swapchain reconfiguration timed out.");
            Thread.Yield();
        }
    }
}

internal sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
            Skip = "This WSI feature request is tested on Windows.";
    }
}
