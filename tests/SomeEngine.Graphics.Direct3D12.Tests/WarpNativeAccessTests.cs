using SlangShaderSharp;
using SomeEngine.Graphics.Direct3D12;
using SomeEngine.Graphics.Validation;
using Xunit;

namespace SomeEngine.Graphics.Direct3D12.Tests;

public sealed unsafe class WarpNativeAccessTests
{
    [Fact]
    public void Native_getters_and_command_list_borrow_expose_exact_borrowed_objects_and_dirty_state()
    {
        const string source = """
            [shader("compute")]
            [numthreads(1, 1, 1)]
            void computeMain() {}
            """;
        using D3D12TestShaderProgram shader = D3D12TestShaderProgram.Compile(
            "rhi_native_access_compute",
            source,
            [new D3D12TestShaderEntry("computeMain", SlangStage.Compute)]);
        using D3D12Backend backend = new();
        using Device device = D3D12TestSupport.CreateWarpDevice(backend);
        using Buffer buffer = backend.CreateBuffer(
            device,
            new BufferDesc(256, BufferUsages.CopySource),
            MemoryType.Upload);
        using Texture texture = backend.CreateTexture(
            device,
            new TextureDesc(
                TextureDimension.Texture2D,
                4,
                4,
                1,
                1,
                1,
                1,
                Format.R8G8B8A8UNorm,
                TextureUsages.CopyDestination));
        using Heap heap = backend.CreateHeap(
            device,
            new HeapDesc(65_536, 0, MemoryType.DeviceLocal, HeapFlags.Buffers));
        using QueryPool queryPool = backend.CreateQueryPool(
            device,
            new QueryPoolDesc(QueryType.Timestamp, QueueType.Compute, 1));
        using Pipeline pipeline = backend.CreateComputePipeline(
            device,
            new ComputePipelineDesc(shader.Program, shader.GetEntryPoint(0)));

        Assert.NotEqual(0, (nint)backend.GetNativeDevice(device));
        Assert.NotEqual(0, (nint)backend.GetNativeAdapter(device));
        Assert.NotEqual(0, (nint)backend.GetNativeResource(buffer));
        Assert.NotEqual(0, (nint)backend.GetNativeResource(texture));
        Assert.NotEqual(0, (nint)backend.GetNativeHeap(heap));
        Assert.NotEqual(0, (nint)backend.GetNativePipelineState(pipeline));
        Assert.NotEqual(0, (nint)backend.GetNativeRootSignature(pipeline));
        Assert.NotEqual(0, (nint)backend.GetNativeQueryHeap(queryPool));

        D3D12CommandListBorrow invalid = default;
        Assert.False(invalid.IsValid);
        AssertCommandListUnavailable(invalid);

        using CommandContext context = backend.CreateCommandContext(
            device,
            new CommandContextDesc(QueueType.Compute, 0, 1));
        backend.Begin(context, default);
        backend.SetPipeline(context, pipeline);
        backend.SetPipeline(context, pipeline);
        D3D12CommandListBorrow borrow = backend.BorrowCommandList(context, [buffer, texture]);
        D3D12CommandListBorrow copy = borrow;
        Assert.True(borrow.IsValid);
        Assert.True(copy.IsValid);
        Assert.NotEqual(0, (nint)borrow.Pointer);

        backend.SetPipeline(context, pipeline);
        Assert.False(borrow.IsValid);
        Assert.False(copy.IsValid);
        AssertCommandListUnavailable(copy);
        using RecordedCommands commands = backend.End(context);
    }

    [Fact]
    public void Native_encoded_resources_are_retained_until_completion()
    {
        using D3D12Backend backend = new();
        using Device device = D3D12TestSupport.CreateWarpDevice(backend);
        Buffer source = backend.CreateBuffer(
            device,
            new BufferDesc(256, BufferUsages.CopySource),
            MemoryType.Upload);
        Buffer destination = backend.CreateBuffer(
            device,
            new BufferDesc(256, BufferUsages.CopyDestination),
            MemoryType.Readback);
        try
        {
            using (MappedBuffer mapping = backend.Map(
                source,
                MapType.Write,
                new BufferRange(0, 256)))
            {
                mapping.Bytes.Fill(0x5A);
                mapping.Flush(new BufferRange(0, 256));
            }

            using CommandContext context = backend.CreateCommandContext(
                device,
                new CommandContextDesc(QueueType.Copy, 0, 1));
            backend.Begin(context, default);
            D3D12CommandListBorrow borrow = backend.BorrowCommandList(
                context,
                [source, destination]);
            borrow.Pointer->CopyBufferRegion(
                backend.GetNativeResource(destination),
                0,
                backend.GetNativeResource(source),
                0,
                256);
            using RecordedCommands commands = backend.End(context);
            Assert.False(borrow.IsValid);

            source.Dispose();
            destination.Dispose();

            Queue queue = backend.GetQueue(device, QueueType.Copy);
            QueueCompletion completion = backend.Submit(
                queue,
                new QueueSubmitDesc([], [], [commands], [], []));
            Assert.Equal(
                WaitStatus.Completed,
                backend.WaitCpu(completion, TimeSpan.FromSeconds(10)));
            backend.CollectCompleted(device);
        }
        finally
        {
            source.Dispose();
            destination.Dispose();
        }
    }

    [Fact]
    public void Validation_native_access_rejects_foreign_disposed_wrong_family_and_illegal_state()
    {
        const string source = """
            [shader("compute")]
            [numthreads(1, 1, 1)]
            void computeMain() {}
            """;
        using D3D12TestShaderProgram shader = D3D12TestShaderProgram.Compile(
            "rhi_validated_native_access",
            source,
            [new D3D12TestShaderEntry("computeMain", SlangStage.Compute)]);
        D3D12Backend nativeBackend = new();
        using ValidationLayer backend = new(nativeBackend);
        using Device foreignDevice = D3D12TestSupport.CreateWarpDevice(nativeBackend);
        using Buffer foreign = nativeBackend.CreateBuffer(
            foreignDevice,
            new BufferDesc(256, BufferUsages.CopySource),
            MemoryType.Upload);
        using Device device = D3D12TestSupport.CreateWarpDevice(backend);
        using Buffer buffer = backend.CreateBuffer(
            device,
            new BufferDesc(256, BufferUsages.CopySource),
            MemoryType.Upload);
        using Pipeline pipeline = backend.CreateComputePipeline(
            device,
            new ComputePipelineDesc(shader.Program, shader.GetEntryPoint(0)));
        using CommandContext context = backend.CreateCommandContext(
            device,
            new CommandContextDesc(QueueType.Compute, 0, 1));

        Assert.NotEqual(0, (nint)backend.GetNativeDevice(device));
        Assert.NotEqual(0, (nint)backend.GetNativeResource(buffer));
        AssertNotSupportedByValidation(() => backend.GetNativeStateObject(pipeline));
        AssertInvalidBorrow(backend, context, buffer);

        Buffer disposed = backend.CreateBuffer(
            device,
            new BufferDesc(256, BufferUsages.CopySource),
            MemoryType.Upload);
        disposed.Dispose();
        AssertNotSupportedByValidation(() => backend.GetNativeResource(disposed));

        AssertNotSupportedByValidation(() => backend.GetNativeResource(foreign));

        Queue queue = backend.GetQueue(device, QueueType.Compute);
        D3D12CommandQueueLock held = backend.LockCommandQueue(queue);
        try
        {
            AssertInvalidQueueLock(backend, queue);
        }
        finally
        {
            held.Dispose();
        }

        backend.Begin(context);
        D3D12CommandListBorrow valid = backend.BorrowCommandList(context, [buffer]);
        Assert.True(valid.IsValid);
        backend.Discard(context);
        Assert.False(valid.IsValid);
    }

    [Fact]
    public void Native_capabilities_and_command_queue_lock_report_exact_availability()
    {
        using D3D12Backend backend = new();
        using Device device = D3D12TestSupport.CreateWarpDevice(backend);

        Assert.True(backend.TryGetCapability(device, out D3D12NativeAccess? nativeAccess));
        Assert.NotNull(nativeAccess);
        Assert.Same(device, nativeAccess.Device);
        Assert.True(backend.TryGetCapability(device, out D3D12Diagnostics? diagnostics));
        Assert.NotNull(diagnostics);
        Assert.Same(device, diagnostics.Device);
        Assert.False(diagnostics.DebugLayerEnabled);
        Assert.False(diagnostics.GpuBasedValidationEnabled);
        Assert.False(diagnostics.SynchronizedQueueValidationEnabled);
        Assert.False(diagnostics.DredEnabled);
        Assert.Null(diagnostics.DeviceLoss);

        D3D12CommandQueueLock invalid = default;
        Assert.False(invalid.IsHeld);
        AssertPointerUnavailable(invalid);
        invalid.Dispose();

        Queue queue = backend.GetQueue(device, QueueType.Graphics);
        D3D12CommandQueueLock held = backend.LockCommandQueue(queue);
        D3D12CommandQueueLock copy = held;
        Assert.True(held.IsHeld);
        Assert.True(copy.IsHeld);
        Assert.NotEqual(0, (nint)held.Pointer);

        using ManualResetEventSlim started = new();
        using ManualResetEventSlim finished = new();
        QueueCompletion submitted = default;
        Exception? submitFailure = null;
        Thread waitingSubmit = new(() =>
        {
            started.Set();
            try
            {
                submitted = backend.Submit(queue, new QueueSubmitDesc([], [], [], [], []));
            }
            catch (Exception exception)
            {
                submitFailure = exception;
            }
            finally
            {
                finished.Set();
            }
        });
        waitingSubmit.Start();
        Assert.True(started.Wait(TimeSpan.FromSeconds(2)));
        Assert.False(finished.Wait(TimeSpan.FromMilliseconds(100)));
        var disposer = new QueueLockDisposer(copy.Lease, copy.Sequence);
        Thread releasingLock = new(disposer.Dispose);
        releasingLock.Start();
        Assert.True(releasingLock.Join(TimeSpan.FromSeconds(5)));
        Assert.Null(disposer.Failure);
        Assert.False(held.IsHeld);
        Assert.False(copy.IsHeld);
        AssertPointerUnavailable(held);
        held.Dispose();
        Assert.True(finished.Wait(TimeSpan.FromSeconds(5)));
        waitingSubmit.Join();
        Assert.Null(submitFailure);
        Assert.Equal(
            WaitStatus.Completed,
            backend.WaitCpu(submitted, TimeSpan.FromSeconds(5)));

        D3D12CommandQueueLock next = backend.LockCommandQueue(queue);
        Assert.True(next.IsHeld);
        Assert.False(held.IsHeld);
        Assert.NotEqual(0, (nint)next.Pointer);
        next.Dispose();
        next.Dispose();
        Assert.False(next.IsHeld);

        for (int index = 0; index < 32; index++)
        {
            D3D12CommandQueueLock warm = backend.LockCommandQueue(queue);
            warm.Dispose();
        }
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int index = 0; index < 1_024; index++)
        {
            D3D12CommandQueueLock stable = backend.LockCommandQueue(queue);
            stable.Dispose();
        }
        Assert.Equal(before, GC.GetAllocatedBytesForCurrentThread());
    }

    [Fact]
    public void Validated_command_queue_lock_reuses_its_sequence_authority_without_allocating()
    {
        using var direct = new D3D12Backend();
        using var backend = new ValidationLayer(direct);
        using Device device = D3D12TestSupport.CreateWarpDevice(backend);
        Queue queue = backend.GetQueue(device, QueueType.Graphics);

        for (int index = 0; index < 32; index++)
        {
            D3D12CommandQueueLock warm = backend.LockCommandQueue(queue);
            warm.Dispose();
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int index = 0; index < 1_024; index++)
        {
            D3D12CommandQueueLock stable = backend.LockCommandQueue(queue);
            stable.Dispose();
        }

        Assert.Equal(before, GC.GetAllocatedBytesForCurrentThread());
    }

    [Fact]
    public void Validated_command_queue_lock_serializes_multiple_waiters_without_poisoning_its_lease()
    {
        using var direct = new D3D12Backend();
        using var backend = new ValidationLayer(direct);
        using Device device = D3D12TestSupport.CreateWarpDevice(backend);
        Queue queue = backend.GetQueue(device, QueueType.Graphics);
        D3D12CommandQueueLock first = backend.LockCommandQueue(queue);
        using var ready = new CountdownEvent(2);
        using var firstRelease = new ManualResetEventSlim();
        using var secondRelease = new ManualResetEventSlim();
        using var left = new QueueLockWaiter(backend, queue, ready, firstRelease);
        using var right = new QueueLockWaiter(backend, queue, ready, secondRelease);
        Thread leftThread = new(left.AcquireAndRelease);
        Thread rightThread = new(right.AcquireAndRelease);
        leftThread.Start();
        rightThread.Start();
        try
        {
            Assert.True(ready.Wait(TimeSpan.FromSeconds(5)));
            Assert.True(SpinWait.SpinUntil(
                () => IsWaiting(leftThread) && IsWaiting(rightThread),
                TimeSpan.FromSeconds(5)));

            first.Dispose();
            int firstIndex = WaitHandle.WaitAny(
                [left.Acquired.WaitHandle, right.Acquired.WaitHandle],
                TimeSpan.FromSeconds(5));
            Assert.True(firstIndex is 0 or 1);
            QueueLockWaiter secondWaiter = firstIndex == 0 ? right : left;
            ManualResetEventSlim acquiredRelease = firstIndex == 0 ? firstRelease : secondRelease;
            ManualResetEventSlim waitingRelease = firstIndex == 0 ? secondRelease : firstRelease;
            acquiredRelease.Set();
            Assert.True(secondWaiter.Acquired.Wait(TimeSpan.FromSeconds(5)));
            waitingRelease.Set();
        }
        finally
        {
            first.Dispose();
            firstRelease.Set();
            secondRelease.Set();
        }
        Assert.True(leftThread.Join(TimeSpan.FromSeconds(5)));
        Assert.True(rightThread.Join(TimeSpan.FromSeconds(5)));
        Assert.Null(left.Failure);
        Assert.Null(right.Failure);
        Assert.True(left.AcquisitionOrder > 0);
        Assert.True(right.AcquisitionOrder > 0);
        Assert.NotEqual(left.AcquisitionOrder, right.AcquisitionOrder);

        D3D12CommandQueueLock diagnostic = backend.LockCommandQueue(queue);
        AssertInvalidQueueLock(backend, queue);
        diagnostic.Dispose();
        D3D12CommandQueueLock retry = backend.LockCommandQueue(queue);
        Assert.True(retry.IsHeld);
        retry.Dispose();
    }

    [Fact]
    public void Validated_command_queue_lock_copies_join_cross_thread_release()
    {
        using var direct = new D3D12Backend();
        using var backend = new ValidationLayer(direct);
        using Device device = D3D12TestSupport.CreateWarpDevice(backend);
        Queue queue = backend.GetQueue(device, QueueType.Graphics);
        D3D12CommandQueueLock held = backend.LockCommandQueue(queue);
        D3D12CommandQueueLock copy = held;
        object validationGate = typeof(ValidationLayer)
            .GetField("_gate", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(backend)!;
        D3D12CommandQueueLockLease heldLease = held.Lease;
        ulong heldSequence = held.Sequence;
        var owner = new QueueLockDisposer(heldLease, heldSequence);
        var contender = new QueueLockDisposer(copy.Lease, copy.Sequence);
        Thread ownerThread = new(owner.Dispose);
        Thread contenderThread = new(contender.Dispose);

        Monitor.Enter(validationGate);
        try
        {
            ownerThread.Start();
            Assert.True(SpinWait.SpinUntil(
                () => !heldLease.IsHeld(heldSequence),
                TimeSpan.FromSeconds(5)));
            contenderThread.Start();
            Assert.True(SpinWait.SpinUntil(
                () => IsWaiting(contenderThread),
                TimeSpan.FromSeconds(5)));
            Assert.True(ownerThread.IsAlive);
            Assert.True(contenderThread.IsAlive);
        }
        finally
        {
            Monitor.Exit(validationGate);
        }

        Assert.True(ownerThread.Join(TimeSpan.FromSeconds(5)));
        Assert.True(contenderThread.Join(TimeSpan.FromSeconds(5)));
        Assert.Null(owner.Failure);
        Assert.Null(contender.Failure);
        Assert.False(held.IsHeld);
        Assert.False(copy.IsHeld);

        D3D12CommandQueueLock retry = backend.LockCommandQueue(queue);
        Assert.True(retry.IsHeld);
        retry.Dispose();
    }

    private static bool IsWaiting(Thread thread) =>
        (thread.ThreadState & ThreadState.WaitSleepJoin) != 0;

    private static void AssertPointerUnavailable(D3D12CommandQueueLock value)
    {
        try
        {
            _ = value.Pointer;
        }
        catch (InvalidOperationException)
        {
            return;
        }

        throw new Xunit.Sdk.XunitException("A released native Queue lock exposed its pointer.");
    }

    private static void AssertCommandListUnavailable(D3D12CommandListBorrow value)
    {
        try
        {
            _ = value.Pointer;
        }
        catch (InvalidOperationException)
        {
            return;
        }

        throw new Xunit.Sdk.XunitException("An invalid native command-list borrow exposed its pointer.");
    }

    private sealed class QueueLockDisposer
    {
        private readonly D3D12CommandQueueLockLease _lease;
        private readonly ulong _sequence;

        internal QueueLockDisposer(D3D12CommandQueueLockLease lease, ulong sequence)
        {
            _lease = lease;
            _sequence = sequence;
        }

        internal Exception? Failure { get; private set; }

        internal void Dispose()
        {
            try
            {
                D3D12CommandQueueLock value = new(_lease, _sequence);
                value.Dispose();
            }
            catch (Exception exception)
            {
                Failure = exception;
            }
        }
    }

    private sealed class QueueLockWaiter : IDisposable
    {
        private static int s_nextAcquisitionOrder;
        private readonly ValidationLayer _backend;
        private readonly Queue _queue;
        private readonly CountdownEvent _ready;
        private readonly ManualResetEventSlim _release;

        internal QueueLockWaiter(
            ValidationLayer backend,
            Queue queue,
            CountdownEvent ready,
            ManualResetEventSlim release)
        {
            _backend = backend;
            _queue = queue;
            _ready = ready;
            _release = release;
        }

        internal ManualResetEventSlim Acquired { get; } = new();
        internal int AcquisitionOrder { get; private set; }
        internal Exception? Failure { get; private set; }

        internal void AcquireAndRelease()
        {
            _ready.Signal();
            try
            {
                D3D12CommandQueueLock value = _backend.LockCommandQueue(_queue);
                AcquisitionOrder = Interlocked.Increment(ref s_nextAcquisitionOrder);
                Acquired.Set();
                _release.Wait();
                value.Dispose();
            }
            catch (Exception exception)
            {
                Failure = exception;
                Acquired.Set();
            }
        }

        public void Dispose() => Acquired.Dispose();
    }

    private static void AssertNotSupportedByValidation(Action action) =>
        Assert.Throws<InvalidOperationException>(action);

    private static void AssertInvalidBorrow(
        ValidationLayer backend,
        CommandContext context,
        Buffer retainedResource)
    {
        try
        {
            _ = backend.BorrowCommandList(context, [retainedResource]);
        }
        catch (InvalidOperationException)
        {
            return;
        }

        throw new Xunit.Sdk.XunitException("Validation accepted an illegal native command-list borrow.");
    }

    private static void AssertInvalidQueueLock(
        ValidationLayer backend,
        Queue queue)
    {
        try
        {
            D3D12CommandQueueLock unexpected = backend.LockCommandQueue(queue);
            unexpected.Dispose();
        }
        catch (InvalidOperationException)
        {
            return;
        }

        throw new Xunit.Sdk.XunitException("Validation accepted same-thread native Queue re-entry.");
    }
}
