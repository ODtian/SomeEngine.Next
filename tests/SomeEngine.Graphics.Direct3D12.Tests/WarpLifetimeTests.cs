using System.Reflection;
using Silk.NET.Direct3D12;
using SlangShaderSharp;
using SomeEngine.Graphics.Direct3D12;
using SomeEngine.Graphics.Validation;
using System.Collections;
using Xunit;

namespace SomeEngine.Graphics.Direct3D12.Tests;

public sealed class WarpLifetimeTests
{
    [Fact]
    public void Validated_graphics_root_cross_thread_dispose_joins_the_single_receiver_release()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        int lifetimeMessages = 0;
        ValidationLayer? validation = null;
        var sink = new DelegateValidationMessageSink(message =>
        {
            if (message.Area != "Lifetime")
                return;

            Interlocked.Increment(ref lifetimeMessages);
            validation!.Dispose();
            entered.Set();
            release.Wait();
        });
        var backend = new D3D12Backend();
        validation = new ValidationLayer(
            backend,
            new ValidationOptions(sink, ReportLiveObjectsOnDispose: true));
        Device device = CreateWarpDevice(validation);

        try
        {
            var owner = new Thread(validation.Dispose);
            owner.Start();
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            Assert.Throws<ObjectDisposedException>(() => validation.TryEnumerateAdapters(
                new AdapterEnumerationOptions(IncludeSoftware: true),
                [],
                out _));

            using var contenderStarted = new ManualResetEventSlim();
            using var contenderReturned = new ManualResetEventSlim();
            var contender = new Thread(() =>
            {
                contenderStarted.Set();
                validation.Dispose();
                contenderReturned.Set();
            });
            contender.Start();
            Assert.True(contenderStarted.Wait(TimeSpan.FromSeconds(5)));
            Assert.False(contenderReturned.Wait(TimeSpan.FromMilliseconds(100)));
            release.Set();
            Assert.True(owner.Join(TimeSpan.FromSeconds(10)));
            Assert.True(contender.Join(TimeSpan.FromSeconds(10)));

            Assert.Equal(1, Volatile.Read(ref lifetimeMessages));
            Assert.Equal(DeviceStatus.Disposed, device.Status);
            Assert.Throws<ObjectDisposedException>(() => backend.TryEnumerateAdapters(
                new AdapterEnumerationOptions(IncludeSoftware: true),
                [],
                out _));
        }
        finally
        {
            release.Set();
            validation.Dispose();
        }
    }

    [Fact]
    public void Validation_receiver_contenders_join_and_sink_failure_is_observational()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var sink = new BlockingThrowingSink(entered, release);
        var backend = new D3D12Backend();
        var validation = new ValidationLayer(
            backend,
            new ValidationOptions(sink, ReportLiveObjectsOnDispose: true));
        Device device = D3D12TestSupport.CreateWarpDevice(validation);

        try
        {
            var owner = new Thread(validation.Dispose);
            owner.Start();
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            using var contenderStarted = new ManualResetEventSlim();
            using var contenderReturned = new ManualResetEventSlim();
            var contender = new Thread(() =>
            {
                contenderStarted.Set();
                validation.Dispose();
                contenderReturned.Set();
            });
            contender.Start();
            Assert.True(contenderStarted.Wait(TimeSpan.FromSeconds(5)));
            Assert.False(contenderReturned.Wait(TimeSpan.FromMilliseconds(100)));
            release.Set();
            Assert.True(owner.Join(TimeSpan.FromSeconds(10)));
            Assert.True(contender.Join(TimeSpan.FromSeconds(10)));

            Assert.Equal(1, sink.ReportCount);
            Assert.Equal(DeviceStatus.Disposed, device.Status);
            Assert.Throws<ObjectDisposedException>(() => backend.TryEnumerateAdapters(
                new AdapterEnumerationOptions(IncludeSoftware: true),
                [],
                out _));

            validation.Dispose();
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int index = 0; index < 1_024; index++)
                validation.Dispose();
            Assert.Equal(before, GC.GetAllocatedBytesForCurrentThread());
        }
        finally
        {
            release.Set();
            validation.Dispose();
        }
    }

    [Fact]
    public void Graphics_object_cross_thread_dispose_joins_one_release()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var value = new BlockingGraphicsObject(entered, release);
        var owner = new Thread(value.Dispose);
        owner.Start();
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
        using var contenderStarted = new ManualResetEventSlim();
        using var contenderReturned = new ManualResetEventSlim();
        var contender = new Thread(() =>
        {
            contenderStarted.Set();
            value.Dispose();
            contenderReturned.Set();
        });
        contender.Start();
        Assert.True(contenderStarted.Wait(TimeSpan.FromSeconds(5)));
        Assert.False(contenderReturned.Wait(TimeSpan.FromMilliseconds(100)));
        release.Set();
        Assert.True(owner.Join(TimeSpan.FromSeconds(5)));
        Assert.True(contender.Join(TimeSpan.FromSeconds(5)));
        Assert.Equal(1, value.ReleaseCount);
    }

    [Fact]
    public void Backend_cross_thread_dispose_joins_while_real_queue_gate_is_held()
    {
        var backend = new D3D12Backend();
        Device device = D3D12TestSupport.CreateWarpDevice(backend);
        Queue queue = backend.GetQueue(device, QueueType.Copy);
        using var gateHeld = new ManualResetEventSlim();
        using var invalidationObserved = new ManualResetEventSlim();
        using var releaseGate = new ManualResetEventSlim();
        using var ownerReturned = new ManualResetEventSlim();
        using var contenderStarted = new ManualResetEventSlim();
        using var contenderReturned = new ManualResetEventSlim();
        Thread holder = new(() =>
        {
            D3D12CommandQueueLock held = backend.LockCommandQueue(queue);
            try
            {
                gateHeld.Set();
                Assert.True(SpinWait.SpinUntil(
                    () => device.Status == DeviceStatus.Disposed,
                    TimeSpan.FromSeconds(5)));
                Assert.False(held.IsHeld);
                AssertQueueLockPointerUnavailable(held);
                invalidationObserved.Set();
                releaseGate.Wait();
            }
            finally
            {
                held.Dispose();
            }
        });
        Thread owner = new(() =>
        {
            backend.Dispose();
            ownerReturned.Set();
        });
        Thread contender = new(() =>
        {
            contenderStarted.Set();
            backend.Dispose();
            contenderReturned.Set();
        });

        holder.Start();
        try
        {
            Assert.True(gateHeld.Wait(TimeSpan.FromSeconds(5)));
            owner.Start();
            Assert.True(SpinWait.SpinUntil(
                () => device.Status == DeviceStatus.Disposed,
                TimeSpan.FromSeconds(5)));
            Assert.True(invalidationObserved.Wait(TimeSpan.FromSeconds(5)));
            contender.Start();
            Assert.True(contenderStarted.Wait(TimeSpan.FromSeconds(5)));
            Assert.False(ownerReturned.Wait(TimeSpan.FromMilliseconds(100)));
            Assert.False(contenderReturned.Wait(TimeSpan.FromMilliseconds(100)));
            releaseGate.Set();
            Assert.True(holder.Join(TimeSpan.FromSeconds(10)));
            Assert.True(owner.Join(TimeSpan.FromSeconds(10)));
            Assert.True(contender.Join(TimeSpan.FromSeconds(10)));
            Assert.False(D3D12PrivateState.IsRuntimeQuarantined(backend));
        }
        finally
        {
            releaseGate.Set();
            _ = holder.Join(TimeSpan.FromSeconds(5));
            if (owner.ThreadState != ThreadState.Unstarted)
                _ = owner.Join(TimeSpan.FromSeconds(5));
            if (contender.ThreadState != ThreadState.Unstarted)
                _ = contender.Join(TimeSpan.FromSeconds(5));
            backend.Dispose();
        }
    }

    [Fact]
    public void External_handle_cross_thread_contender_joins()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var contenderStarted = new ManualResetEventSlim();
        using var contenderReturned = new ManualResetEventSlim();
        ExternalHandle? handle = null;
        int releaseCount = 0;
        handle = new ExternalHandle(ExternalHandleType.OpaqueWin32, 1, _ =>
        {
            releaseCount++;
            handle!.Dispose();
            entered.Set();
            release.Wait();
            throw new InvalidOperationException("close failure must not escape Dispose");
        });

        var owner = new Thread(handle.Dispose);
        var contender = new Thread(() =>
        {
            contenderStarted.Set();
            handle.Dispose();
            contenderReturned.Set();
        });
        owner.Start();
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
        contender.Start();
        Assert.True(contenderStarted.Wait(TimeSpan.FromSeconds(5)));
        Assert.False(contenderReturned.Wait(TimeSpan.FromMilliseconds(100)));
        release.Set();
        Assert.True(owner.Join(TimeSpan.FromSeconds(5)));
        Assert.True(contender.Join(TimeSpan.FromSeconds(5)));
        Assert.Equal(1, releaseCount);
        Assert.Throws<ObjectDisposedException>(() => _ = handle.Value);
    }

    [Fact]
    public void Recorded_commands_copies_collectively_join_discard_for_the_same_sequence()
    {
        using var backend = new D3D12Backend();
        using Device device = D3D12TestSupport.CreateWarpDevice(backend);
        Queue queue = backend.GetQueue(device, QueueType.Copy);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var contenderStarted = new ManualResetEventSlim();
        using var contenderReturned = new ManualResetEventSlim();
        var lease = new BlockingRecordedCommandsLease(device, queue, entered, release);
        RecordedCommands first = lease.Create(1);
        RecordedCommands copy = first;

        var owner = new Thread(first.Dispose);
        var contender = new Thread(() =>
        {
            contenderStarted.Set();
            copy.Dispose();
            contenderReturned.Set();
        });
        owner.Start();
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
        contender.Start();
        Assert.True(contenderStarted.Wait(TimeSpan.FromSeconds(5)));
        Assert.False(contenderReturned.Wait(TimeSpan.FromMilliseconds(100)));
        release.Set();
        Assert.True(owner.Join(TimeSpan.FromSeconds(5)));
        Assert.True(contender.Join(TimeSpan.FromSeconds(5)));
        Assert.Equal(1, lease.DiscardCount);
        Assert.Equal(RecordedCommandsStatus.Disposed, copy.Status);
    }

    [Fact]
    public void Graphics_object_release_and_diagnostic_failures_do_not_escape_dispose()
    {
        var value = new ThrowingGraphicsObject();
        value.Dispose();
        value.Dispose();
        Assert.Equal(1, value.ReleaseCount);
        Assert.Equal(1, value.DiagnosticCount);
    }

    [Fact]
    public void Device_child_failure_publishes_first_diagnostic_and_preserves_native_authority()
    {
        var backend = new D3D12Backend();
        Device device = D3D12TestSupport.CreateWarpDevice(backend);
        Assert.True(backend.TryGetCapability(device, out D3D12Diagnostics? diagnostics));
        var failure = new InvalidOperationException("test-local Device child teardown failure");
        var child = new ThrowingDeviceChild(device, failure);
        D3D12PrivateState.RegisterDeviceChild(device, child);

        device.Dispose();

        Assert.Same(failure, diagnostics!.TeardownFailure);
        Assert.Equal(1, child.ReleaseCount);
        Assert.True(D3D12PrivateState.HasNativeDevice(device));
        backend.Dispose();

        Assert.Same(failure, diagnostics.TeardownFailure);
        Assert.Equal(1, child.ReleaseCount);
        Assert.True(D3D12PrivateState.HasNativeDevice(device));
        Assert.True(D3D12PrivateState.IsRuntimeQuarantined(backend));
    }

    [Fact]
    public void Child_registry_retains_a_failed_child_and_disposes_every_sibling()
    {
        object gate = new();
        var registry = new GraphicsObjectRegistry(gate);
        var tail = new RegistryChild(registry, throws: false);
        var middle = new RegistryChild(registry, throws: false);
        var head = new RegistryChild(registry, throws: true);
        registry.Add(tail);
        registry.Add(middle);
        registry.Add(head);

        GraphicsObject? children = registry.CloseAndBuildDrainList();
        while (children is GraphicsObject child)
        {
            children = child.RegistryDrainNext;
            child.RegistryDrainNext = null;
            child.DisposeFromParent();
            _ = registry.CompleteDrain(child);
        }

        Assert.Equal(1, head.ReleaseCount);
        Assert.Equal(1, middle.ReleaseCount);
        Assert.Equal(1, tail.ReleaseCount);
        Assert.True(registry.HasRetainedFailures);
    }

    [Fact]
    public void Child_registry_rejects_duplicate_registration()
    {
        object gate = new();
        var registry = new GraphicsObjectRegistry(gate);
        var child = new RegistryChild(registry, throws: false);
        registry.Add(child);

        _ = Assert.Throws<InvalidOperationException>(() => registry.Add(child));

        GraphicsObject? children = registry.CloseAndBuildDrainList();
        while (children is GraphicsObject value)
        {
            children = value.RegistryDrainNext;
            value.RegistryDrainNext = null;
            value.DisposeFromParent();
            _ = registry.CompleteDrain(value);
        }
        Assert.Equal(1, child.ReleaseCount);
        Assert.False(registry.HasRetainedFailures);
    }

    [Fact]
    public void Backend_cascade_drains_healthy_device_and_surface_after_failed_head_device()
    {
        using var window = new D3D12TestWindow();
        var backend = new D3D12Backend();
        Device healthy = D3D12TestSupport.CreateWarpDevice(backend);
        Surface surface = backend.CreateSurface(new SurfaceDesc(
            NativeWindowType.Win32,
            window.Handle));
        Device failed = D3D12TestSupport.CreateWarpDevice(backend);
        Assert.True(backend.TryGetCapability(failed, out D3D12Diagnostics? diagnostics));
        var failure = new InvalidOperationException("backend head Device child release failed");
        var child = new ThrowingDeviceChild(failed, failure);
        D3D12PrivateState.RegisterDeviceChild(failed, child);

        backend.Dispose();

        Assert.Equal(1, child.ReleaseCount);
        Assert.Same(failure, diagnostics!.TeardownFailure);
        Assert.True(D3D12PrivateState.HasNativeDevice(failed));
        Assert.False(D3D12PrivateState.HasNativeDevice(healthy));
        Assert.Equal(2, D3D12PrivateState.DisposeGateState(surface));
        Assert.True(D3D12PrivateState.IsRuntimeQuarantined(backend));
    }

    [Fact]
    public void Preexisting_teardown_diagnostic_does_not_interrupt_device_child_cascade()
    {
        var backend = new D3D12Backend();
        Device device = D3D12TestSupport.CreateWarpDevice(backend);
        Assert.True(backend.TryGetCapability(device, out D3D12Diagnostics? diagnostics));
        Buffer child = backend.CreateBuffer(
            device,
            new BufferDesc(256, BufferUsages.CopySource),
            MemoryType.Upload);
        var first = new InvalidOperationException("preexisting teardown diagnostic");
        device.RecordReleaseFailure(first);

        device.Dispose();

        Assert.Same(first, diagnostics!.TeardownFailure);
        Assert.False(D3D12PrivateState.HasNativeResource(child));
        Assert.True(D3D12PrivateState.HasNativeDevice(device));
        backend.Dispose();
        Assert.True(D3D12PrivateState.IsRuntimeQuarantined(backend));
    }

    [Fact]
    public void Backend_teardown_quiesces_submitted_payload_before_releasing_native_roots()
    {
        var backend = new D3D12Backend();
        Device device = D3D12TestSupport.CreateWarpDevice(backend);
        Assert.True(backend.TryGetCapability(device, out D3D12Diagnostics? diagnostics));
        CommandContext context = backend.CreateCommandContext(
            device,
            new CommandContextDesc(QueueType.Copy, 0, 1));
        backend.Begin(context, default);
        RecordedCommands commands = backend.End(context);
        Queue queue = backend.GetQueue(device, QueueType.Copy);
        _ = backend.Submit(queue, new QueueSubmitDesc([], [], [commands], [], []));

        backend.Dispose();

        Assert.Equal(RecordedCommandsStatus.Completed, commands.Status);
        Assert.Null(diagnostics!.TeardownFailure);
        Assert.False(D3D12PrivateState.HasNativeDevice(device));
        Assert.False(D3D12PrivateState.IsRuntimeQuarantined(backend));
        commands.Dispose();
        context.Dispose();
        device.Dispose();
    }

    [Fact]
    public unsafe void Native_queue_lock_copies_release_from_another_thread_and_contenders_join()
    {
        var exclusion = new QueueExclusion();
        exclusion.Enter();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var firstStarted = new ManualResetEventSlim();
        using var secondStarted = new ManualResetEventSlim();
        using var firstReturned = new ManualResetEventSlim();
        using var secondReturned = new ManualResetEventSlim();
        const ulong sequence = 7;
        var lease = new BlockingQueueLockLease(exclusion, sequence, entered, release);
        D3D12CommandQueueLock original = new(lease, sequence);

        var first = new Thread(() =>
        {
            D3D12CommandQueueLock copy = new(lease, sequence);
            firstStarted.Set();
            copy.Dispose();
            firstReturned.Set();
        });
        var second = new Thread(() =>
        {
            D3D12CommandQueueLock copy = new(lease, sequence);
            secondStarted.Set();
            copy.Dispose();
            secondReturned.Set();
        });
        first.Start();
        Assert.True(firstStarted.Wait(TimeSpan.FromSeconds(5)));
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
        Assert.False(original.IsHeld);
        AssertQueueLockPointerUnavailable(original);
        second.Start();
        Assert.True(secondStarted.Wait(TimeSpan.FromSeconds(5)));
        Assert.False(firstReturned.Wait(TimeSpan.FromMilliseconds(100)));
        Assert.False(secondReturned.Wait(TimeSpan.FromMilliseconds(100)));
        release.Set();
        Assert.True(first.Join(TimeSpan.FromSeconds(5)));
        Assert.True(second.Join(TimeSpan.FromSeconds(5)));
        Assert.Equal(1, lease.ReleaseCount);

        Assert.False(original.IsHeld);
        original.Dispose();
        using (exclusion.EnterScope())
        {
        }
    }

    [Fact]
    public void Structured_uav_retains_both_native_resources_without_owning_their_wrappers()
    {
        using var backend = new D3D12Backend();
        using Device device = D3D12TestSupport.CreateWarpDevice(backend);
        Buffer data = backend.CreateBuffer(
            device,
            new BufferDesc(4_096, BufferUsages.ShaderWrite));
        Buffer counter = backend.CreateBuffer(
            device,
            new BufferDesc(4_096, BufferUsages.ShaderWrite));
        BufferUav first = backend.CreateBufferUav(
            device,
            new BufferUavDesc(
                data,
                BufferRange.Whole,
                StructureStride: 16,
                CounterBuffer: counter));

        counter.Dispose();
        Assert.Equal(0, D3D12PrivateState.DisposeGateState(first));
        Assert.True(D3D12PrivateState.HasNativeResource(counter));
        Assert.True(D3D12PrivateState.HasNativeResource(data));
        first.Dispose();
        Assert.False(D3D12PrivateState.HasNativeResource(counter));

        Buffer secondCounter = backend.CreateBuffer(
            device,
            new BufferDesc(4_096, BufferUsages.ShaderWrite));
        BufferUav second = backend.CreateBufferUav(
            device,
            new BufferUavDesc(
                data,
                BufferRange.Whole,
                StructureStride: 16,
                CounterBuffer: secondCounter));
        data.Dispose();
        Assert.Equal(0, D3D12PrivateState.DisposeGateState(second));
        Assert.True(D3D12PrivateState.HasNativeResource(data));
        Assert.True(D3D12PrivateState.HasNativeResource(secondCounter));
        second.Dispose();
        Assert.False(D3D12PrivateState.HasNativeResource(data));
        secondCounter.Dispose();
    }

    [Fact]
    public void Structured_uav_with_identical_resource_and_counter_retains_one_native_payload()
    {
        var backend = new D3D12Backend();
        Device device = D3D12TestSupport.CreateWarpDevice(backend);
        Assert.True(backend.TryGetCapability(device, out D3D12Diagnostics? diagnostics));
        Buffer parent = backend.CreateBuffer(
            device,
            new BufferDesc(4_096, BufferUsages.ShaderWrite));
        BufferUav ordinary = backend.CreateBufferUav(
            device,
            new BufferUavDesc(
                parent,
                BufferRange.Whole,
                StructureStride: 16,
                CounterBuffer: parent));

        ordinary.Dispose();
        Assert.True(D3D12PrivateState.HasNativeResource(parent));

        BufferUav tableView = backend.CreateBufferUav(
            device,
            new BufferUavDesc(
                parent,
                BufferRange.Whole,
                StructureStride: 16,
                CounterBuffer: parent));
        using DescriptorTable table = backend.CreateSingleDescriptorTable(
            device,
            new DescriptorSlotDesc(
                ResourceBindingType.BufferUav,
                StructureStride: 16,
                HasCounter: true),
            ResourceBinding.WritableBuffer(tableView),
            out _);
        parent.Dispose();
        Assert.Equal(0, D3D12PrivateState.DisposeGateState(tableView));
        Assert.True(D3D12PrivateState.HasNativeResource(parent));
        tableView.Dispose();

        device.Dispose();
        backend.Dispose();
        Assert.Null(diagnostics!.TeardownFailure);
        Assert.False(D3D12PrivateState.HasNativeDevice(device));
        Assert.False(D3D12PrivateState.IsRuntimeQuarantined(backend));
    }

    [Fact]
    public void Runtime_release_checkpoint_failure_publishes_device_diagnostic_and_quarantines()
    {
        var failure = new InvalidOperationException("runtime release checkpoint failed");
        var checkpoint = new RecordingRuntimeReleaseCheckpoint(failure);
        var backend = new D3D12Backend(default, checkpoint.Invoke);
        Device device = D3D12TestSupport.CreateWarpDevice(backend);
        Assert.True(backend.TryGetCapability(device, out D3D12Diagnostics? diagnostics));

        backend.Dispose();

        Assert.Equal(1, checkpoint.CallCount);
        Assert.True(checkpoint.ObservedLoaderAuthorities);
        Assert.Same(failure, diagnostics!.TeardownFailure);
        Assert.True(D3D12PrivateState.IsRuntimeQuarantined(backend));
    }

    [Fact]
    public void Runtime_release_checkpoint_preserves_an_earlier_teardown_diagnostic()
    {
        var first = new InvalidOperationException("first teardown failure");
        var later = new InvalidOperationException("later runtime checkpoint failure");
        var checkpoint = new RecordingRuntimeReleaseCheckpoint(later);
        var backend = new D3D12Backend(default, checkpoint.Invoke);
        Device device = D3D12TestSupport.CreateWarpDevice(backend);
        Assert.True(backend.TryGetCapability(device, out D3D12Diagnostics? diagnostics));
        _ = D3D12PrivateState.Invoke(device, "RecordReleaseFailure", first);

        backend.Dispose();

        Assert.Equal(1, checkpoint.CallCount);
        Assert.Same(first, diagnostics!.TeardownFailure);
        Assert.True(D3D12PrivateState.IsRuntimeQuarantined(backend));
    }

    [Fact]
    public void Runtime_release_checkpoint_runs_once_on_normal_teardown()
    {
        var checkpoint = new RecordingRuntimeReleaseCheckpoint();
        var backend = new D3D12Backend(default, checkpoint.Invoke);
        using Device device = D3D12TestSupport.CreateWarpDevice(backend);

        backend.Dispose();
        backend.Dispose();

        Assert.Equal(1, checkpoint.CallCount);
        Assert.True(checkpoint.ObservedLoaderAuthorities);
        Assert.False(D3D12PrivateState.IsRuntimeQuarantined(backend));
    }

    [Fact]
    public void Validated_queue_lock_is_invalidated_before_parent_physical_release()
    {
        var direct = new D3D12Backend();
        var backend = new ValidationLayer(direct);
        Device device = D3D12TestSupport.CreateWarpDevice(backend);
        Queue queue = backend.GetQueue(device, QueueType.Copy);
        using var held = new ManualResetEventSlim();
        using var invalidated = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        Exception? holderFailure = null;
        Thread holder = new(() =>
        {
            try
            {
                D3D12CommandQueueLock value = backend.LockCommandQueue(queue);
                held.Set();
                Assert.True(SpinWait.SpinUntil(
                    () => device.Status == DeviceStatus.Disposed,
                    TimeSpan.FromSeconds(5)));
                Assert.False(value.IsHeld);
                AssertQueueLockPointerUnavailable(value);
                invalidated.Set();
                release.Wait();
                value.Dispose();
            }
            catch (Exception exception)
            {
                holderFailure = exception;
                invalidated.Set();
            }
        });
        Thread parent = new(backend.Dispose);

        holder.Start();
        try
        {
            Assert.True(held.Wait(TimeSpan.FromSeconds(5)));
            parent.Start();
            Assert.True(invalidated.Wait(TimeSpan.FromSeconds(5)));
            Assert.Null(holderFailure);
            Assert.True(parent.IsAlive);
            release.Set();
            Assert.True(holder.Join(TimeSpan.FromSeconds(10)));
            Assert.True(parent.Join(TimeSpan.FromSeconds(10)));
            Assert.Equal(DeviceStatus.Disposed, device.Status);
        }
        finally
        {
            release.Set();
            _ = holder.Join(TimeSpan.FromSeconds(5));
            if (parent.ThreadState != ThreadState.Unstarted)
                _ = parent.Join(TimeSpan.FromSeconds(5));
            backend.Dispose();
        }
    }

    private sealed class BlockingGraphicsObject : GraphicsObject
    {
        private readonly ManualResetEventSlim _entered;
        private readonly ManualResetEventSlim _release;

        internal BlockingGraphicsObject(
            ManualResetEventSlim entered,
            ManualResetEventSlim release)
            : base(null)
        {
            _entered = entered;
            _release = release;
        }

        internal int ReleaseCount { get; private set; }

        internal override void Release(bool fromParent)
        {
            ReleaseCount++;
            Dispose();
            _entered.Set();
            _release.Wait();
        }
    }

    private sealed class RecordingRuntimeReleaseCheckpoint
    {
        private readonly Exception? _failure;

        internal RecordingRuntimeReleaseCheckpoint(Exception? failure = null) =>
            _failure = failure;

        internal int CallCount { get; private set; }
        internal bool ObservedLoaderAuthorities { get; private set; }

        internal void Invoke(D3D12Backend backend)
        {
            CallCount++;
            ObservedLoaderAuthorities =
                D3D12PrivateState.GetField(backend, "_d3d12").GetValue(backend) is not null &&
                D3D12PrivateState.GetField(backend, "_dxgi").GetValue(backend) is not null;
            if (_failure is not null)
                throw _failure;
        }
    }

    private sealed class BlockingRecordedCommandsLease : RecordedCommandsLease
    {
        private readonly ManualResetEventSlim _entered;
        private readonly ManualResetEventSlim _release;
        private RecordedCommands _copy;

        internal BlockingRecordedCommandsLease(
            Device device,
            Queue queue,
            ManualResetEventSlim entered,
            ManualResetEventSlim release)
            : base(device, queue)
        {
            _entered = entered;
            _release = release;
        }

        internal int DiscardCount { get; private set; }

        internal RecordedCommands Create(ulong sequence)
        {
            Activate(sequence);
            _copy = new RecordedCommands(this, sequence);
            return _copy;
        }

        protected override void DiscardUnsubmitted(ulong sequence)
        {
            DiscardCount++;
            _copy.Dispose();
            _entered.Set();
            _release.Wait();
        }
    }

    private sealed class ThrowingGraphicsObject : GraphicsObject
    {
        internal ThrowingGraphicsObject() : base(null)
        {
        }

        internal int ReleaseCount { get; private set; }
        internal int DiagnosticCount { get; private set; }

        internal override void Release(bool fromParent)
        {
            ReleaseCount++;
            throw new InvalidOperationException("release failed");
        }

        internal override void RecordReleaseFailure(Exception exception)
        {
            DiagnosticCount++;
            throw new InvalidOperationException("diagnostic publication failed");
        }
    }

    private sealed class RegistryChild : GraphicsObject
    {
        private readonly GraphicsObjectRegistry _registry;
        private readonly bool _throws;

        internal RegistryChild(GraphicsObjectRegistry registry, bool throws)
            : base(null)
        {
            _registry = registry;
            _throws = throws;
        }

        internal int ReleaseCount { get; private set; }

        internal override void Release(bool fromParent)
        {
            ReleaseCount++;
            if (_throws)
                throw new InvalidOperationException("registry child release failed");
            _registry.Remove(this);
        }
    }

    private sealed class ThrowingDeviceChild : DeviceResource
    {
        private readonly Exception _failure;

        internal ThrowingDeviceChild(Device device, Exception failure)
            : base(device, null) => _failure = failure;

        internal int ReleaseCount { get; private set; }

        internal override void Release(bool fromParent)
        {
            ReleaseCount++;
            throw _failure;
        }
    }

    private sealed unsafe class BlockingQueueLockLease : D3D12CommandQueueLockLease
    {
        private readonly QueueExclusion _exclusion;
        private readonly ManualResetEventSlim _entered;
        private readonly ManualResetEventSlim _release;

        internal BlockingQueueLockLease(
            QueueExclusion exclusion,
            ulong sequence,
            ManualResetEventSlim entered,
            ManualResetEventSlim release)
        {
            _exclusion = exclusion;
            _entered = entered;
            _release = release;
            Activate(sequence);
        }

        internal int ReleaseCount { get; private set; }

        protected override Silk.NET.Direct3D12.ID3D12CommandQueue* GetPointerCore() => null;

        protected override void ReleaseCore()
        {
            ReleaseCount++;
            _entered.Set();
            _release.Wait();
            _exclusion.Exit();
        }
    }

    private sealed class BlockingThrowingSink : IValidationMessageSink
    {
        private readonly ManualResetEventSlim _entered;
        private readonly ManualResetEventSlim _release;

        internal BlockingThrowingSink(
            ManualResetEventSlim entered,
            ManualResetEventSlim release)
        {
            _entered = entered;
            _release = release;
        }

        internal int ReportCount { get; private set; }

        public void Report(in ValidationMessage message)
        {
            if (message.Area != "Lifetime")
                return;

            ReportCount++;
            _entered.Set();
            _release.Wait();
            throw new InvalidOperationException("The diagnostic sink failed during teardown.");
        }
    }

    private static Device CreateWarpDevice(IGraphicsBackend graphics)
    {
        AdapterEnumerationOptions options = new(
            AdapterPreference.HighPerformance,
            IncludeSoftware: true);
        _ = graphics.TryEnumerateAdapters(options, [], out int count);
        var adapters = new AdapterInfo[count];
        Assert.True(graphics.TryEnumerateAdapters(options, adapters, out int confirmed));
        Assert.Equal(count, confirmed);
        AdapterInfo warp = Assert.Single(adapters, static value => !value.HardwareAccelerated);
        DeviceQueueDesc[] queues = [new(QueueType.Copy)];
        return graphics.CreateDevice(new DeviceDesc(
            warp.Id,
            queues));
    }
    [Fact]
    public void Device_disposal_discards_payload_after_its_context_was_disposed()
    {
        using IGraphicsBackend backend = new D3D12Backend();
        Device device = D3D12TestSupport.CreateWarpDevice(backend);
        CommandContext context = backend.CreateCommandContext(
            device,
            new CommandContextDesc(QueueType.Copy, 0, 1));

        backend.Begin(context, default);
        RecordedCommands commands = backend.End(context);
        context.Dispose();
        Assert.Equal(RecordedCommandsStatus.Executable, commands.Status);

        device.Dispose();

        Assert.Equal(DeviceStatus.Disposed, device.Status);
        Assert.Equal(RecordedCommandsStatus.Discarded, commands.Status);
        commands.Dispose();
        Assert.Equal(RecordedCommandsStatus.Disposed, commands.Status);
    }

    [Fact]
    public void Device_disposal_joins_a_caller_discard_already_releasing_its_payload()
    {
        var backend = new D3D12Backend();
        Device device = D3D12TestSupport.CreateWarpDevice(backend);
        Assert.True(backend.TryGetCapability(device, out D3D12Diagnostics? diagnostics));
        using CommandContext context = backend.CreateCommandContext(
            device,
            new CommandContextDesc(QueueType.Copy, 0, 1));
        backend.Begin(context, default);
        RecordedCommands commands = backend.End(context);
        var commandPayloadGate = (System.Threading.Lock)D3D12PrivateState
            .GetField(device, "_commandPayloadGate")
            .GetValue(device)!;
        using var callerStarted = new ManualResetEventSlim();
        using var joinerStarted = new ManualResetEventSlim();
        Exception? callerFailure = null;
        Exception? joinerFailure = null;
        var caller = new Thread(() =>
        {
            callerStarted.Set();
            try
            {
                commands.Dispose();
            }
            catch (Exception exception)
            {
                callerFailure = exception;
            }
        });
        var joiner = new Thread(() =>
        {
            joinerStarted.Set();
            try
            {
                _ = D3D12PrivateState.Invoke(commands.Lease, "DiscardExecutableFromDevice");
            }
            catch (Exception exception)
            {
                joinerFailure = exception;
            }
        });

        using (commandPayloadGate.EnterScope())
        {
            caller.Start();
            Assert.True(callerStarted.Wait(TimeSpan.FromSeconds(5)));
            Assert.True(SpinWait.SpinUntil(
                () => (int)D3D12PrivateState.GetField(commands.Lease, "_callerReleaseState")
                    .GetValue(commands.Lease)! == 1,
                TimeSpan.FromSeconds(5)));
            joiner.Start();
            Assert.True(joinerStarted.Wait(TimeSpan.FromSeconds(5)));
            Assert.True(SpinWait.SpinUntil(
                () => (joiner.ThreadState & ThreadState.WaitSleepJoin) != 0,
                TimeSpan.FromSeconds(5)));
            Assert.True(caller.IsAlive);
            Assert.True(joiner.IsAlive);
        }

        Assert.True(caller.Join(TimeSpan.FromSeconds(10)));
        Assert.True(joiner.Join(TimeSpan.FromSeconds(10)));
        Assert.Null(callerFailure);
        Assert.Null(joinerFailure);
        Assert.Equal(RecordedCommandsStatus.Disposed, commands.Status);

        device.Dispose();
        Assert.Null(diagnostics!.TeardownFailure);
        Assert.False(D3D12PrivateState.HasNativeDevice(device));
        backend.Dispose();
        Assert.False(D3D12PrivateState.IsRuntimeQuarantined(backend));
    }

    [Fact]
    public void Device_loss_racing_recorded_commands_and_recording_context_disposal_does_not_invert_gates()
    {
        using (var backend = new D3D12Backend())
        {
            using Device device = D3D12TestSupport.CreateWarpDevice(backend);
            using CommandContext context = backend.CreateCommandContext(
                device,
                new CommandContextDesc(QueueType.Copy, 0, 1));
            backend.Begin(context, default);
            RecordedCommands commands = backend.End(context);
            object leaseGate = D3D12PrivateState.GetField(commands.Lease, "_gate")
                .GetValue(commands.Lease)!;
            using var disposeStarted = new ManualResetEventSlim();
            using var lossStarted = new ManualResetEventSlim();
            Exception? disposeFailure = null;
            Exception? lossFailure = null;
            var disposer = new Thread(() =>
            {
                disposeStarted.Set();
                try
                {
                    commands.Dispose();
                }
                catch (Exception exception)
                {
                    disposeFailure = exception;
                }
            });
            var loser = new Thread(() =>
            {
                lossStarted.Set();
                try
                {
                    D3D12PrivateState.MarkSoftwareLost(device);
                }
                catch (Exception exception)
                {
                    lossFailure = exception;
                }
            });

            Monitor.Enter(leaseGate);
            try
            {
                disposer.Start();
                loser.Start();
                Assert.True(disposeStarted.Wait(TimeSpan.FromSeconds(5)));
                Assert.True(lossStarted.Wait(TimeSpan.FromSeconds(5)));
                Assert.True(disposer.IsAlive);
                Assert.True(loser.IsAlive);
            }
            finally
            {
                Monitor.Exit(leaseGate);
            }

            Assert.True(disposer.Join(TimeSpan.FromSeconds(10)));
            Assert.True(loser.Join(TimeSpan.FromSeconds(10)));
            Assert.Null(disposeFailure);
            Assert.Null(lossFailure);
            Assert.Equal(DeviceStatus.Lost, device.Status);
            Assert.Equal(RecordedCommandsStatus.Disposed, commands.Status);
            D3D12PrivateState.ConfirmNativeDeviceLoss(device);
        }

        using (var backend = new D3D12Backend())
        {
            using Device device = D3D12TestSupport.CreateWarpDevice(backend);
            CommandContext context = backend.CreateCommandContext(
                device,
                new CommandContextDesc(QueueType.Copy, 0, 1));
            backend.Begin(context, default);
            object contextGate = D3D12PrivateState.GetField(context, "_gate")
                .GetValue(context)!;
            using var disposeStarted = new ManualResetEventSlim();
            using var lossStarted = new ManualResetEventSlim();
            Exception? disposeFailure = null;
            Exception? lossFailure = null;
            var disposer = new Thread(() =>
            {
                disposeStarted.Set();
                try
                {
                    context.Dispose();
                }
                catch (Exception exception)
                {
                    disposeFailure = exception;
                }
            });
            var loser = new Thread(() =>
            {
                lossStarted.Set();
                try
                {
                    D3D12PrivateState.MarkSoftwareLost(device);
                }
                catch (Exception exception)
                {
                    lossFailure = exception;
                }
            });

            Monitor.Enter(contextGate);
            try
            {
                disposer.Start();
                loser.Start();
                Assert.True(disposeStarted.Wait(TimeSpan.FromSeconds(5)));
                Assert.True(lossStarted.Wait(TimeSpan.FromSeconds(5)));
                Assert.True(disposer.IsAlive);
                Assert.True(loser.IsAlive);
            }
            finally
            {
                Monitor.Exit(contextGate);
            }

            Assert.True(disposer.Join(TimeSpan.FromSeconds(10)));
            Assert.True(loser.Join(TimeSpan.FromSeconds(10)));
            Assert.Null(disposeFailure);
            Assert.Null(lossFailure);
            Assert.Equal(DeviceStatus.Lost, device.Status);
            Assert.Equal(2, D3D12PrivateState.DisposeGateState(context));
            D3D12PrivateState.ConfirmNativeDeviceLoss(device);
        }
    }

    [Fact]
    public void Device_disposal_cascades_and_is_concurrent_with_every_WARP_child_family()
    {
        const string source = """
            [shader("compute")]
            [numthreads(1, 1, 1)]
            void computeMain(uint3 dispatchThread : SV_DispatchThreadID)
            {
            }

            """;
        D3D12TestShaderEntry[] entries =
        [
            new("computeMain", SlangStage.Compute),
        ];
        using D3D12TestShaderProgram shader = D3D12TestShaderProgram.Compile(
            "rhi_lifetime_matrix",
            source,
            entries);
        using IGraphicsBackend backend = new D3D12Backend();
        Device device = D3D12TestSupport.CreateWarpDevice(backend);
        var children = new List<GraphicsObject>();
        D3D12TestShaderProgram? rayShader = null;

        BufferDesc placedDescription = new(
            4_096,
            BufferUsages.ShaderRead | BufferUsages.CopyDestination);
        MemoryRequirements requirements = backend.GetBufferMemoryRequirements(
            device,
            placedDescription);
        Heap heap = Keep(backend.CreateHeap(
            device,
            new HeapDesc(
                requirements.Size,
                requirements.Alignment,
                MemoryType.DeviceLocal,
                requirements.CompatibleHeapFlags)));
        _ = Keep(backend.CreatePlacedBuffer(device, heap, 0, placedDescription));

        Buffer buffer = Keep(backend.CreateBuffer(
            device,
            new BufferDesc(
                4_096,
                BufferUsages.Constant |
                BufferUsages.ShaderRead |
                BufferUsages.ShaderWrite |
                BufferUsages.CopySource |
                BufferUsages.CopyDestination)));
        Buffer counter = Keep(backend.CreateBuffer(
            device,
            new BufferDesc(4_096, BufferUsages.ShaderWrite)));
        BufferCbv cbv = Keep(backend.CreateBufferCbv(
            device,
            new BufferCbvDesc(buffer, new BufferRange(0, 256))));
        BufferSrv bufferSrv = Keep(backend.CreateBufferSrv(
            device,
            new BufferSrvDesc(buffer, BufferRange.Whole, Format.R32UInt)));
        _ = Keep(backend.CreateBufferUav(
            device,
            new BufferUavDesc(
                buffer,
                BufferRange.Whole,
                StructureStride: 16,
                CounterBuffer: counter)));

        Texture color = Keep(backend.CreateTexture(
            device,
            new TextureDesc(
                TextureDimension.Texture2D,
                16,
                16,
                1,
                1,
                1,
                1,
                Format.R8G8B8A8UNorm,
                TextureUsages.Sampled |
                TextureUsages.Storage |
                TextureUsages.ColorAttachment)));
        TextureSubresourceRange colorRange = new(0, 1, 0, 1, TextureAspects.Color);
        TextureSrv textureSrv = Keep(backend.CreateTextureSrv(
            device,
            new TextureSrvDesc(
                color,
                colorRange,
                Format.R8G8B8A8UNorm,
                TextureViewDimension.Texture2D)));
        _ = Keep(backend.CreateTextureUav(
            device,
            new TextureUavDesc(
                color,
                colorRange,
                Format.R8G8B8A8UNorm,
                TextureViewDimension.Texture2D)));
        _ = Keep(backend.CreateColorAttachmentView(
            device,
            new ColorAttachmentViewDesc(
                color,
                colorRange,
                Format.R8G8B8A8UNorm,
                TextureViewDimension.Texture2D)));

        Texture depth = Keep(backend.CreateTexture(
            device,
            new TextureDesc(
                TextureDimension.Texture2D,
                16,
                16,
                1,
                1,
                1,
                1,
                Format.D32Float,
                TextureUsages.DepthStencilAttachment)));
        _ = Keep(backend.CreateDepthStencilView(
            device,
            new DepthStencilViewDesc(
                depth,
                new TextureSubresourceRange(0, 1, 0, 1, TextureAspects.Depth),
                Format.D32Float,
                TextureViewDimension.Texture2D)));

        SamplerDesc samplerDescription = new(
            FilterType.Nearest,
            FilterType.Nearest,
            FilterType.Nearest,
            AddressType.ClampToEdge,
            AddressType.ClampToEdge,
            AddressType.ClampToEdge);
        Sampler sampler = Keep(backend.CreateSampler(device, samplerDescription));
        DescriptorTable resourceDescriptors = Keep(backend.CreateDescriptorTable(
            device,
            [ResourceBindingType.BufferSrv, ResourceBindingType.TextureSrv]));
        backend.WriteDescriptor(resourceDescriptors, 0, ResourceBinding.ReadOnlyBuffer(bufferSrv));
        backend.WriteDescriptor(resourceDescriptors, 1, ResourceBinding.SampledTexture(textureSrv));
        DescriptorTable samplerDescriptors = Keep(backend.CreateDescriptorTable(
            device,
            [ResourceBindingType.Sampler, ResourceBindingType.Sampler]));
        backend.WriteDescriptor(samplerDescriptors, 0, ResourceBinding.SampledWith(sampler));
        backend.WriteDescriptor(samplerDescriptors, 1, ResourceBinding.SampledWith(sampler));
        _ = Keep(backend.CreatePipelineCache(device, default));
        _ = Keep(backend.CreateQueryPool(
            device,
            new QueryPoolDesc(QueryType.Timestamp, QueueType.Graphics, 2)));

        Pipeline computePipeline = Keep(backend.CreateComputePipeline(
            device,
            new ComputePipelineDesc(shader.Program, shader.GetEntryPoint(0))));
        VariableLayoutReflection globalLayout =
            shader.Reflection.GetGlobalParamsVarLayout() ?? VariableLayoutReflection.Null;
        Assert.NotEqual(VariableLayoutReflection.Null, globalLayout);
        _ = Keep(backend.CreatePersistentParameterBindings(
            device,
            computePipeline,
            new ParameterBlockBindings(globalLayout, [], [])));

        CommandContext context = Keep(backend.CreateCommandContext(
            device,
            new CommandContextDesc(QueueType.Graphics, 0, 1)));
        backend.Begin(context, default);
        RecordedCommands executable = backend.End(context);
        CommandContext bundleContext = Keep(backend.CreateCommandContext(
            device,
            new CommandContextDesc(QueueType.Graphics, 0, 1, Bundle: true)));
        backend.Begin(bundleContext);
        _ = Keep(backend.EndBundle(bundleContext));

        _ = Keep(backend.CreateExternalTimeline(device, 7));
        IndirectArgumentDesc[] indirectArguments = [new(IndirectArgumentType.Draw)];
        _ = Keep(backend.CreateIndirectCommandLayout(
            device,
            new IndirectCommandLayoutDesc(indirectArguments, 16)));

        if (backend.TryGetCapability(device, out SparseResources? sparse) && sparse is not null)
        {
            _ = Keep(backend.CreateReservedBuffer(
                device,
                new BufferDesc(65_536, BufferUsages.CopyDestination)));
        }

        if (backend.TryGetCapability(device, out RayTracing? rayTracing) && rayTracing is not null)
        {
            ulong storageSize = Math.Max(65_536UL, rayTracing.AccelerationStructureAlignment);
            storageSize = checked(
                (storageSize + rayTracing.AccelerationStructureAlignment - 1) /
                rayTracing.AccelerationStructureAlignment *
                rayTracing.AccelerationStructureAlignment);
            Buffer storage = Keep(backend.CreateBuffer(
                device,
                new BufferDesc(storageSize, BufferUsages.AccelerationStructure)));
            AccelerationStructure structure = Keep(backend.CreateAccelerationStructure(
                device,
                storage,
                BufferRange.Whole,
                AccelerationStructureType.BottomLevel));
            AccelerationStructureSrv structureSrv = Keep(backend.CreateAccelerationStructureSrv(
                device,
                new AccelerationStructureSrvDesc(structure)));
            DescriptorTable structureDescriptors = Keep(backend.CreateDescriptorTable(
                device,
                [ResourceBindingType.AccelerationStructure]));
            backend.WriteDescriptor(
                structureDescriptors,
                0,
                ResourceBinding.AccelerationStructure(structureSrv));

            const string raySource = """
                [shader("raygeneration")]
                void rayGenerationMain()
                {
                }
                """;
            D3D12TestShaderEntry[] rayEntries =
            [
                new("rayGenerationMain", SlangStage.RayGeneration),
            ];
            rayShader = D3D12TestShaderProgram.Compile(
                "rhi_lifetime_ray_table",
                raySource,
                rayEntries);
            EntryPointReflection[] rayGeneration = [rayShader.GetEntryPoint(0)];
            Pipeline rayPipeline = Keep(backend.CreateRayTracingPipeline(
                device,
                new RayTracingPipelineDesc(
                    rayShader.Program,
                    rayGeneration,
                    [],
                    [],
                    [],
                    1,
                    0,
                    8)));
            _ = Keep(backend.CreateRayTracingShaderTable(
                device,
                new RayTracingShaderTableDesc(rayPipeline, 1, 0, 0, 0, 32)));
        }

        var releases = new List<Action>(checked(children.Count * 2 + 2));
        foreach (GraphicsObject child in children)
        {
            releases.Add(child.Dispose);
            releases.Add(child.Dispose);
        }
        releases.Add(device.Dispose);
        releases.Add(device.Dispose);
        Parallel.Invoke(releases.ToArray());

        Assert.Equal(DeviceStatus.Disposed, device.Status);
        foreach (GraphicsObject child in children)
        {
            Assert.True(
                child.IsDisposed,
                $"{child.GetType().Name} remained live after the concurrent Device cascade.");
            child.Dispose();
        }
        Assert.Equal(RecordedCommandsStatus.Discarded, executable.Status);
        executable.Dispose();
        executable.Dispose();
        Assert.Equal(RecordedCommandsStatus.Disposed, executable.Status);
        Assert.Equal(PipelineType.Compute, computePipeline.Type);
        rayShader?.Dispose();

        T Keep<T>(T value)
            where T : GraphicsObject
        {
            children.Add(value);
            return value;
        }
    }

    [Fact]
    public void Surface_or_device_disposal_invalidates_swapchain_images_and_is_idempotent()
    {
        using D3D12TestWindow window = new();
        using IGraphicsBackend backend = new D3D12Backend();
        Device device = D3D12TestSupport.CreateWarpDevice(backend);
        Surface surface = backend.CreateSurface(new SurfaceDesc(
            NativeWindowType.Win32,
            window.Handle));
        SwapchainConfig config = new(
            32,
            32,
            Format.R8G8B8A8UNorm,
            ColorSpace.Srgb,
            PresentType.Mailbox,
            AllowTearing: false,
            MaximumFrameLatency: 2);
        Swapchain swapchain = backend.CreateSwapchain(
            device,
            new SwapchainDesc(
                surface,
                2,
                TextureUsages.ColorAttachment,
                config));
        Assert.Equal(
            SwapchainAcquireStatus.Success,
            backend.Acquire(
                swapchain,
                new SwapchainAcquireOptions(TimeSpan.FromSeconds(2)),
                out SwapchainImage image));
        Texture imageTexture = image.Texture;

        Parallel.Invoke(
            surface.Dispose,
            surface.Dispose,
            swapchain.Dispose,
            swapchain.Dispose,
            device.Dispose,
            device.Dispose);

        Assert.True(surface.IsDisposed);
        Assert.True(swapchain.IsDisposed);
        Assert.True(imageTexture.IsDisposed);
        Assert.Equal(DeviceStatus.Disposed, device.Status);
        Assert.Equal(SwapchainImageStatus.Invalidated, image.Status);
        Assert.Throws<InvalidOperationException>(() => _ = image.Texture);
        imageTexture.Dispose();
        swapchain.Dispose();
        surface.Dispose();
    }

    [Fact]
    public void External_handle_disposal_is_concurrent_terminal_and_keeps_metadata()
    {
        using IGraphicsBackend backend = new D3D12Backend();
        using Device device = D3D12TestSupport.CreateWarpDevice(backend);
        using ExternalTimeline timeline = backend.CreateExternalTimeline(device, 1);
        ExternalHandle handle = backend.ExportTimeline(
            timeline,
            ExternalHandleType.OpaqueWin32);

        Parallel.For(0, 32, _ => handle.Dispose());

        Assert.Equal(ExternalHandleType.OpaqueWin32, handle.Type);
        Assert.Throws<ObjectDisposedException>(() => _ = handle.Value);
        handle.Dispose();
    }

    [Fact]
    public void Replacing_the_owning_root_fully_closes_the_old_runtime_before_the_new_one()
    {
        var oldBackend = new D3D12Backend();
        Device oldDevice = CreateWarpDevice(oldBackend);
        Buffer oldBuffer = oldBackend.CreateBuffer(
            oldDevice,
            new BufferDesc(64, BufferUsages.CopySource),
            MemoryType.Upload);

        oldBackend.Dispose();
        oldBackend.Dispose();

        Assert.False(D3D12PrivateState.IsRuntimeQuarantined(oldBackend));
        Assert.Equal(DeviceStatus.Disposed, oldDevice.Status);
        Assert.True(oldBuffer.IsDisposed);
        Assert.Throws<ObjectDisposedException>(() => oldBackend.TryEnumerateAdapters(
            new AdapterEnumerationOptions(IncludeSoftware: true),
            [],
            out _));

        using var newBackend = new D3D12Backend();
        using Device newDevice = CreateWarpDevice(newBackend);
        using Buffer newBuffer = newBackend.CreateBuffer(
            newDevice,
            new BufferDesc(64, BufferUsages.CopySource),
            MemoryType.Upload);
        Assert.Equal(DeviceStatus.Active, newDevice.Status);
        Assert.NotSame(oldDevice, newDevice);
        Assert.NotSame(oldBuffer, newBuffer);
        Assert.False(D3D12PrivateState.IsRuntimeQuarantined(newBackend));
        newBackend.Dispose();
        Assert.False(D3D12PrivateState.IsRuntimeQuarantined(newBackend));

        static Device CreateWarpDevice(IGraphicsBackend graphics)
        {
            AdapterEnumerationOptions options = new(
                AdapterPreference.HighPerformance,
                IncludeSoftware: true);
            _ = graphics.TryEnumerateAdapters(options, [], out int count);
            var adapters = new AdapterInfo[count];
            Assert.True(graphics.TryEnumerateAdapters(options, adapters, out int confirmed));
            Assert.Equal(count, confirmed);
            AdapterInfo warp = Assert.Single(adapters, static value => !value.HardwareAccelerated);
            DeviceQueueDesc[] queues = [new(QueueType.Copy)];
            return graphics.CreateDevice(new DeviceDesc(
                warp.Id,
                    queues));
        }
    }

    [Fact]
    public void Heap_and_device_disposal_cascade_once_through_descendants()
    {
        using IGraphicsBackend backend = new D3D12Backend();
        Device device = D3D12TestSupport.CreateWarpDevice(backend);
        BufferDesc description = new(
            256,
            BufferUsages.ShaderRead | BufferUsages.CopyDestination,
            "placed lifetime buffer");
        MemoryRequirements requirements = backend.GetBufferMemoryRequirements(device, description);
        Heap heap = backend.CreateHeap(device, new HeapDesc(
            requirements.Size,
            requirements.Alignment,
            MemoryType.DeviceLocal,
            requirements.CompatibleHeapFlags));
        Buffer buffer = backend.CreatePlacedBuffer(device, heap, 0, description);
        BufferSrv view = backend.CreateBufferSrv(
            device,
            new BufferSrvDesc(buffer, BufferRange.Whole, Format.R32UInt));

        Parallel.For(0, 32, _ => heap.Dispose());

        Assert.True(heap.IsDisposed);
        Assert.False(buffer.IsDisposed);
        Assert.False(view.IsDisposed);
        Assert.NotEqual(0, D3D12PrivateState.NativeHeapPointer(heap));
        view.Dispose();
        buffer.Dispose();
        heap.Dispose();
        Assert.Equal(0, D3D12PrivateState.NativeHeapPointer(heap));

        Sampler sampler = backend.CreateSampler(
            device,
            new SamplerDesc(
                FilterType.Nearest,
                FilterType.Nearest,
                FilterType.Nearest,
                AddressType.ClampToEdge,
                AddressType.ClampToEdge,
                AddressType.ClampToEdge));
        device.Dispose();
        device.Dispose();

        Assert.Equal(DeviceStatus.Disposed, device.Status);
        Assert.True(sampler.IsDisposed);
        sampler.Dispose();
    }

    [Fact]
    public void View_retains_resource_native_state_exactly_until_view_disposal()
    {
        using var backend = new D3D12Backend();
        using Device device = D3D12TestSupport.CreateWarpDevice(backend);
        using Buffer buffer = backend.CreateBuffer(
            device,
            new BufferDesc(4_096, BufferUsages.ShaderRead),
            MemoryType.DeviceLocal);

        Assert.Equal(1, D3D12PrivateState.NativeLeaseReferenceCount(buffer));
        BufferSrv view = backend.CreateBufferSrv(
            device,
            new BufferSrvDesc(buffer, BufferRange.Whole, Format.R32UInt));
        Assert.Equal(2, D3D12PrivateState.NativeLeaseReferenceCount(buffer));

        view.Dispose();
        Assert.Equal(1, D3D12PrivateState.NativeLeaseReferenceCount(buffer));
        view.Dispose();
        Assert.Equal(1, D3D12PrivateState.NativeLeaseReferenceCount(buffer));
    }

    [Fact]
    public unsafe void NativeLease_construction_rolls_back_owned_pointer_and_dependencies()
    {
        using var backend = new D3D12Backend();
        using Device device = D3D12TestSupport.CreateWarpDevice(backend);
        using Buffer buffer = backend.CreateBuffer(
            device,
            new BufferDesc(4_096, BufferUsages.ShaderRead),
            MemoryType.DeviceLocal);

        object sourceLease = D3D12PrivateState.NativeLeaseObject(buffer);
        Type leaseType = sourceLease.GetType();
        ConstructorInfo[] constructors = leaseType.GetConstructors(
            BindingFlags.Instance | BindingFlags.NonPublic);
        ConstructorInfo singleDependencyConstructor = constructors.Single(
            static constructor => !constructor.GetParameters()[2].ParameterType.IsArray);
        ConstructorInfo dependencyArrayConstructor = constructors.Single(
            static constructor => constructor.GetParameters()[2].ParameterType.IsArray);
        MethodInfo release = leaseType.GetMethod(
            "Release",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        Type pointerType = singleDependencyConstructor.GetParameters()[0].ParameterType;
        object nullPointer = Pointer.Box((void*)0, pointerType);
        object liveDependency = singleDependencyConstructor.Invoke(
            [nullPointer, false, null, null]);
        object disposedDependency = singleDependencyConstructor.Invoke(
            [nullPointer, false, null, null]);
        release.Invoke(disposedDependency, null);

        ID3D12Resource* native = backend.GetNativeResource(buffer);
        uint baseline = ProbeReferenceCount(native);
        try
        {
            _ = native->AddRef();
            TargetInvocationException singleFailure = Assert.Throws<TargetInvocationException>(
                () => singleDependencyConstructor.Invoke(
                    [Pointer.Box(native, pointerType), true, disposedDependency, null]));
            Assert.IsType<ObjectDisposedException>(singleFailure.InnerException);
            Assert.Equal(baseline, ProbeReferenceCount(native));

            Array dependencies = Array.CreateInstance(leaseType, 2);
            dependencies.SetValue(liveDependency, 0);
            dependencies.SetValue(disposedDependency, 1);
            _ = native->AddRef();
            TargetInvocationException arrayFailure = Assert.Throws<TargetInvocationException>(
                () => dependencyArrayConstructor.Invoke(
                    [Pointer.Box(native, pointerType), true, dependencies]));
            Assert.IsType<ObjectDisposedException>(arrayFailure.InnerException);
            Assert.Equal(1, D3D12PrivateState.NativeLeaseReferenceCount(liveDependency));
            Assert.Equal(baseline, ProbeReferenceCount(native));
        }
        finally
        {
            release.Invoke(liveDependency, null);
        }

        static uint ProbeReferenceCount(ID3D12Resource* value)
        {
            uint count = value->AddRef();
            _ = value->Release();
            return count;
        }
    }

    [Fact]
    public void Backend_disposal_cascades_the_complete_device_tree()
    {
        var backend = new D3D12Backend();
        Device device = D3D12TestSupport.CreateWarpDevice(backend);
        Buffer buffer = backend.CreateBuffer(
            device,
            new BufferDesc(64, BufferUsages.CopySource),
            MemoryType.Upload);

        backend.Dispose();
        backend.Dispose();

        Assert.Equal(DeviceStatus.Disposed, device.Status);
        Assert.True(buffer.IsDisposed);
        buffer.Dispose();
        device.Dispose();
    }

    [Fact]
    public void Automatic_submission_retains_native_payload_after_public_owners_dispose()
    {
        using IGraphicsBackend backend = new D3D12Backend();
        using Device device = D3D12TestSupport.CreateWarpDevice(backend);
        byte[] expected = Enumerable.Range(0, 257)
            .Select(static value => unchecked((byte)(value * 43 + 7)))
            .ToArray();
        Buffer upload = backend.CreateBuffer(
            device,
            new BufferDesc((ulong)expected.Length, BufferUsages.CopySource),
            MemoryType.Upload);
        using Buffer readback = backend.CreateBuffer(
            device,
            new BufferDesc((ulong)expected.Length, BufferUsages.CopyDestination),
            MemoryType.Readback);
        BufferRange range = new(0, (ulong)expected.Length);
        using (MappedBuffer mapping = backend.Map(upload, MapType.Write, range))
        {
            expected.CopyTo(mapping.Bytes);
            mapping.Flush(range);
        }

        CommandContext context = backend.CreateCommandContext(
            device,
            new CommandContextDesc(QueueType.Copy, 0, 1));
        backend.Begin(context, default);
        backend.CopyBuffer(context, new BufferCopy(upload, 0, readback, 0, range.Size));
        RecordedCommands recorded = backend.End(context);
        RecordedCommands copied = recorded;
        context.Dispose();
        upload.Dispose();

        RecordedCommands[] commands = [recorded];
        Queue queue = backend.GetQueue(device, QueueType.Copy);
        QueueCompletion completion = backend.Submit(
            queue,
            new QueueSubmitDesc([], [], commands, [], []));
        RecordedCommands[] duplicate = [copied];
        Assert.Throws<InvalidOperationException>(() => backend.Submit(
            queue,
            new QueueSubmitDesc([], [], duplicate, [], [])));
        recorded.Dispose();
        copied.Dispose();

        Assert.Equal(WaitStatus.Completed, backend.WaitCpu(completion, TimeSpan.FromSeconds(10)));
        backend.CollectCompleted(device);
        byte[] actual = new byte[expected.Length];
        using (MappedBuffer mapping = backend.Map(readback, MapType.Read, range))
        {
            mapping.Invalidate(range);
            mapping.Bytes.CopyTo(actual);
        }
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Recorded_commands_retain_intrinsic_payload_until_completion()
    {
        using IGraphicsBackend backend = new D3D12Backend();
        using Device device = D3D12TestSupport.CreateWarpDevice(backend);
        byte[] expected = Enumerable.Range(0, 257)
            .Select(static value => unchecked((byte)(value * 29 + 11)))
            .ToArray();
        Buffer upload = backend.CreateBuffer(
            device,
            new BufferDesc((ulong)expected.Length, BufferUsages.CopySource),
            MemoryType.Upload);
        Buffer readback = backend.CreateBuffer(
            device,
            new BufferDesc((ulong)expected.Length, BufferUsages.CopyDestination),
            MemoryType.Readback);
        BufferRange range = new(0, (ulong)expected.Length);
        using (MappedBuffer mapping = backend.Map(upload, MapType.Write, range))
        {
            expected.CopyTo(mapping.Bytes);
            mapping.Flush(range);
        }

        CommandContext context = backend.CreateCommandContext(
            device,
            new CommandContextDesc(QueueType.Copy, 0, 1));
        backend.Begin(context, default);
        backend.CopyBuffer(context, new BufferCopy(upload, 0, readback, 0, range.Size));
        RecordedCommands recorded = backend.End(context);

        IList slots = (IList)GetRequiredField(context, "_slots").GetValue(context)!;
        Assert.Single(slots);
        object slot = slots[0]!;
        object captures = GetRequiredField(slot, "_captures").GetValue(slot)!;
        object resources = GetRequiredField(captures, "_resources").GetValue(captures)!;
        int resourceCount = (int)resources.GetType()
            .GetProperty("Count", BindingFlags.Instance | BindingFlags.Public)!
            .GetValue(resources)!;
        Assert.True(resourceCount >= 2);

        context.Dispose();
        QueueCompletion completion = backend.Submit(
            backend.GetQueue(device, QueueType.Copy),
            new QueueSubmitDesc([], [], [recorded], [], []));
        recorded.Dispose();
        upload.Dispose();

        Assert.Equal(WaitStatus.Completed, backend.WaitCpu(completion, TimeSpan.FromSeconds(10)));
        backend.CollectCompleted(device);
        byte[] actual = new byte[expected.Length];
        using (MappedBuffer mapping = backend.Map(readback, MapType.Read, range))
        {
            mapping.Invalidate(range);
            mapping.Bytes.CopyTo(actual);
        }
        Assert.Equal(expected, actual);

        readback.Dispose();
        context.Dispose();
    }

    [Fact]
    public void End_releases_CommandContext_encoding_state_references()
    {
        using IGraphicsBackend backend = new D3D12Backend();
        using Device device = D3D12TestSupport.CreateWarpDevice(backend);
        using Buffer buffer = backend.CreateBuffer(
            device,
            new BufferDesc(
                64,
                BufferUsages.Vertex | BufferUsages.Index | BufferUsages.Predication),
            MemoryType.Upload);
        using CommandContext context = backend.CreateCommandContext(
            device,
            new CommandContextDesc(QueueType.Graphics, 0, 1));

        backend.Begin(context, default);
        backend.SetVertexBuffers(
            context,
            0,
            [new VertexBufferBinding(buffer, 0, 16, 64)]);
        backend.SetIndexBuffer(
            context,
            new IndexBufferBinding(buffer, 0, 64, IndexType.UInt16));
        backend.SetPredication(context, buffer, 0);
        using RecordedCommands commands = backend.End(context);

        VertexBufferBinding[] vertexBuffers =
            (VertexBufferBinding[])GetRequiredField(context, "_vertexBuffers").GetValue(context)!;
        Assert.All(vertexBuffers, static binding => Assert.Equal(default, binding));
        Assert.Equal(
            0u,
            (uint)GetRequiredField(context, "_vertexBufferSetMask").GetValue(context)!);
        Assert.Equal(
            default,
            (IndexBufferBinding)GetRequiredField(context, "_indexBuffer").GetValue(context)!);
        Assert.Null(GetRequiredField(context, "_predication").GetValue(context));
        Assert.Null(GetRequiredField(context, "_pipeline").GetValue(context));
        Assert.Null(GetRequiredField(context, "_shadingRateImage").GetValue(context));
        Assert.Null(GetRequiredField(context, "_workGraphProgram").GetValue(context));
    }

    [Fact]
    public void Reused_command_slot_does_not_resurrect_stale_recorded_command_copies()
    {
        using IGraphicsBackend backend = new D3D12Backend();
        using Device device = D3D12TestSupport.CreateWarpDevice(backend);
        using Buffer upload = backend.CreateBuffer(
            device,
            new BufferDesc(64, BufferUsages.CopySource),
            MemoryType.Upload);
        using Buffer readback = backend.CreateBuffer(
            device,
            new BufferDesc(64, BufferUsages.CopyDestination),
            MemoryType.Readback);
        using CommandContext context = backend.CreateCommandContext(
            device,
            new CommandContextDesc(QueueType.Copy, 0, 1));
        Queue queue = backend.GetQueue(device, QueueType.Copy);
        var commands = new RecordedCommands[1];

        backend.Begin(context, default);
        backend.CopyBuffer(context, new BufferCopy(upload, 0, readback, 0, 64));
        RecordedCommands first = backend.End(context);
        RecordedCommands staleCopy = first;
        commands[0] = first;
        QueueCompletion firstCompletion = backend.Submit(
            queue,
            new QueueSubmitDesc([], [], commands, [], []));
        first.Dispose();
        Assert.Equal(WaitStatus.Completed, backend.WaitCpu(firstCompletion, TimeSpan.FromSeconds(10)));
        backend.CollectCompleted(device);

        backend.Begin(context, default);
        backend.CopyBuffer(context, new BufferCopy(upload, 0, readback, 0, 64));
        RecordedCommands second = backend.End(context);

        Assert.Same(device, staleCopy.Device);
        Assert.Same(queue, staleCopy.Queue);
        Assert.Throws<InvalidOperationException>(() => _ = staleCopy.Status);
        staleCopy.Dispose();

        commands[0] = second;
        QueueCompletion secondCompletion = backend.Submit(
            queue,
            new QueueSubmitDesc([], [], commands, [], []));
        second.Dispose();
        Assert.Equal(WaitStatus.Completed, backend.WaitCpu(secondCompletion, TimeSpan.FromSeconds(10)));
        backend.CollectCompleted(device);
    }

    private static unsafe void AssertQueueLockPointerUnavailable(
        D3D12CommandQueueLock value)
    {
        try
        {
            _ = (nint)value.Pointer;
        }
        catch (InvalidOperationException)
        {
            return;
        }
        throw new Xunit.Sdk.XunitException(
            "An invalid D3D12 command-queue lock exposed its native pointer.");
    }

    private static FieldInfo GetRequiredField(object instance, string name) =>
        instance.GetType().GetField(
            name,
            BindingFlags.Instance | BindingFlags.NonPublic) ??
        throw new InvalidOperationException(
            $"{instance.GetType().FullName} has no non-public field named {name}.");

    private static PropertyInfo GetRequiredProperty(object instance, string name) =>
        instance.GetType().GetProperty(
            name,
            BindingFlags.Instance | BindingFlags.NonPublic) ??
        throw new InvalidOperationException(
            $"{instance.GetType().FullName} has no non-public property named {name}.");
}
