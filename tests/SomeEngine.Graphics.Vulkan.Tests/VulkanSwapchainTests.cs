using System.ComponentModel;
using System.Numerics;
using System.Runtime.InteropServices;
using SomeEngine.Tests;
using Xunit;

namespace SomeEngine.Graphics.Vulkan.Tests;

[Collection("Vulkan shader compilation")]
public sealed class VulkanSwapchainTests
{
    [WindowsFact]
    [Trait("Category", "VulkanWSI")]
    public void DisposingSwapchainBeforeSubmissionCompletionPreservesCopiedPixels()
    {
        using var window = new Window();
        using IGraphicsBackend backend = VulkanGraphicsBackend.Create(
            new VulkanBackendOptions(EnableValidation: true));
        using Device device = backend.CreateDevice(new DeviceDesc(
            default,
            [new DeviceQueueDesc(QueueType.Graphics, Count: 2)],
            requiredFeatures: DeviceFeatures.Presentation | DeviceFeatures.ExternalTimelines));
        Queue graphics = backend.GetQueue(device, QueueType.Graphics);
        Queue releaseQueue = backend.GetQueue(device, QueueType.Graphics, 1);
        Assert.True(backend.TryGetCapability(device, out ExternalTimelines? timelines));
        Assert.NotNull(timelines);
        Assert.True(timelines.SupportsExport(ExternalHandleType.OpaqueWin32));
        using ExternalTimeline gate = backend.CreateExternalTimeline(device, 0);
        using Surface surface = backend.CreateSurface(
            new SurfaceDesc(NativeWindowType.Win32, window.Handle));
        using Swapchain swapchain = CreateSwapchain(backend, device, surface);
        Assert.Equal(SwapchainAcquireStatus.Success, backend.Acquire(
            swapchain, new SwapchainAcquireOptions(TimeSpan.FromSeconds(5)), out SwapchainImage image));
        Texture texture = image.Texture;
        Format format = swapchain.Info.Config.Format;
        int width = checked((int)swapchain.Info.Config.Width);
        int height = checked((int)swapchain.Info.Config.Height);
        uint rowPitch = checked(((uint)width * 4 + 255) & ~255u);
        using Buffer readback = backend.CreateBuffer(device,
            new BufferDesc(checked((ulong)rowPitch * (uint)height), BufferUsages.CopyDestination),
            MemoryType.Readback);
        using ColorAttachmentView attachment = backend.CreateColorAttachmentView(device,
            new ColorAttachmentViewDesc(texture, new(0, 1, 0, 1, TextureAspects.Color),
                format, TextureViewDimension.Texture2D));
        using CommandContext context = backend.CreateCommandContext(device,
            new CommandContextDesc(QueueType.Graphics, 0, 1));
        backend.Begin(context);
        TextureSubresourceRange range = new(0, 1, 0, 1, TextureAspects.Color);
        backend.Barrier(context, new TextureBarrier(texture, range,
            image.InitialSync, PipelineSync.RenderTarget, image.InitialAccess, ResourceAccess.RenderTarget,
            image.InitialLayout, TextureLayout.RenderTarget));
        ColorAttachmentDesc[] colors =
            [new(attachment, LoadType.Clear, StoreType.Store, new Vector4(0, 1, 0, 1))];
        backend.BeginRendering(context, new RenderingDesc(colors, null, (uint)width, (uint)height));
        backend.EndRendering(context);
        backend.Barrier(context, new TextureBarrier(texture, range,
            PipelineSync.RenderTarget, PipelineSync.Copy, ResourceAccess.RenderTarget, ResourceAccess.CopySource,
            TextureLayout.RenderTarget, TextureLayout.CopySource));
        backend.CopyTextureToBuffer(context, new BufferTextureCopy(
            readback, 0, rowPitch, (uint)height, texture, 0, 0, TextureAspects.Color,
            0, 0, 0, (uint)width, (uint)height, 1));
        using RecordedCommands commands = backend.End(context);
        QueueCompletion completion = backend.Submit(graphics,
            new QueueSubmitDesc([], [new TimelinePoint(gate, 1)], [commands], [image], []));
        Task? disposal = null;
        try
        {
            // No signal has been submitted for gate=1, so GPU completion is impossible here.
            Assert.False(backend.IsComplete(completion));
            disposal = Task.Run(swapchain.Dispose);
            disposal.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
            Assert.Equal(SwapchainImageStatus.Invalidated, image.Status);
            Assert.Throws<InvalidOperationException>(() => { _ = image.Texture; });
        }
        finally
        {
            // This always runs, including assertion failures and the disposal hang guard.
            QueueCompletion release = backend.Submit(releaseQueue,
                new QueueSubmitDesc([], [], [], [], [new TimelineSignal(gate, 1)]));
            try
            {
                Assert.Equal(WaitStatus.Completed, backend.WaitCpu(release, TimeSpan.FromSeconds(5)));
                Assert.Equal(WaitStatus.Completed, backend.WaitCpu(completion, TimeSpan.FromSeconds(5)));
            }
            finally
            {
                if (disposal is not null)
                    disposal.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
            }
        }
        backend.CollectCompleted(device);
        byte[] expected = new byte[checked(width * height * 4)];
        for (int pixel = 0; pixel < width * height; pixel++)
        {
            expected[pixel * 4 + 0] = 0;
            expected[pixel * 4 + 1] = 255;
            expected[pixel * 4 + 2] = 0;
            expected[pixel * 4 + 3] = 255;
        }
        ImageArtifacts.AssertEqual(
            new TestImage(width, height, Format.R8G8B8A8UNorm, expected),
            ReadImage(backend, readback, width, height, rowPitch, format));
    }

    [WindowsFact]
    [Trait("Category", "VulkanWSI")]
    public void DisposingSurfaceBeforeSubmissionCompletionPreservesCopiedPixels()
    {
        using var window = new Window();
        using IGraphicsBackend backend = VulkanGraphicsBackend.Create(
            new VulkanBackendOptions(EnableValidation: true));
        using Device device = backend.CreateDevice(new DeviceDesc(
            default,
            [new DeviceQueueDesc(QueueType.Graphics, Count: 2)],
            requiredFeatures: DeviceFeatures.Presentation | DeviceFeatures.ExternalTimelines));
        Queue graphics = backend.GetQueue(device, QueueType.Graphics);
        Queue releaseQueue = backend.GetQueue(device, QueueType.Graphics, 1);
        Assert.True(backend.TryGetCapability(device, out ExternalTimelines? timelines));
        Assert.NotNull(timelines);
        Assert.True(timelines.SupportsExport(ExternalHandleType.OpaqueWin32));
        using ExternalTimeline gate = backend.CreateExternalTimeline(device, 0);
        using Surface surface = backend.CreateSurface(
            new SurfaceDesc(NativeWindowType.Win32, window.Handle));
        using Swapchain swapchain = CreateSwapchain(backend, device, surface);
        Assert.Equal(SwapchainAcquireStatus.Success, backend.Acquire(
            swapchain, new SwapchainAcquireOptions(TimeSpan.FromSeconds(5)), out SwapchainImage image));
        Texture texture = image.Texture;
        Format format = swapchain.Info.Config.Format;
        int width = checked((int)swapchain.Info.Config.Width);
        int height = checked((int)swapchain.Info.Config.Height);
        uint rowPitch = checked(((uint)width * 4 + 255) & ~255u);
        using Buffer readback = backend.CreateBuffer(device,
            new BufferDesc(checked((ulong)rowPitch * (uint)height), BufferUsages.CopyDestination),
            MemoryType.Readback);
        using ColorAttachmentView attachment = backend.CreateColorAttachmentView(device,
            new ColorAttachmentViewDesc(texture, new(0, 1, 0, 1, TextureAspects.Color),
                format, TextureViewDimension.Texture2D));
        using CommandContext context = backend.CreateCommandContext(device,
            new CommandContextDesc(QueueType.Graphics, 0, 1));
        backend.Begin(context);
        TextureSubresourceRange range = new(0, 1, 0, 1, TextureAspects.Color);
        backend.Barrier(context, new TextureBarrier(texture, range,
            image.InitialSync, PipelineSync.RenderTarget, image.InitialAccess, ResourceAccess.RenderTarget,
            image.InitialLayout, TextureLayout.RenderTarget));
        ColorAttachmentDesc[] colors =
            [new(attachment, LoadType.Clear, StoreType.Store, new Vector4(1, 0, 1, 1))];
        backend.BeginRendering(context, new RenderingDesc(colors, null, (uint)width, (uint)height));
        backend.EndRendering(context);
        backend.Barrier(context, new TextureBarrier(texture, range,
            PipelineSync.RenderTarget, PipelineSync.Copy, ResourceAccess.RenderTarget, ResourceAccess.CopySource,
            TextureLayout.RenderTarget, TextureLayout.CopySource));
        backend.CopyTextureToBuffer(context, new BufferTextureCopy(
            readback, 0, rowPitch, (uint)height, texture, 0, 0, TextureAspects.Color,
            0, 0, 0, (uint)width, (uint)height, 1));
        using RecordedCommands commands = backend.End(context);
        QueueCompletion completion = backend.Submit(graphics,
            new QueueSubmitDesc([], [new TimelinePoint(gate, 1)], [commands], [image], []));
        Task? disposal = null;
        try
        {
            // No signal has been submitted for gate=1, so GPU completion is impossible here.
            Assert.False(backend.IsComplete(completion));
            disposal = Task.Run(surface.Dispose);
            disposal.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
            Assert.Equal(SwapchainImageStatus.Invalidated, image.Status);
            Assert.Throws<InvalidOperationException>(() => { _ = image.Texture; });
        }
        finally
        {
            // This always runs, including assertion failures and the disposal hang guard.
            QueueCompletion release = backend.Submit(releaseQueue,
                new QueueSubmitDesc([], [], [], [], [new TimelineSignal(gate, 1)]));
            try
            {
                Assert.Equal(WaitStatus.Completed, backend.WaitCpu(release, TimeSpan.FromSeconds(5)));
                Assert.Equal(WaitStatus.Completed, backend.WaitCpu(completion, TimeSpan.FromSeconds(5)));
            }
            finally
            {
                if (disposal is not null)
                    disposal.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
            }
        }
        backend.CollectCompleted(device);
        byte[] expected = new byte[checked(width * height * 4)];
        for (int pixel = 0; pixel < width * height; pixel++)
        {
            expected[pixel * 4 + 0] = 255;
            expected[pixel * 4 + 1] = 0;
            expected[pixel * 4 + 2] = 255;
            expected[pixel * 4 + 3] = 255;
        }
        ImageArtifacts.AssertEqual(
            new TestImage(width, height, Format.R8G8B8A8UNorm, expected),
            ReadImage(backend, readback, width, height, rowPitch, format));
    }

    [WindowsFact]
    [Trait("Category", "VulkanWSI")]
    public void RejectedSubmitCanBeRetriedWithTheSameCommandsAndImage()
    {
        using var window = new Window();
        using IGraphicsBackend backend = VulkanGraphicsBackend.Create(
            new VulkanBackendOptions(EnableValidation: true));
        using Device device = backend.CreateDevice(new DeviceDesc(
            default,
            [new DeviceQueueDesc(QueueType.Graphics, Count: 1)],
            requiredFeatures: DeviceFeatures.Presentation | DeviceFeatures.ExternalTimelines));
        Queue graphics = backend.GetQueue(device, QueueType.Graphics);
        Assert.True(backend.TryGetCapability(device, out ExternalTimelines? timelines));
        Assert.NotNull(timelines);
        Assert.True(timelines.SupportsExport(ExternalHandleType.OpaqueWin32));
        using ExternalTimeline liveWait = backend.CreateExternalTimeline(device, 0);
        using ExternalTimeline rejectedWait = backend.CreateExternalTimeline(device, 0);
        rejectedWait.Dispose();
        using Surface surface = backend.CreateSurface(
            new SurfaceDesc(NativeWindowType.Win32, window.Handle));
        using Swapchain swapchain = CreateSwapchain(backend, device, surface);
        Assert.Equal(SwapchainAcquireStatus.Success, backend.Acquire(
            swapchain, new SwapchainAcquireOptions(TimeSpan.FromSeconds(5)), out SwapchainImage image));
        Texture texture = image.Texture;
        Format format = swapchain.Info.Config.Format;
        int width = checked((int)swapchain.Info.Config.Width);
        int height = checked((int)swapchain.Info.Config.Height);
        uint rowPitch = checked(((uint)width * 4 + 255) & ~255u);
        using Buffer readback = backend.CreateBuffer(device,
            new BufferDesc(checked((ulong)rowPitch * (uint)height), BufferUsages.CopyDestination),
            MemoryType.Readback);
        using ColorAttachmentView attachment = backend.CreateColorAttachmentView(device,
            new ColorAttachmentViewDesc(texture, new(0, 1, 0, 1, TextureAspects.Color),
                format, TextureViewDimension.Texture2D));
        using CommandContext context = backend.CreateCommandContext(device,
            new CommandContextDesc(QueueType.Graphics, 0, 1));
        backend.Begin(context);
        TextureSubresourceRange range = new(0, 1, 0, 1, TextureAspects.Color);
        backend.Barrier(context, new TextureBarrier(texture, range,
            image.InitialSync, PipelineSync.RenderTarget, image.InitialAccess, ResourceAccess.RenderTarget,
            image.InitialLayout, TextureLayout.RenderTarget));
        ColorAttachmentDesc[] colors =
            [new(attachment, LoadType.Clear, StoreType.Store, new Vector4(0, 1, 0, 1))];
        backend.BeginRendering(context, new RenderingDesc(colors, null, (uint)width, (uint)height));
        backend.EndRendering(context);
        backend.Barrier(context, new TextureBarrier(texture, range,
            PipelineSync.RenderTarget, PipelineSync.Copy, ResourceAccess.RenderTarget, ResourceAccess.CopySource,
            TextureLayout.RenderTarget, TextureLayout.CopySource));
        backend.CopyTextureToBuffer(context, new BufferTextureCopy(
            readback, 0, rowPitch, (uint)height, texture, 0, 0, TextureAspects.Color,
            0, 0, 0, (uint)width, (uint)height, 1));
        using RecordedCommands commands = backend.End(context);

        QueueCompletion completion = default;
        bool unexpectedlyAccepted = false;
        Exception? rejection = Record.Exception(() =>
        {
            completion = backend.Submit(graphics, new QueueSubmitDesc(
                [], [new TimelinePoint(liveWait, 0), new TimelinePoint(rejectedWait, 0)],
                [commands], [image], []));
            unexpectedlyAccepted = true;
        });
        RecordedCommandsStatus commandsAfterRejection = commands.Status;
        SwapchainImageStatus imageAfterRejection = image.Status;
        if (!unexpectedlyAccepted)
        {
            completion = backend.Submit(graphics, new QueueSubmitDesc(
                [], [new TimelinePoint(liveWait, 0)], [commands], [image], []));
        }
        // Consume the acquisition before asserting rejection/rollback behavior.
        // If the invalid submit was unexpectedly accepted, drain that submission instead.
        WaitStatus completed = backend.WaitCpu(completion, TimeSpan.FromSeconds(5));
        backend.CollectCompleted(device);

        Assert.Equal(WaitStatus.Completed, completed);
        Assert.IsType<ObjectDisposedException>(rejection);
        Assert.Equal(RecordedCommandsStatus.Executable, commandsAfterRejection);
        Assert.Equal(SwapchainImageStatus.Acquired, imageAfterRejection);
        byte[] expected = new byte[checked(width * height * 4)];
        for (int pixel = 0; pixel < width * height; pixel++)
        {
            expected[pixel * 4 + 0] = 0;
            expected[pixel * 4 + 1] = 255;
            expected[pixel * 4 + 2] = 0;
            expected[pixel * 4 + 3] = 255;
        }
        ImageArtifacts.AssertEqual(
            new TestImage(width, height, Format.R8G8B8A8UNorm, expected),
            ReadImage(backend, readback, width, height, rowPitch, format));
    }

    [WindowsFact]
    [Trait("Category", "VulkanWSI")]
    public void AcquiringBeyondMaximumFrameLatencyReturnsTimeout()
    {
        using var window = new Window();
        using IGraphicsBackend backend = VulkanGraphicsBackend.Create(
            new VulkanBackendOptions(EnableValidation: true));
        using Device device = backend.CreateDevice(new DeviceDesc(
            default,
            [new DeviceQueueDesc(QueueType.Graphics, Count: 1)],
            requiredFeatures: DeviceFeatures.Presentation));
        Queue graphics = backend.GetQueue(device, QueueType.Graphics);
        using Surface surface = backend.CreateSurface(
            new SurfaceDesc(NativeWindowType.Win32, window.Handle));
        using Swapchain swapchain = CreateSwapchain(backend, device, surface);
        Assert.Equal(1u, swapchain.Info.Config.MaximumFrameLatency);
        Assert.Equal(SwapchainAcquireStatus.Success, backend.Acquire(
            swapchain, new SwapchainAcquireOptions(TimeSpan.FromSeconds(5)), out SwapchainImage first));

        // Run this test in a process with a watchdog: the old backend can hang in native Acquire.
        SwapchainAcquireStatus result = backend.Acquire(swapchain,
            new SwapchainAcquireOptions(Timeout.InfiniteTimeSpan), out SwapchainImage additional);
        SwapchainImageStatus heldStatus = first.Status;
        SwapchainImage[] acquired = result == SwapchainAcquireStatus.Success ? [first, additional] : [first];

        // Consume every successful acquisition before asserting the exhaustion result.
        // Cleanup does not rely on destruction of an unsubmitted acquire semaphore.
        using CommandContext context = backend.CreateCommandContext(device,
            new CommandContextDesc(QueueType.Graphics, 0, 1));
        backend.Begin(context);
        foreach (SwapchainImage current in acquired)
        {
            backend.Barrier(context, new TextureBarrier(current.Texture,
                new(0, 1, 0, 1, TextureAspects.Color), current.InitialSync, PipelineSync.All,
                current.InitialAccess, ResourceAccess.NoAccess,
                current.InitialLayout, TextureLayout.Present));
        }
        using RecordedCommands commands = backend.End(context);
        QueueCompletion completion = backend.Submit(graphics,
            new QueueSubmitDesc([], [], [commands], acquired, []));
        Assert.Equal(WaitStatus.Completed, backend.WaitCpu(completion, TimeSpan.FromSeconds(5)));
        backend.CollectCompleted(device);

        Assert.Equal(SwapchainImageStatus.Acquired, heldStatus);
        Assert.Equal(SwapchainAcquireStatus.Timeout, result);
        Assert.Throws<InvalidOperationException>(() => { _ = additional.Texture; });
    }

    [WindowsFact]
    [Trait("Category", "VulkanWSI")]
    public void DiscardingRecordedImageUseLeavesImageAvailableForSubmission()
    {
        using var window = new Window();
        using IGraphicsBackend backend = VulkanGraphicsBackend.Create(
            new VulkanBackendOptions(EnableValidation: true));
        using Device device = backend.CreateDevice(new DeviceDesc(
            default,
            [new DeviceQueueDesc(QueueType.Graphics, Count: 1)],
            requiredFeatures: DeviceFeatures.Presentation));
        Queue graphics = backend.GetQueue(device, QueueType.Graphics);
        using Surface surface = backend.CreateSurface(
            new SurfaceDesc(NativeWindowType.Win32, window.Handle));
        using Swapchain swapchain = CreateSwapchain(backend, device, surface);
        Assert.Equal(SwapchainAcquireStatus.Success, backend.Acquire(
            swapchain, new SwapchainAcquireOptions(TimeSpan.FromSeconds(5)), out SwapchainImage image));
        Texture texture = image.Texture;
        Format format = swapchain.Info.Config.Format;
        int width = checked((int)swapchain.Info.Config.Width);
        int height = checked((int)swapchain.Info.Config.Height);
        uint rowPitch = checked(((uint)width * 4 + 255) & ~255u);
        using Buffer readback = backend.CreateBuffer(device,
            new BufferDesc(checked((ulong)rowPitch * (uint)height), BufferUsages.CopyDestination),
            MemoryType.Readback);
        using ColorAttachmentView attachment = backend.CreateColorAttachmentView(device,
            new ColorAttachmentViewDesc(texture, new(0, 1, 0, 1, TextureAspects.Color),
                format, TextureViewDimension.Texture2D));
        using CommandContext context = backend.CreateCommandContext(device,
            new CommandContextDesc(QueueType.Graphics, 0, 1));
        backend.Begin(context);
        backend.Barrier(context, new TextureBarrier(texture,
            new(0, 1, 0, 1, TextureAspects.Color), image.InitialSync, PipelineSync.Copy,
            image.InitialAccess, ResourceAccess.CopySource, image.InitialLayout, TextureLayout.CopySource));
        RecordedCommands discarded = backend.End(context);
        discarded.Dispose();
        Assert.Equal(SwapchainImageStatus.Acquired, image.Status);

        backend.Begin(context);
        TextureSubresourceRange range = new(0, 1, 0, 1, TextureAspects.Color);
        backend.Barrier(context, new TextureBarrier(texture, range,
            image.InitialSync, PipelineSync.RenderTarget, image.InitialAccess, ResourceAccess.RenderTarget,
            image.InitialLayout, TextureLayout.RenderTarget));
        ColorAttachmentDesc[] colors =
            [new(attachment, LoadType.Clear, StoreType.Store, new Vector4(1, 1, 1, 1))];
        backend.BeginRendering(context, new RenderingDesc(colors, null, (uint)width, (uint)height));
        backend.EndRendering(context);
        backend.Barrier(context, new TextureBarrier(texture, range,
            PipelineSync.RenderTarget, PipelineSync.Copy, ResourceAccess.RenderTarget, ResourceAccess.CopySource,
            TextureLayout.RenderTarget, TextureLayout.CopySource));
        backend.CopyTextureToBuffer(context, new BufferTextureCopy(
            readback, 0, rowPitch, (uint)height, texture, 0, 0, TextureAspects.Color,
            0, 0, 0, (uint)width, (uint)height, 1));
        using RecordedCommands commands = backend.End(context);
        QueueCompletion completion = backend.Submit(graphics,
            new QueueSubmitDesc([], [], [commands], [image], []));
        Assert.Equal(WaitStatus.Completed, backend.WaitCpu(completion, TimeSpan.FromSeconds(5)));
        backend.CollectCompleted(device);
        byte[] expected = new byte[checked(width * height * 4)];
        for (int pixel = 0; pixel < width * height; pixel++)
        {
            expected[pixel * 4 + 0] = 255;
            expected[pixel * 4 + 1] = 255;
            expected[pixel * 4 + 2] = 255;
            expected[pixel * 4 + 3] = 255;
        }
        ImageArtifacts.AssertEqual(
            new TestImage(width, height, Format.R8G8B8A8UNorm, expected),
            ReadImage(backend, readback, width, height, rowPitch, format));
    }

    [WindowsFact]
    [Trait("Category", "VulkanWSI")]
    public void DisposingUnsubmittedAcquisitionPreservesDeviceUsability()
    {
        using var window = new Window();
        using IGraphicsBackend backend = VulkanGraphicsBackend.Create(
            new VulkanBackendOptions(EnableValidation: true));
        using Device device = backend.CreateDevice(new DeviceDesc(
            default, [new DeviceQueueDesc(QueueType.Graphics)],
            requiredFeatures: DeviceFeatures.Presentation));
        using Surface surface = backend.CreateSurface(
            new SurfaceDesc(NativeWindowType.Win32, window.Handle));
        using Swapchain swapchain = CreateSwapchain(backend, device, surface);
        Assert.Equal(SwapchainAcquireStatus.Success, backend.Acquire(
            swapchain, new SwapchainAcquireOptions(TimeSpan.FromSeconds(5)), out SwapchainImage image));

        // Do not submit this image: cleanup must independently retire native acquisition.
        swapchain.Dispose();
        surface.Dispose();
        Assert.Equal(SwapchainImageStatus.Invalidated, image.Status);
        Assert.Throws<InvalidOperationException>(() => { _ = image.Texture; });
        backend.CollectCompleted(device);

        TestImage actual = RenderOffscreenGreenImage(backend, device);
        byte[] expected = new byte[16 * 16 * 4];
        for (int pixel = 0; pixel < 16 * 16; pixel++)
        {
            expected[pixel * 4 + 1] = 255;
            expected[pixel * 4 + 3] = 255;
        }
        ImageArtifacts.AssertEqual(new TestImage(16, 16, Format.R8G8B8A8UNorm, expected), actual);
    }

    [WindowsFact]
    [Trait("Category", "VulkanWSI")]
    public void DiscardingLastRecordedImageUseAfterDisposalPreservesDeviceUsability()
    {
        using var window = new Window();
        using IGraphicsBackend backend = VulkanGraphicsBackend.Create(
            new VulkanBackendOptions(EnableValidation: true));
        using Device device = backend.CreateDevice(new DeviceDesc(
            default, [new DeviceQueueDesc(QueueType.Graphics)],
            requiredFeatures: DeviceFeatures.Presentation));
        using Surface surface = backend.CreateSurface(
            new SurfaceDesc(NativeWindowType.Win32, window.Handle));
        using Swapchain swapchain = CreateSwapchain(backend, device, surface);
        Assert.Equal(SwapchainAcquireStatus.Success, backend.Acquire(
            swapchain, new SwapchainAcquireOptions(TimeSpan.FromSeconds(5)), out SwapchainImage image));
        using CommandContext context = backend.CreateCommandContext(device,
            new CommandContextDesc(QueueType.Graphics, 0, 1));
        backend.Begin(context);
        backend.Barrier(context, new TextureBarrier(image.Texture,
            new(0, 1, 0, 1, TextureAspects.Color), image.InitialSync, PipelineSync.Copy,
            image.InitialAccess, ResourceAccess.CopySource, image.InitialLayout, TextureLayout.CopySource));
        using RecordedCommands commands = backend.End(context);

        // The executable payload retains its native captures after logical context disposal.
        context.Dispose();
        swapchain.Dispose();
        surface.Dispose();
        Assert.Equal(SwapchainImageStatus.Invalidated, image.Status);
        Assert.Equal(RecordedCommandsStatus.Executable, commands.Status);
        commands.Dispose();
        Assert.Equal(RecordedCommandsStatus.Disposed, commands.Status);
        backend.CollectCompleted(device);

        TestImage actual = RenderOffscreenGreenImage(backend, device);
        byte[] expected = new byte[16 * 16 * 4];
        for (int pixel = 0; pixel < 16 * 16; pixel++)
        {
            expected[pixel * 4 + 1] = 255;
            expected[pixel * 4 + 3] = 255;
        }
        ImageArtifacts.AssertEqual(new TestImage(16, 16, Format.R8G8B8A8UNorm, expected), actual);
    }

    [WindowsFact]
    [Trait("Category", "VulkanWSI")]
    public void ConcurrentResourceDeviceAndRecordedCommandsDisposalInvalidatesUnsubmittedAcquisition()
    {
        using var window = new Window();
        using IGraphicsBackend backend = VulkanGraphicsBackend.Create(
            new VulkanBackendOptions(EnableValidation: true));
        using Device device = backend.CreateDevice(new DeviceDesc(
            default, [new DeviceQueueDesc(QueueType.Graphics)],
            requiredFeatures: DeviceFeatures.Presentation));
        using Surface surface = backend.CreateSurface(
            new SurfaceDesc(NativeWindowType.Win32, window.Handle));
        using Swapchain swapchain = CreateSwapchain(backend, device, surface);
        Assert.Equal(SwapchainAcquireStatus.Success, backend.Acquire(
            swapchain, new SwapchainAcquireOptions(TimeSpan.FromSeconds(5)), out SwapchainImage image));
        using Buffer capturedBuffer = backend.CreateBuffer(device,
            new BufferDesc(256, BufferUsages.CopySource), MemoryType.DeviceLocal);
        using CommandContext context = backend.CreateCommandContext(device,
            new CommandContextDesc(QueueType.Graphics, 0, 1));
        backend.Begin(context);
        backend.Barrier(context, new TextureBarrier(image.Texture,
            new(0, 1, 0, 1, TextureAspects.Color), image.InitialSync, PipelineSync.Copy,
            image.InitialAccess, ResourceAccess.CopySource, image.InitialLayout, TextureLayout.CopySource));
        backend.Barrier(context, new BufferBarrier(capturedBuffer,
            PipelineSync.None, PipelineSync.Copy, ResourceAccess.NoAccess, ResourceAccess.CopySource));
        using RecordedCommands commands = backend.End(context);
        context.Dispose();
        swapchain.Dispose();
        surface.Dispose();
        Assert.Equal(RecordedCommandsStatus.Executable, commands.Status);

        // This is a public concurrent-disposal smoke test, not a forced internal interleaving.
        // Run under the test-process watchdog; the timeout is only a hang guard.
        using var ready = new CountdownEvent(3);
        using var start = new ManualResetEventSlim();
        Task discard = Task.Run(() =>
        {
            ready.Signal();
            start.Wait();
            commands.Dispose();
        });
        Task releaseResource = Task.Run(() =>
        {
            ready.Signal();
            start.Wait();
            capturedBuffer.Dispose();
        });
        Task dispose = Task.Run(() =>
        {
            ready.Signal();
            start.Wait();
            device.Dispose();
        });
        try
        {
            Assert.True(ready.Wait(TimeSpan.FromSeconds(5)));
        }
        finally
        {
            start.Set();
            Task.WhenAll(discard, releaseResource, dispose).WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
        }
        Assert.Equal(RecordedCommandsStatus.Disposed, commands.Status);
        Assert.Equal(SwapchainImageStatus.Invalidated, image.Status);
        Assert.Equal(DeviceStatus.Disposed, device.Status);
        Assert.Throws<ObjectDisposedException>(() => backend.GetQueue(device, QueueType.Graphics));
        commands.Dispose();
        capturedBuffer.Dispose();
        context.Dispose();
        swapchain.Dispose();
        surface.Dispose();
        device.Dispose();
    }

    private static TestImage RenderOffscreenGreenImage(IGraphicsBackend backend, Device device)
    {
        const uint width = 16;
        const uint height = 16;
        const uint rowPitch = 256;
        using Texture texture = backend.CreateTexture(device,
            new TextureDesc(TextureDimension.Texture2D, width, height, 1, 1, 1, 1,
                Format.R8G8B8A8UNorm, TextureUsages.ColorAttachment | TextureUsages.CopySource));
        using Buffer readback = backend.CreateBuffer(device,
            new BufferDesc(rowPitch * height, BufferUsages.CopyDestination), MemoryType.Readback);
        using ColorAttachmentView attachment = backend.CreateColorAttachmentView(device,
            new ColorAttachmentViewDesc(texture, new(0, 1, 0, 1, TextureAspects.Color),
                Format.R8G8B8A8UNorm, TextureViewDimension.Texture2D));
        using CommandContext context = backend.CreateCommandContext(device,
            new CommandContextDesc(QueueType.Graphics, 0, 1));
        backend.Begin(context);
        TextureSubresourceRange range = new(0, 1, 0, 1, TextureAspects.Color);
        backend.Barrier(context, new TextureBarrier(texture, range,
            PipelineSync.None, PipelineSync.RenderTarget, ResourceAccess.NoAccess,
            ResourceAccess.RenderTarget, TextureLayout.Undefined, TextureLayout.RenderTarget));
        ColorAttachmentDesc[] colors =
            [new(attachment, LoadType.Clear, StoreType.Store, new Vector4(0, 1, 0, 1))];
        backend.BeginRendering(context, new RenderingDesc(colors, null, width, height));
        backend.EndRendering(context);
        backend.Barrier(context, new TextureBarrier(texture, range,
            PipelineSync.RenderTarget, PipelineSync.Copy, ResourceAccess.RenderTarget,
            ResourceAccess.CopySource, TextureLayout.RenderTarget, TextureLayout.CopySource));
        backend.CopyTextureToBuffer(context, new BufferTextureCopy(
            readback, 0, rowPitch, height, texture, 0, 0, TextureAspects.Color,
            0, 0, 0, width, height, 1));
        using RecordedCommands commands = backend.End(context);
        Queue graphics = backend.GetQueue(device, QueueType.Graphics);
        QueueCompletion completion = backend.Submit(graphics,
            new QueueSubmitDesc([], [], [commands], [], []));
        if (backend.WaitCpu(completion, TimeSpan.FromSeconds(5)) != WaitStatus.Completed)
            throw new TimeoutException("The independent offscreen image did not complete.");
        backend.CollectCompleted(device);
        return ReadImage(backend, readback, (int)width, (int)height, rowPitch, Format.R8G8B8A8UNorm);
    }

    private static Swapchain CreateSwapchain(IGraphicsBackend backend, Device device, Surface surface)
    {
        Format[] formats =
        [
            Format.R8G8B8A8UNorm,
            Format.B8G8R8A8UNorm,
            Format.R8G8B8A8UNormSrgb,
            Format.B8G8R8A8UNormSrgb,
        ];
        foreach (Format format in formats)
        {
            try
            {
                return backend.CreateSwapchain(device, new SwapchainDesc(surface, 2,
                    TextureUsages.ColorAttachment | TextureUsages.CopySource,
                    new SwapchainConfig(64, 64, format, ColorSpace.Srgb, PresentType.Fifo,
                        AllowTearing: false, MaximumFrameLatency: 1)));
            }
            catch (NotSupportedException)
            {
            }
        }
        throw new NotSupportedException("The test requires a copyable RGBA8 or BGRA8 FIFO swapchain.");
    }

    private static TestImage ReadImage(IGraphicsBackend backend, Buffer readback,
        int width, int height, uint rowPitch, Format format)
    {
        using MappedBuffer mapped = backend.Map(readback, MapType.Read, BufferRange.Whole);
        mapped.Invalidate(mapped.Range);
        byte[] bytes = new byte[checked(width * height * 4)];
        for (int row = 0; row < height; row++)
        {
            mapped.Bytes.Slice(checked(row * (int)rowPitch), checked(width * 4))
                .CopyTo(bytes.AsSpan(checked(row * width * 4), checked(width * 4)));
        }
        if (format is Format.B8G8R8A8UNorm or Format.B8G8R8A8UNormSrgb)
        {
            for (int pixel = 0; pixel < width * height; pixel++)
            {
                int offset = pixel * 4;
                (bytes[offset], bytes[offset + 2]) = (bytes[offset + 2], bytes[offset]);
            }
        }
        return new TestImage(width, height, Format.R8G8B8A8UNorm, bytes);
    }

    private sealed class Window : IDisposable
    {
        private const uint WsOverlappedWindow = 0x00CF0000;
        private nint _handle;

        internal Window(int width = 160, int height = 120)
        {
            _handle = CreateWindowExW(
                0,
                "STATIC",
                "SomeEngine Vulkan WSI Test",
                WsOverlappedWindow,
                0,
                0,
                width,
                height,
                0,
                0,
                0,
                0);
            if (_handle == 0)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "A hidden test window could not be created.");
        }

        internal nint Handle => _handle != 0
            ? _handle
            : throw new ObjectDisposedException(nameof(Window));

        public void Dispose()
        {
            nint handle = Interlocked.Exchange(ref _handle, 0);
            if (handle != 0)
                _ = DestroyWindow(handle);
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern nint CreateWindowExW(
            uint extendedStyle,
            string className,
            string windowName,
            uint style,
            int x,
            int y,
            int width,
            int height,
            nint parent,
            nint menu,
            nint instance,
            nint parameter);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DestroyWindow(nint window);
    }
}
