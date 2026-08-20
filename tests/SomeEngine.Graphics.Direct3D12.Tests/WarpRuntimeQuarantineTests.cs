using Xunit;

namespace SomeEngine.Graphics.Direct3D12.Tests;

/// <summary>
/// Process-destructive tests. The project default filter excludes this trait; certification runs
/// it with a dedicated dotnet-test process.
/// </summary>
public sealed class WarpRuntimeQuarantineTests
{
    [Fact]
    [Trait("Isolation", "ProcessDestructive")]
    public void Ordinary_final_wait_failure_retains_all_payloads_and_quarantines_backend()
    {
        using D3D12TestWindow window = new();
        var backend = new D3D12Backend();
        Device device = D3D12TestSupport.CreateWarpDevice(backend);
        Assert.True(backend.TryGetCapability(device, out D3D12Diagnostics? diagnostics));
        Assert.NotNull(diagnostics);
        Queue queue = backend.GetQueue(device, QueueType.Graphics, 0);
        using Surface surface = backend.CreateSurface(new SurfaceDesc(
            NativeWindowType.Win32,
            window.Handle));
        SwapchainConfig config = new(
            32, 32, Format.R8G8B8A8UNorm, ColorSpace.Srgb,
            PresentType.Mailbox, false, 2);
        Swapchain swapchain = backend.CreateSwapchain(
            device,
            new SwapchainDesc(surface, 2, TextureUsages.ColorAttachment, config));
        SubmitAndPresent(backend, device, swapchain, queue);
        swapchain.Dispose();
        Buffer buffer = backend.CreateBuffer(
            device,
            new BufferDesc(
                64 * 1024,
                BufferUsages.CopySource | BufferUsages.CopyDestination));
        ResidencyResource resource = backend.GetResidencyResource(buffer);
        _ = backend.EnqueueMakeResident(queue, [resource]);
        D3D12PrivateState.SetNextCompletion(queue, ulong.MaxValue);
        Assert.Throws<GraphicsException>(() =>
            backend.EnqueueMakeResident(queue, [resource]));
        buffer.Dispose();
        using (D3D12PrivateState.ReplaceFenceWithSetEventFailure(queue))
            device.Dispose();

        GraphicsException failure = Assert.IsType<GraphicsException>(diagnostics.TeardownFailure);
        Assert.Equal(GraphicsError.NativeFailure, failure.Error);
        Assert.Contains("SetEventOnCompletion", failure.Message);
        Assert.Null(diagnostics.DeviceLoss);

        D3D12Backend? previousHead = D3D12PrivateState.RuntimeQuarantineHead();
        backend.Dispose();
        Assert.True(D3D12PrivateState.IsRuntimeQuarantined(backend));
        Assert.Same(backend, D3D12PrivateState.RuntimeQuarantineHead());
        Assert.Same(previousHead, D3D12PrivateState.RuntimeQuarantineNext(backend));
        backend.Dispose();
        Assert.Same(failure, diagnostics.TeardownFailure);
        QueueRetirementSnapshot retained = D3D12PrivateState.QueueRetirements(queue);
        Assert.Equal(1, retained.PendingSubmissionCount);
        Assert.Equal(1, retained.PendingPresentationCount);
        Assert.Equal(1, retained.PendingCapabilityCount);
        Assert.True(retained.PresentationNativeReferenceCount > 0);
        Assert.True(retained.CapabilityNativeReferenceCount > 0);
        Assert.True(retained.HasNativeQueue);
        Assert.True(retained.HasFence);
    }

    private static void SubmitAndPresent(
        IGraphicsBackend backend,
        Device device,
        Swapchain swapchain,
        Queue queue)
    {
        Assert.Equal(
            SwapchainAcquireStatus.Success,
            backend.Acquire(
                swapchain,
                new SwapchainAcquireOptions(TimeSpan.FromSeconds(2)),
                out SwapchainImage image));
        using CommandContext context = backend.CreateCommandContext(
            device,
            new CommandContextDesc(QueueType.Graphics, 0, 1));
        backend.Begin(context);
        backend.Barrier(context, new TextureBarrier(
            image.Texture,
            new TextureSubresourceRange(0, 1, 0, 1, TextureAspects.Color),
            image.InitialSync,
            PipelineSync.None,
            image.InitialAccess,
            ResourceAccess.NoAccess,
            image.InitialLayout,
            TextureLayout.Present));
        using RecordedCommands commands = backend.End(context);
        _ = backend.Submit(
            queue,
            new QueueSubmitDesc([], [], [commands], [image], []));
        _ = backend.Present(queue, image);
    }
}
