using SomeEngine.Graphics.Direct3D12;
using SomeEngine.Graphics.Validation;
using System.Reflection;
using Xunit;

namespace SomeEngine.Graphics.Direct3D12.Tests;

[Trait("Isolation", "PresentationProcess")]
public sealed class WarpPresentationTests
{


    [Fact]
    public void Hdr10_metadata_is_rejected_for_non_hdr10_color_space_before_native_creation()
    {
        using D3D12TestWindow window = new();
        using IGraphicsBackend backend = D3D12GraphicsBackend.Create();
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
        using IGraphicsBackend backend = D3D12GraphicsBackend.Create();
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
    public void Acquire_submit_present_and_reconfigure_enforce_sequence_and_commit_boundaries_DoesNotThrow()
    {
        using IGraphicsBackend backend = D3D12GraphicsBackend.Create();
        ExercisePresentationLifecycle(backend);
    }

    [Fact]
    public void Native_validation_accepts_the_complete_presentation_lifecycle_DoesNotThrow()
    {
        using IGraphicsBackend backend =
            new ValidationLayer(D3D12GraphicsBackend.Create());
        ExercisePresentationLifecycle(backend);
    }

    [Fact]
    public void Dxgi_presentation_telemetry_tracks_configuration_latency_and_completion()
    {
        using D3D12TestWindow window = new();
        using IGraphicsBackend backend = D3D12GraphicsBackend.Create();
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



    [Theory]
    [InlineData(PresentedTeardown.Swapchain)]
    [InlineData(PresentedTeardown.Surface)]
    [InlineData(PresentedTeardown.Device)]
    [InlineData(PresentedTeardown.Backend)]
    public void Native_validation_retires_an_in_flight_present_during_parent_teardown_DoesNotThrow(
        PresentedTeardown teardown)
    {
        using IGraphicsBackend backend =
            new ValidationLayer(D3D12GraphicsBackend.Create());
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
            ? new ValidationLayer(D3D12GraphicsBackend.Create())
            : D3D12GraphicsBackend.Create();
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
        var events = new System.Collections.Concurrent.ConcurrentQueue<string>();
        using D3D12TestWindow window = new();
        using var backend = new ValidationLayer(D3D12GraphicsBackend.Create());
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
                events.Enqueue("swapchain disposal completed");
                childReturned.Set();
            });
            child.Start();
            using var parentStarted = new ManualResetEventSlim();
            using var parentReturned = new ManualResetEventSlim();
            Thread parent = new(() =>
            {
                parentStarted.Set();
                if (disposeDevice)
                    device.Dispose();
                else
                    surface.Dispose();
                events.Enqueue("parent disposal completed");
                parentReturned.Set();
            });
            parent.Start();
            Assert.True(parentStarted.Wait(TimeSpan.FromSeconds(5)));
            events.Enqueue("release");
            releaseQueueGate.Set();
            Assert.True(holder.Join(TimeSpan.FromSeconds(10)));
            Assert.True(child.Join(TimeSpan.FromSeconds(10)));
            Assert.True(parent.Join(TimeSpan.FromSeconds(10)));
            Assert.Equal("release", events.First());
            Assert.Contains("swapchain disposal completed", events);
            Assert.Contains("parent disposal completed", events);
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



    public enum TerminalSwapchainState
    {
        Disposed,
        DeviceLost,
        OutOfDate,
    }


    [Fact]
    public void Final_drain_waits_for_maximum_target_and_collects_all_payload_families_DoesNotThrow()
    {
        using D3D12TestWindow window = new();
        using IGraphicsBackend backend = D3D12GraphicsBackend.Create();
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

        device.Dispose();

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
