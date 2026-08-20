using SomeEngine.Graphics.Direct3D12;
using SomeEngine.Graphics.Validation;
using System.Reflection;
using Xunit;

namespace SomeEngine.Graphics.Direct3D12.Tests;

[Trait("Isolation", "PresentationProcess")]
public sealed class WarpPresentationTests
{
    [Fact]
    public unsafe void Hdr10_metadata_lowering_preserves_exact_transport_values()
    {
        Hdr10Metadata source = CreateHdr10Metadata();

        Silk.NET.DXGI.HdrMetadataHdr10 native =
            D3D12Backend.ToNativeHdr10Metadata(source);

        Assert.Equal(source.RedPrimaryX, native.RedPrimary[0]);
        Assert.Equal(source.RedPrimaryY, native.RedPrimary[1]);
        Assert.Equal(source.GreenPrimaryX, native.GreenPrimary[0]);
        Assert.Equal(source.GreenPrimaryY, native.GreenPrimary[1]);
        Assert.Equal(source.BluePrimaryX, native.BluePrimary[0]);
        Assert.Equal(source.BluePrimaryY, native.BluePrimary[1]);
        Assert.Equal(source.WhitePointX, native.WhitePoint[0]);
        Assert.Equal(source.WhitePointY, native.WhitePoint[1]);
        Assert.Equal(source.MaximumMasteringLuminance, native.MaxMasteringLuminance);
        Assert.Equal(source.MinimumMasteringLuminance, native.MinMasteringLuminance);
        Assert.Equal(source.MaximumContentLightLevel, native.MaxContentLightLevel);
        Assert.Equal(
            source.MaximumFrameAverageLightLevel,
            native.MaxFrameAverageLightLevel);
    }

    [Fact]
    public void Hdr10_metadata_is_rejected_for_non_hdr10_color_space_before_native_creation()
    {
        using D3D12TestWindow window = new();
        using var backend = new D3D12Backend();
        using Device device = D3D12TestSupport.CreateWarpDevice(backend);
        using Surface surface = backend.CreateSurface(new SurfaceDesc(
            NativeWindowType.Win32,
            window.Handle));
        SwapchainConfig config = new(
            32,
            32,
            Format.R8G8B8A8UNorm,
            ColorSpace.Srgb,
            PresentType.Mailbox,
            AllowTearing: false,
            MaximumFrameLatency: 2,
            Hdr10Metadata: CreateHdr10Metadata());

        Assert.Throws<ArgumentException>(() => backend.CreateSwapchain(
            device,
            new SwapchainDesc(surface, 2, TextureUsages.ColorAttachment, config)));
    }

    [Fact]
    public void Invalid_hdr10_transport_values_are_rejected_before_native_creation()
    {
        using D3D12TestWindow window = new();
        using var backend = new D3D12Backend();
        using Device device = D3D12TestSupport.CreateWarpDevice(backend);
        using Surface surface = backend.CreateSurface(new SurfaceDesc(
            NativeWindowType.Win32,
            window.Handle));
        Hdr10Metadata invalid = CreateHdr10Metadata() with { RedPrimaryX = 50_001 };
        SwapchainConfig config = new(
            32,
            32,
            Format.R10G10B10A2UNorm,
            ColorSpace.Hdr10,
            PresentType.Mailbox,
            AllowTearing: false,
            MaximumFrameLatency: 2,
            Hdr10Metadata: invalid);

        Assert.Throws<ArgumentOutOfRangeException>(() => backend.CreateSwapchain(
            device,
            new SwapchainDesc(surface, 2, TextureUsages.ColorAttachment, config)));
    }

    [Fact]
    public void Acquire_submit_present_and_reconfigure_enforce_sequence_and_commit_boundaries()
    {
        using IGraphicsBackend backend = new D3D12Backend();
        ExercisePresentationLifecycle(backend);
    }

    [Fact]
    public void Native_validation_accepts_the_complete_presentation_lifecycle()
    {
        using IGraphicsBackend backend =
            new ValidationLayer(new D3D12Backend());
        ExercisePresentationLifecycle(backend);
    }

    [Fact]
    public void Dxgi_presentation_telemetry_tracks_configuration_latency_and_completion()
    {
        using D3D12TestWindow window = new();
        using var backend = new D3D12Backend();
        using Device device = D3D12TestSupport.CreateWarpDevice(backend);
        using Surface surface = backend.CreateSurface(new SurfaceDesc(
            NativeWindowType.Win32,
            window.Handle));
        SwapchainConfig config = new(
            48,
            40,
            Format.R8G8B8A8UNorm,
            ColorSpace.Srgb,
            PresentType.Mailbox,
            AllowTearing: false,
            MaximumFrameLatency: 2);
        using Swapchain swapchain = backend.CreateSwapchain(
            device,
            new SwapchainDesc(
                surface,
                2,
                TextureUsages.ColorAttachment,
                config,
                "telemetry swapchain"));
        Queue queue = backend.GetQueue(device, QueueType.Graphics);
        Assert.True(backend.TryGetCapability(device, out D3D12Diagnostics? diagnostics));
        Assert.NotNull(diagnostics);

        D3D12PresentationInfo initial = diagnostics.GetPresentationInfo(swapchain);
        Assert.Equal(config, initial.Config);
        Assert.Equal(1UL, initial.Generation);
        Assert.Equal(0, initial.AcquireAttemptCount);
        Assert.Equal(0, initial.PresentAttemptCount);
        Assert.False(initial.OutOfDate);

        SubmitAndPresent(backend, device, swapchain, queue);

        D3D12PresentationInfo presented = diagnostics.GetPresentationInfo(swapchain);
        Assert.Equal(1, presented.AcquireAttemptCount);
        Assert.Equal(0, presented.AcquireTimeoutCount);
        Assert.Equal(0, presented.AcquireFailureCount);
        Assert.Equal(1, presented.PresentAttemptCount);
        Assert.Equal(0, presented.PresentFailureCount);
        Assert.Equal(SwapchainAcquireStatus.Success, presented.LastAcquireStatus);
        Assert.Contains(
            presented.LastPresentStatus,
            new PresentStatus?[]
            {
                PresentStatus.Success,
                PresentStatus.Suboptimal,
                PresentStatus.Occluded,
            });
        Assert.True(presented.LastSubmissionCompletion > 0);
        Assert.True(presented.LastAcquireDuration >= TimeSpan.Zero);
        Assert.True(presented.LastPresentDuration >= TimeSpan.Zero);

        SwapchainConfig resized = config with { Width = 64, Height = 56 };
        Assert.Equal(ReconfigureStatus.Success, backend.Reconfigure(swapchain, resized));
        D3D12PresentationInfo reconfigured = diagnostics.GetPresentationInfo(swapchain);
        Assert.Equal(1, reconfigured.ReconfigureAttemptCount);
        Assert.Equal(0, reconfigured.ReconfigureFailureCount);
        Assert.Equal(ReconfigureStatus.Success, reconfigured.LastReconfigureStatus);
        Assert.Equal(2UL, reconfigured.Generation);
        Assert.Equal(64u, reconfigured.Config.Width);
        Assert.Equal(56u, reconfigured.Config.Height);
        Assert.True(reconfigured.LastReconfigureDuration >= TimeSpan.Zero);
    }

    [Fact]
    public void Native_debug_validation_retires_a_presented_generation_on_direct_dispose()
    {
        using D3D12TestWindow window = new();
        using var backend = new D3D12Backend(new D3D12BackendOptions(
            new D3D12ValidationOptions(
                DisableGpuBasedValidation: true,
                DisableSynchronizedQueueValidation: true)));
        ((INativeValidationControl)backend).EnableNativeValidation();
        using Device device = D3D12TestSupport.CreateWarpDevice(backend);
        using Surface surface = backend.CreateSurface(new SurfaceDesc(
            NativeWindowType.Win32,
            window.Handle));
        SwapchainConfig config = new(
            32, 32, Format.R8G8B8A8UNorm, ColorSpace.Srgb,
            PresentType.Mailbox, false, 2);
        Swapchain swapchain = backend.CreateSwapchain(
            device,
            new SwapchainDesc(surface, 2, TextureUsages.ColorAttachment, config));
        Queue queue = backend.GetQueue(device, QueueType.Graphics, 0);

        SubmitAndPresent(backend, device, swapchain, queue);
        swapchain.Dispose();

        Assert.True(swapchain.IsDisposed);
    }

    [Theory]
    [InlineData(PresentedTeardown.Swapchain)]
    [InlineData(PresentedTeardown.Surface)]
    [InlineData(PresentedTeardown.Device)]
    [InlineData(PresentedTeardown.Backend)]
    public void Native_validation_retires_an_in_flight_present_during_parent_teardown(
        PresentedTeardown teardown)
    {
        using IGraphicsBackend backend =
            new ValidationLayer(new D3D12Backend());
        ExercisePresentationLifecycle(backend, teardown);
    }

    private static void ExercisePresentationLifecycle(
        IGraphicsBackend backend,
        PresentedTeardown? teardown = null)
    {
        SwapchainImage invalid = default;
        Assert.Throws<InvalidOperationException>(() => _ = invalid.Texture);
        Assert.Throws<InvalidOperationException>(() => _ = invalid.Status);

        using D3D12TestWindow window = new();
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
        Assert.Throws<NotSupportedException>(() => backend.Acquire(
            swapchain,
            new SwapchainAcquireOptions(TimeSpan.FromSeconds(2), PreserveContents: true),
            out _));
        Assert.Equal(
            SwapchainAcquireStatus.Success,
            backend.Acquire(
                swapchain,
                new SwapchainAcquireOptions(TimeSpan.FromSeconds(2), PreserveContents: false),
                out SwapchainImage image));
        Assert.Equal(SwapchainImageStatus.Acquired, image.Status);
        Assert.Equal(TextureLayout.Undefined, image.InitialLayout);
        Assert.Equal(ResourceAccess.NoAccess, image.InitialAccess);
        Assert.Same(swapchain, image.Swapchain);

        Assert.Equal(
            SwapchainAcquireStatus.Timeout,
            backend.Acquire(
                swapchain,
                new SwapchainAcquireOptions(TimeSpan.FromTicks(1)),
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

        if (teardown is not null)
        {
            switch (teardown.Value)
            {
                case PresentedTeardown.Swapchain:
                    swapchain.Dispose();
                    break;
                case PresentedTeardown.Surface:
                    surface.Dispose();
                    break;
                case PresentedTeardown.Device:
                    device.Dispose();
                    break;
                case PresentedTeardown.Backend:
                    backend.Dispose();
                    break;
            }
            Assert.True(swapchain.IsDisposed);
            return;
        }

        QueueCompletion afterPresent = backend.Submit(
            queue,
            new QueueSubmitDesc([], [], [], [], []));
        Assert.Equal(completion.Value + 1, afterPresent.Value);
        Assert.Equal(
            WaitStatus.Completed,
            backend.WaitCpu(afterPresent, TimeSpan.FromSeconds(10)));

        SwapchainConfig resized = config with { Width = 96, Height = 80 };
        Assert.Equal(ReconfigureStatus.Success, backend.Reconfigure(swapchain, resized));
        Assert.Equal(originalGeneration + 1, swapchain.Info.Generation);
        Assert.Equal(96u, swapchain.Info.Config.Width);
        Assert.Equal(80u, swapchain.Info.Config.Height);
        Assert.Throws<InvalidOperationException>(() => _ = image.Texture);
        Assert.Throws<InvalidOperationException>(() => _ = image.Status);
        QueueCompletion afterReconfigure = backend.Submit(
            queue,
            new QueueSubmitDesc([], [], [], [], []));
        Assert.Equal(afterPresent.Value + 2, afterReconfigure.Value);
        Assert.Equal(
            WaitStatus.Completed,
            backend.WaitCpu(afterReconfigure, TimeSpan.FromSeconds(10)));

        ulong resizedGeneration = swapchain.Info.Generation;
        SwapchainConfig unsupported = resized with
        {
            Format = Format.R8G8B8A8UNorm,
            ColorSpace = ColorSpace.Hdr10,
        };
        Assert.Equal(ReconfigureStatus.Unsupported, backend.Reconfigure(swapchain, unsupported));
        Assert.Equal(resizedGeneration, swapchain.Info.Generation);

        swapchain.Dispose();
        Assert.Throws<ArgumentOutOfRangeException>(() => backend.Acquire(
            swapchain,
            new SwapchainAcquireOptions(TimeSpan.FromTicks(-1)),
            out _));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Only_the_native_presentation_queue_can_submit_or_present_swapchain_images(
        bool validationEnabled)
    {
        using IGraphicsBackend backend = validationEnabled
            ? new ValidationLayer(new D3D12Backend())
            : new D3D12Backend();
        using D3D12TestWindow window = new();
        AdapterInfo adapter = D3D12TestSupport.SelectWarp(backend);
        using Device device = backend.CreateDevice(new DeviceDesc(
            adapter.Id,
            [new(QueueType.Graphics, Count: 2)],
            optionalFeatures: DeviceFeatures.Presentation));
        using Surface surface = backend.CreateSurface(new SurfaceDesc(
            NativeWindowType.Win32,
            window.Handle));
        SwapchainConfig config = new(
            64, 64, Format.R8G8B8A8UNorm, ColorSpace.Srgb,
            PresentType.Mailbox, false, 2);
        using Swapchain swapchain = backend.CreateSwapchain(
            device,
            new SwapchainDesc(surface, 2, TextureUsages.ColorAttachment, config));
        Queue presentationQueue = backend.GetQueue(device, QueueType.Graphics, 0);
        Queue otherQueue = backend.GetQueue(device, QueueType.Graphics, 1);
        Assert.Equal(
            SwapchainAcquireStatus.Success,
            backend.Acquire(
                swapchain,
                new SwapchainAcquireOptions(TimeSpan.FromSeconds(2)),
                out SwapchainImage image));

        TextureSubresourceRange range = new(0, 1, 0, 1, TextureAspects.Color);
        using CommandContext wrongContext = backend.CreateCommandContext(
            device,
            new CommandContextDesc(QueueType.Graphics, 1, 1));
        backend.Begin(wrongContext);
        backend.Barrier(wrongContext, new TextureBarrier(
            image.Texture, range, image.InitialSync, PipelineSync.None,
            image.InitialAccess, ResourceAccess.NoAccess,
            image.InitialLayout, TextureLayout.Present));
        using RecordedCommands wrongCommands = backend.End(wrongContext);
        Exception? submitFailure = Record.Exception(() => backend.Submit(
            otherQueue,
            new QueueSubmitDesc([], [], [wrongCommands], [image], [])));
        Assert.True(
            submitFailure is ArgumentException or InvalidOperationException,
            submitFailure?.ToString());
        Assert.Equal(SwapchainImageStatus.Acquired, image.Status);

        using CommandContext context = backend.CreateCommandContext(
            device,
            new CommandContextDesc(QueueType.Graphics, 0, 1));
        backend.Begin(context);
        backend.Barrier(context, new TextureBarrier(
            image.Texture, range, image.InitialSync, PipelineSync.None,
            image.InitialAccess, ResourceAccess.NoAccess,
            image.InitialLayout, TextureLayout.Present));
        using RecordedCommands commands = backend.End(context);
        _ = backend.Submit(
            presentationQueue,
            new QueueSubmitDesc([], [], [commands], [image], []));
        Exception? presentFailure = Record.Exception(() => backend.Present(otherQueue, image));
        Assert.True(
            presentFailure is ArgumentException or InvalidOperationException,
            presentFailure?.ToString());
        Assert.Equal(SwapchainImageStatus.Submitted, image.Status);
        _ = backend.Present(presentationQueue, image);
        Assert.Equal(SwapchainImageStatus.Presented, image.Status);
    }

    private static Hdr10Metadata CreateHdr10Metadata() => new(
        RedPrimaryX: 35_400,
        RedPrimaryY: 14_600,
        GreenPrimaryX: 8_500,
        GreenPrimaryY: 39_850,
        BluePrimaryX: 6_550,
        BluePrimaryY: 2_300,
        WhitePointX: 15_635,
        WhitePointY: 16_450,
        MaximumMasteringLuminance: 1_000,
        MinimumMasteringLuminance: 50,
        MaximumContentLightLevel: 1_000,
        MaximumFrameAverageLightLevel: 400);

    public enum PresentedTeardown
    {
        Swapchain,
        Surface,
        Device,
        Backend,
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Parent_disposal_joins_an_already_running_swapchain_release(bool disposeDevice)
    {
        using D3D12TestWindow window = new();
        using var backend = new D3D12Backend();
        Device device = D3D12TestSupport.CreateWarpDevice(backend);
        Surface surface = backend.CreateSurface(new SurfaceDesc(
            NativeWindowType.Win32,
            window.Handle));
        SwapchainConfig config = new(
            32, 32, Format.R8G8B8A8UNorm, ColorSpace.Srgb,
            PresentType.Mailbox, false, 2);
        Swapchain swapchain = backend.CreateSwapchain(
            device,
            new SwapchainDesc(surface, 2, TextureUsages.ColorAttachment, config));
        Queue presentationQueue = backend.GetQueue(device, QueueType.Graphics, 0);
        using var gateHeld = new ManualResetEventSlim();
        using var releaseQueueGate = new ManualResetEventSlim();
        Thread holder = new(() =>
        {
            D3D12CommandQueueLock held = backend.LockCommandQueue(presentationQueue);
            try
            {
                gateHeld.Set();
                releaseQueueGate.Wait();
            }
            finally
            {
                held.Dispose();
            }
        });
        holder.Start();
        try
        {
            Assert.True(gateHeld.Wait(TimeSpan.FromSeconds(5)));
            using var childReturned = new ManualResetEventSlim();
            Thread child = new(() =>
            {
                swapchain.Dispose();
                childReturned.Set();
            });
            child.Start();
            Assert.True(SpinWait.SpinUntil(
                () => D3D12PrivateState.DisposeGateState(swapchain) == 1,
                TimeSpan.FromSeconds(5)));
            using var parentStarted = new ManualResetEventSlim();
            using var parentReturned = new ManualResetEventSlim();
            Thread parent = new(() =>
            {
                parentStarted.Set();
                if (disposeDevice)
                    device.Dispose();
                else
                    surface.Dispose();
                parentReturned.Set();
            });
            parent.Start();
            Assert.True(parentStarted.Wait(TimeSpan.FromSeconds(5)));
            Assert.False(parentReturned.Wait(TimeSpan.FromMilliseconds(100)));
            releaseQueueGate.Set();
            Assert.True(holder.Join(TimeSpan.FromSeconds(10)));
            Assert.True(child.Join(TimeSpan.FromSeconds(10)));
            Assert.True(parent.Join(TimeSpan.FromSeconds(10)));
            Assert.True(swapchain.IsDisposed);
        }
        finally
        {
            releaseQueueGate.Set();
            Assert.True(holder.Join(TimeSpan.FromSeconds(5)));
            swapchain.Dispose();
            surface.Dispose();
            device.Dispose();
        }
    }

    [Fact]
    public void Reconfigure_drain_failure_preserves_the_old_generation_for_retry()
    {
        using D3D12TestWindow window = new();
        using var backend = new D3D12Backend();
        using Device device = D3D12TestSupport.CreateWarpDevice(backend);
        using Surface surface = backend.CreateSurface(new SurfaceDesc(
            NativeWindowType.Win32,
            window.Handle));
        SwapchainConfig config = new(
            32, 32, Format.R8G8B8A8UNorm, ColorSpace.Srgb,
            PresentType.Mailbox, false, 2);
        using Swapchain swapchain = backend.CreateSwapchain(
            device,
            new SwapchainDesc(surface, 2, TextureUsages.ColorAttachment, config));
        Queue queue = backend.GetQueue(device, QueueType.Graphics, 0);
        ulong nextCompletion = D3D12PrivateState.NextCompletion(queue);
        D3D12PrivateState.SetNextCompletion(queue, ulong.MaxValue);
        ulong generation = swapchain.Info.Generation;
        Assert.Throws<InvalidOperationException>(() => backend.Reconfigure(
            swapchain,
            config with { Width = 40 }));
        Assert.Equal(generation, swapchain.Info.Generation);

        D3D12PrivateState.SetNextCompletion(queue, nextCompletion);
        Assert.Equal(
            ReconfigureStatus.Success,
            backend.Reconfigure(swapchain, config with { Width = 48 }));
        Assert.Equal(generation + 1, swapchain.Info.Generation);
        QueueCompletion afterRetry = backend.Submit(
            queue,
            new QueueSubmitDesc([], [], [], [], []));
        Assert.Equal(nextCompletion + 1, afterRetry.Value);
    }

    [Theory]
    [InlineData(TerminalSwapchainState.Disposed)]
    [InlineData(TerminalSwapchainState.DeviceLost)]
    [InlineData(TerminalSwapchainState.OutOfDate)]
    public void Terminal_state_precedes_unsupported_reconfigure(
        TerminalSwapchainState terminalState)
    {
        using D3D12TestWindow window = new();
        using var backend = new D3D12Backend();
        using Device device = D3D12TestSupport.CreateWarpDevice(backend);
        using Surface surface = backend.CreateSurface(new SurfaceDesc(
            NativeWindowType.Win32,
            window.Handle));
        SwapchainConfig config = new(
            32, 32, Format.R8G8B8A8UNorm, ColorSpace.Srgb,
            PresentType.Mailbox, false, 2);
        using Swapchain swapchain = backend.CreateSwapchain(
            device,
            new SwapchainDesc(surface, 2, TextureUsages.ColorAttachment, config));
        Queue queue = backend.GetQueue(device, QueueType.Graphics, 0);
        switch (terminalState)
        {
            case TerminalSwapchainState.Disposed:
                swapchain.Dispose();
                break;
            case TerminalSwapchainState.DeviceLost:
                D3D12PrivateState.MarkSoftwareLost(device);
                break;
            case TerminalSwapchainState.OutOfDate:
                swapchain.GetType().GetField(
                    "_outOfDate",
                    BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(swapchain, true);
                break;
        }
        ulong nextCompletion = D3D12PrivateState.NextCompletion(queue);
        SwapchainConfig unsupported = config with { ColorSpace = ColorSpace.Hdr10 };
        Exception? failure = Record.Exception(() => backend.Reconfigure(swapchain, unsupported));
        Assert.True(
            failure is ObjectDisposedException or InvalidOperationException ||
            failure is GraphicsException { Error: GraphicsError.DeviceLost },
            failure?.ToString());
        Assert.Equal(nextCompletion, D3D12PrivateState.NextCompletion(queue));
        Assert.Equal(1ul, swapchain.Info.Generation);
    }

    public enum TerminalSwapchainState
    {
        Disposed,
        DeviceLost,
        OutOfDate,
    }

    [Fact]
    public void Capability_only_untrusted_retirement_is_retained_and_diagnosed()
    {
        var backend = new D3D12Backend();
        Device device = D3D12TestSupport.CreateWarpDevice(backend);
        Assert.True(backend.TryGetCapability(device, out D3D12Diagnostics? diagnostics));
        Assert.NotNull(diagnostics);
        Queue queue = backend.GetQueue(device, QueueType.Graphics, 0);
        Buffer buffer = backend.CreateBuffer(
            device,
            new BufferDesc(
                64 * 1024,
                BufferUsages.CopySource | BufferUsages.CopyDestination));
        ResidencyResource resource = backend.GetResidencyResource(buffer);
        D3D12PrivateState.SetNextCompletion(queue, ulong.MaxValue);
        GraphicsException acceptedFailure = Assert.Throws<GraphicsException>(() =>
            backend.EnqueueMakeResident(queue, [resource]));
        Assert.Equal(GraphicsError.DeviceLost, acceptedFailure.Error);
        buffer.Dispose();
        QueueRetirementSnapshot seeded = D3D12PrivateState.QueueRetirements(queue);
        Assert.Equal(0, seeded.PendingSubmissionCount);
        Assert.Equal(0, seeded.PendingPresentationCount);
        Assert.Equal(0, seeded.PendingCapabilityCount);
        Assert.Equal(1, seeded.UntrustedCapabilityCount);
        Assert.True(seeded.CapabilityNativeReferenceCount > 0);

        device.Dispose();
        Assert.Equal(DeviceStatus.Disposed, device.Status);
        Exception failure = Assert.IsType<InvalidOperationException>(diagnostics.TeardownFailure);
        Assert.Contains("completion was not verified", failure.Message);
        Assert.Null(diagnostics.DeviceLoss);
        QueueRetirementSnapshot retained = D3D12PrivateState.QueueRetirements(queue);
        Assert.Equal(1, retained.UntrustedCapabilityCount);
        Assert.True(retained.CapabilityNativeReferenceCount > 0);
        Assert.True(retained.HasNativeQueue);
        Assert.True(retained.HasFence);
        device.Dispose();
        Assert.Same(failure, diagnostics.TeardownFailure);
        backend.Dispose();
        Assert.True(D3D12PrivateState.IsRuntimeQuarantined(backend));
    }

    [Fact]
    public void Final_drain_waits_for_maximum_target_and_collects_all_payload_families()
    {
        using D3D12TestWindow window = new();
        using var backend = new D3D12Backend();
        Device device = D3D12TestSupport.CreateWarpDevice(backend);
        Queue queue = backend.GetQueue(device, QueueType.Graphics, 0);
        _ = backend.Submit(queue, new QueueSubmitDesc([], [], [], [], []));
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
        using Buffer buffer = backend.CreateBuffer(
            device,
            new BufferDesc(
                64 * 1024,
                BufferUsages.CopySource | BufferUsages.CopyDestination));
        _ = backend.EnqueueMakeResident(
            queue,
            [backend.GetResidencyResource(buffer)]);

        QueueRetirementSnapshot seeded = D3D12PrivateState.QueueRetirements(queue);
        ulong expected = Math.Max(
            seeded.SubmissionTarget,
            Math.Max(seeded.PresentationTarget, seeded.CapabilityTarget));
        Assert.Equal(seeded.CapabilityTarget, expected);
        Assert.True(seeded.SubmissionTarget < seeded.PresentationTarget);
        Assert.True(seeded.PresentationTarget < seeded.CapabilityTarget);
        device.Dispose();

        QueueRetirementSnapshot retired = D3D12PrivateState.QueueRetirements(queue);
        Assert.Equal(0, retired.PendingSubmissionCount);
        Assert.Equal(0, retired.PendingPresentationCount);
        Assert.Equal(0, retired.PendingCapabilityCount);
        Assert.False(retired.HasNativeQueue);
        Assert.False(retired.HasFence);
    }

    [Fact]
    public void Precreated_intrusive_retirement_registration_allocates_zero_bytes()
    {
        var warm = new IntrusiveRetirementChain<TestRetirementPayload>();
        warm.Append(new TestRetirementPayload(), 1);
        warm.Abandon();
        var pending = new IntrusiveRetirementChain<TestRetirementPayload>();
        var untrusted = new IntrusiveRetirementChain<TestRetirementPayload>();
        var first = new TestRetirementPayload();
        var second = new TestRetirementPayload();

        long before = GC.GetAllocatedBytesForCurrentThread();
        pending.Append(first, 17);
        untrusted.Append(second, 0);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocated);
        Assert.True(pending.HasAny);
        Assert.True(untrusted.HasAny);
        pending.Abandon();
        untrusted.Abandon();
        Assert.True(first.Disposed);
        Assert.True(second.Disposed);
    }

    [Fact]
    public void Intrusive_retirement_nonempty_authority_is_the_head()
    {
        var chain = new IntrusiveRetirementChain<TestRetirementPayload>();
        var payload = new TestRetirementPayload();

        chain.Append(payload, 17);

        Assert.True(chain.HasAny);
        Assert.Equal(17UL, chain.Target);
        chain.Abandon();
        Assert.False(chain.HasAny);
        Assert.Equal(0UL, chain.Target);
        Assert.True(payload.Disposed);
    }

    [Fact]
    public void Swapchain_dispose_signal_failure_keeps_the_generation_in_untrusted_retirement()
    {
        using D3D12TestWindow window = new();
        using var backend = new D3D12Backend();
        Device device = D3D12TestSupport.CreateWarpDevice(backend);
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

        D3D12PrivateState.SetNextCompletion(queue, ulong.MaxValue);
        swapchain.Dispose();

        QueueRetirementSnapshot retained =
            D3D12PrivateState.QueueRetirements(queue);
        Assert.Equal(0, retained.PendingPresentationCount);
        Assert.Equal(1, retained.UntrustedPresentationCount);
        Assert.True(retained.PresentationNativeReferenceCount > 0);
        Assert.False(D3D12PrivateState.NativeDeviceLossConfirmed(device));

        D3D12PrivateState.ConfirmNativeDeviceLoss(device);
        device.Dispose();
        QueueRetirementSnapshot abandoned =
            D3D12PrivateState.QueueRetirements(queue);
        Assert.Equal(0, abandoned.UntrustedPresentationCount);
        Assert.Equal(0, abandoned.PresentationNativeReferenceCount);
    }

    [Fact]
    public void Confirmed_native_loss_skips_wait_and_abandons_pending_and_untrusted_payloads()
    {
        using D3D12TestWindow window = new();
        using var backend = new D3D12Backend();
        Device device = D3D12TestSupport.CreateWarpDevice(backend);
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
        buffer.Dispose();
        QueueRetirementSnapshot seeded = D3D12PrivateState.QueueRetirements(queue);
        Assert.Equal(1, seeded.PendingSubmissionCount);
        Assert.Equal(1, seeded.PendingPresentationCount);
        Assert.Equal(1, seeded.PendingCapabilityCount);
        Assert.Equal(0, seeded.UntrustedSubmissionCount);
        Assert.Equal(0, seeded.UntrustedPresentationCount);
        Assert.Equal(0, seeded.UntrustedCapabilityCount);
        Assert.True(seeded.PresentationNativeReferenceCount > 0);
        Assert.True(seeded.CapabilityNativeReferenceCount > 0);
        D3D12PrivateState.ConfirmNativeDeviceLoss(device);
        device.Dispose();

        QueueRetirementSnapshot abandoned = D3D12PrivateState.QueueRetirements(queue);
        Assert.Equal(0, abandoned.PendingSubmissionCount);
        Assert.Equal(0, abandoned.UntrustedSubmissionCount);
        Assert.Equal(0, abandoned.PendingPresentationCount);
        Assert.Equal(0, abandoned.UntrustedPresentationCount);
        Assert.Equal(0, abandoned.PendingCapabilityCount);
        Assert.Equal(0, abandoned.UntrustedCapabilityCount);
        Assert.Equal(0, abandoned.CapabilityNativeReferenceCount);
        Assert.False(abandoned.HasNativeQueue);
        Assert.False(abandoned.HasFence);
    }

    [Fact]
    public void Concurrent_queue_loss_publication_is_lock_free_across_queue_gates()
    {
        using var backend = new D3D12Backend();
        AdapterInfo adapter = D3D12TestSupport.SelectWarp(backend);
        using Device device = backend.CreateDevice(new DeviceDesc(
            adapter.Id,
            [new DeviceQueueDesc(QueueType.Graphics, Count: 2)]));
        Queue first = backend.GetQueue(device, QueueType.Graphics, 0);
        Queue second = backend.GetQueue(device, QueueType.Graphics, 1);
        using var rendezvous = new Barrier(2);
        Exception?[] failures = new Exception?[2];
        Thread firstPublication = new(() => PublishUnderQueueGate(first, 0));
        Thread secondPublication = new(() => PublishUnderQueueGate(second, 1));
        firstPublication.Start();
        secondPublication.Start();
        Assert.True(firstPublication.Join(TimeSpan.FromSeconds(5)));
        Assert.True(secondPublication.Join(TimeSpan.FromSeconds(5)));
        Assert.Null(failures[0]);
        Assert.Null(failures[1]);
        Assert.True(D3D12PrivateState.NativeDeviceLossConfirmed(device));

        void PublishUnderQueueGate(Queue queue, int index)
        {
            try
            {
                D3D12CommandQueueLock held = backend.LockCommandQueue(queue);
                try
                {
                    Assert.True(rendezvous.SignalAndWait(TimeSpan.FromSeconds(2)));
                    D3D12PrivateState.ConfirmNativeDeviceLoss(device);
                }
                finally
                {
                    held.Dispose();
                }
            }
            catch (Exception exception)
            {
                failures[index] = exception;
            }
        }

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

    private sealed class TestRetirementPayload :
        IntrusiveRetirementPayload<TestRetirementPayload>
    {
        internal bool Disposed { get; private set; }

        public override void Dispose() => Disposed = true;
    }

}
