using SomeEngine.Graphics.Direct3D12;
using Xunit;

namespace SomeEngine.Graphics.Direct3D12.Tests;

public sealed class WarpDeviceLossTests
{
    [Fact]
    public void Native_removal_is_one_terminal_device_state_and_invalidates_all_live_work()
    {
        using D3D12TestWindow window = new();
        using D3D12Backend backend = new();
        Device device = D3D12TestSupport.CreateWarpDevice(backend);
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
        Assert.Equal(
            SwapchainAcquireStatus.Success,
            backend.Acquire(
                swapchain,
                new SwapchainAcquireOptions(TimeSpan.FromSeconds(2)),
                out SwapchainImage image));

        Queue queue = backend.GetQueue(device, QueueType.Graphics);
        using CommandContext submittedContext = backend.CreateCommandContext(
            device,
            new CommandContextDesc(QueueType.Graphics, 0, 0, 1));
        backend.Begin(submittedContext, default);
        using RecordedCommands submitted = backend.End(submittedContext);
        QueueCompletion submittedCompletion = backend.Submit(
            queue,
            new QueueSubmitDesc([], [], [submitted], [], []));
        Assert.Equal(RecordedCommandsStatus.Submitted, submitted.Status);

        using CommandContext executableContext = backend.CreateCommandContext(
            device,
            new CommandContextDesc(QueueType.Graphics, 0, 0, 1));
        backend.Begin(executableContext, default);
        using RecordedCommands executable = backend.End(executableContext);
        Assert.Equal(RecordedCommandsStatus.Executable, executable.Status);

        using CommandContext recordingContext = backend.CreateCommandContext(
            device,
            new CommandContextDesc(QueueType.Graphics, 0, 0, 1));
        backend.Begin(recordingContext, default);
        Assert.True(backend.TryGetCapability(device, out D3D12Diagnostics? diagnostics));
        Assert.NotNull(diagnostics);
        Assert.Null(diagnostics.DeviceLoss);

        GraphicsException loss = backend.RemoveDeviceForTesting(device);

        Assert.Equal(GraphicsError.DeviceLost, loss.Error);
        Assert.True(loss.NativeCode < 0);
        Assert.Contains("GetDeviceRemovedReason", loss.Diagnostic);
        Assert.Equal(DeviceStatus.Lost, device.Status);
        Assert.Same(loss, device.Loss);
        Assert.Same(loss, diagnostics.DeviceLoss);
        Assert.Equal(RecordedCommandsStatus.DeviceLost, submitted.Status);
        Assert.Equal(RecordedCommandsStatus.DeviceLost, executable.Status);
        Assert.Equal(SwapchainImageStatus.DeviceLost, image.Status);
        Assert.Throws<InvalidOperationException>(() => _ = image.Texture);

        Assert.Same(loss, Assert.Throws<GraphicsException>(() => backend.Submit(
            queue,
            new QueueSubmitDesc([], [], [], [], []))));
        Assert.Same(loss, Assert.Throws<GraphicsException>(() => backend.End(recordingContext)));
        Assert.Same(loss, Assert.Throws<GraphicsException>(() => backend.GetQueue(device, QueueType.Copy)));
        Assert.Same(loss, Assert.Throws<GraphicsException>(() => backend.CollectCompleted(device)));
        Assert.Same(loss, Assert.Throws<GraphicsException>(() => backend.WaitCpu(
            submittedCompletion,
            TimeSpan.Zero)));
        Assert.Same(loss, Assert.Throws<GraphicsException>(() => backend.Acquire(
            swapchain,
            new SwapchainAcquireOptions(TimeSpan.Zero),
            out _)));

        device.Dispose();
        device.Dispose();
        Assert.Equal(DeviceStatus.Disposed, device.Status);
        Assert.Null(diagnostics.DeviceLoss);
    }
}
