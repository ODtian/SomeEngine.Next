using SomeEngine.Graphics.Direct3D12;
using SomeEngine.Graphics.Validation;
using Xunit;

namespace SomeEngine.Graphics.Direct3D12.Tests;

public sealed class WarpResourceTests
{
    [Theory]
    [InlineData(MapType.Read)]
    [InlineData(MapType.Write)]
    [InlineData(MapType.ReadWrite)]
    public void Every_named_map_type_maps_and_releases(MapType type)
    {
        using IGraphicsBackend backend = new D3D12Backend();
        using Device device = D3D12TestSupport.CreateWarpDevice(backend);
        using Buffer upload = backend.CreateBuffer(
            device,
            new BufferDesc(64, BufferUsages.CopySource),
            MemoryType.Upload);

        MappedBuffer mapping = backend.Map(upload, type, new BufferRange(8, 32));
        Assert.Equal(32, mapping.Bytes.Length);
        mapping.Dispose();
    }

    [Fact]
    public void Buffers_report_canonical_memory_type_initial_facts()
    {
        using IGraphicsBackend backend = new D3D12Backend();
        using Device device = D3D12TestSupport.CreateWarpDevice(backend);
        using Buffer ordinary = backend.CreateBuffer(
            device,
            new BufferDesc(256, BufferUsages.CopySource | BufferUsages.CopyDestination),
            MemoryType.DeviceLocal);
        using Buffer upload = backend.CreateBuffer(
            device,
            new BufferDesc(256, BufferUsages.CopySource),
            MemoryType.Upload);
        using Buffer readback = backend.CreateBuffer(
            device,
            new BufferDesc(256, BufferUsages.CopyDestination),
            MemoryType.Readback);

        Assert.Equal(PipelineSync.None, ordinary.InitialSync);
        Assert.Equal(ResourceAccess.NoAccess, ordinary.InitialAccess);
        Assert.Equal(PipelineSync.None, upload.InitialSync);
        Assert.Equal(ResourceAccess.NoAccess, upload.InitialAccess);
        Assert.Equal(PipelineSync.Copy, readback.InitialSync);
        Assert.Equal(ResourceAccess.CopyDestination, readback.InitialAccess);
    }

    [Fact]
    public void Private_resource_allocator_reuses_ranges_only_after_native_capture_retirement()
    {
        using var backend = new D3D12Backend();
        using Device device = D3D12TestSupport.CreateWarpDevice(backend);
        Assert.True(backend.TryGetCapability(device, out D3D12Diagnostics? diagnostics));
        Assert.NotNull(diagnostics);
        const ulong size = 64 * 1024;
        BufferDesc pooledDescription = new(
            size,
            BufferUsages.CopySource | BufferUsages.CopyDestination,
            "pooled resource");
        Buffer first = backend.CreateBuffer(device, pooledDescription);
        using Buffer upload = backend.CreateBuffer(
            device,
            new BufferDesc(size, BufferUsages.CopySource),
            MemoryType.Upload);
        using Buffer readback = backend.CreateBuffer(
            device,
            new BufferDesc(size, BufferUsages.CopyDestination),
            MemoryType.Readback);
        Assert.Null(first.Heap);
        Assert.Null(upload.Heap);
        Assert.Null(readback.Heap);
        PooledResourceAllocation firstAllocation = Assert.NotNull(
            D3D12PrivateState.PooledAllocation(first));
        Assert.NotEqual(0, firstAllocation.HeapPointer);

        BufferRange range = new(0, size);
        using (MappedBuffer mapping = backend.Map(upload, MapType.Write, range))
        {
            for (int index = 0; index < mapping.Bytes.Length; index++)
                mapping.Bytes[index] = unchecked((byte)(index * 31 + 7));
            mapping.Flush(range);
        }

        using CommandContext context = backend.CreateCommandContext(
            device,
            new CommandContextDesc(QueueType.Copy, 0, 1));
        backend.Begin(context);
        backend.Barrier(context, new BufferBarrier(
            first,
            PipelineSync.None,
            PipelineSync.Copy,
            ResourceAccess.NoAccess,
            ResourceAccess.CopyDestination));
        backend.CopyBuffer(context, new BufferCopy(upload, 0, first, 0, size));
        backend.Barrier(context, new BufferBarrier(
            first,
            PipelineSync.Copy,
            PipelineSync.Copy,
            ResourceAccess.CopyDestination,
            ResourceAccess.CopySource));
        backend.CopyBuffer(context, new BufferCopy(first, 0, readback, 0, size));
        RecordedCommands commands = backend.End(context);
        first.Dispose();

        using Buffer whileCaptured = backend.CreateBuffer(device, pooledDescription);
        PooledResourceAllocation capturedAllocation = Assert.NotNull(
            D3D12PrivateState.PooledAllocation(whileCaptured));
        Assert.Equal(firstAllocation.HeapPointer, capturedAllocation.HeapPointer);
        Assert.NotEqual(firstAllocation.Offset, capturedAllocation.Offset);

        QueueCompletion completion = backend.Submit(
            backend.GetQueue(device, QueueType.Copy),
            new QueueSubmitDesc([], [], [commands], [], []));
        commands.Dispose();
        Assert.Equal(
            WaitStatus.Completed,
            backend.WaitCpu(completion, TimeSpan.FromSeconds(10)));
        backend.CollectCompleted(device);

        using Buffer afterCompletion = backend.CreateBuffer(device, pooledDescription);
        PooledResourceAllocation reused = Assert.NotNull(
            D3D12PrivateState.PooledAllocation(afterCompletion));
        Assert.Equal(firstAllocation.HeapPointer, reused.HeapPointer);
        Assert.Equal(firstAllocation.Offset, reused.Offset);

        using (MappedBuffer mapping = backend.Map(readback, MapType.Read, range))
        {
            mapping.Invalidate(range);
            for (int index = 0; index < mapping.Bytes.Length; index++)
                Assert.Equal(unchecked((byte)(index * 31 + 7)), mapping.Bytes[index]);
        }

        D3D12MemoryAllocatorInfo info = diagnostics.MemoryAllocator;
        Assert.True(info.HeapCount >= 3);
        Assert.True(info.AllocationCount >= 4);
        Assert.True(info.PooledHeapBytes >= 96UL * 1024 * 1024);
        Assert.True(info.PooledAllocatedBytes >= 4 * size);
    }

    [Fact]
    public void Oversized_resource_uses_committed_fallback_and_reports_it()
    {
        using var backend = new D3D12Backend();
        using Device device = D3D12TestSupport.CreateWarpDevice(backend);
        Assert.True(backend.TryGetCapability(device, out D3D12Diagnostics? diagnostics));
        Assert.NotNull(diagnostics);
        D3D12MemoryAllocatorInfo before = diagnostics.MemoryAllocator;

        using Buffer large = backend.CreateBuffer(
            device,
            new BufferDesc(33UL * 1024 * 1024, BufferUsages.CopyDestination));

        Assert.Null(D3D12PrivateState.PooledAllocation(large));
        D3D12MemoryAllocatorInfo after = diagnostics.MemoryAllocator;
        Assert.Equal(before.CommittedFallbackCount + 1, after.CommittedFallbackCount);
    }

    [Fact]
    public void Resource_allocator_releases_extra_empty_blocks_and_keeps_one_warm_block()
    {
        using var backend = new D3D12Backend();
        using Device device = D3D12TestSupport.CreateWarpDevice(backend);
        Assert.True(backend.TryGetCapability(device, out D3D12Diagnostics? diagnostics));
        Assert.NotNull(diagnostics);
        const ulong allocationSize = 24UL * 1024 * 1024;
        var buffers = new Buffer[3];
        try
        {
            for (int index = 0; index < buffers.Length; index++)
            {
                buffers[index] = backend.CreateBuffer(
                    device,
                    new BufferDesc(allocationSize, BufferUsages.CopyDestination));
            }

            D3D12MemoryAllocatorInfo populated = diagnostics.MemoryAllocator;
            Assert.Equal(2, populated.HeapCount);
            Assert.Equal(3, populated.AllocationCount);
        }
        finally
        {
            foreach (Buffer? buffer in buffers)
                buffer?.Dispose();
        }

        D3D12MemoryAllocatorInfo trimmed = diagnostics.MemoryAllocator;
        Assert.Equal(1, trimmed.HeapCount);
        Assert.Equal(0, trimmed.AllocationCount);
        Assert.Equal(64UL * 1024 * 1024, trimmed.PooledHeapBytes);
        Assert.Equal(0UL, trimmed.PooledAllocatedBytes);
    }

    [Fact]
    public void Mapping_uses_absolute_windows_and_one_shared_terminal_sequence()
    {
        using IGraphicsBackend backend = new D3D12Backend();
        using Device device = D3D12TestSupport.CreateWarpDevice(backend);
        using Buffer upload = backend.CreateBuffer(
            device,
            new BufferDesc(256, BufferUsages.CopySource),
            MemoryType.Upload);
        BufferRange window = new(32, 64);
        MappedBuffer mapping = backend.Map(upload, MapType.Write, window);
        MappedBuffer copied = mapping;

        Assert.Equal(window, mapping.Range);
        Assert.Equal(64, mapping.Bytes.Length);
        mapping.Bytes.Fill(0x5a);
        mapping.Flush(new BufferRange(48, 16));
        mapping.Flush(new BufferRange(96, 0));

        AssertSecondMapRejected(backend, upload, window);
        AssertEscapingFlushRejected(ref mapping);
        mapping.Dispose();
        copied.Dispose();
        AssertInactive(ref copied);

        MappedBuffer next = backend.Map(upload, MapType.Write, window);
        next.Dispose();
    }

    [Fact]
    public void Mapping_reuses_its_cold_state_without_managed_allocation()
    {
        using D3D12Backend backend = new();
        using Device device = D3D12TestSupport.CreateWarpDevice(backend);
        using Buffer upload = backend.CreateBuffer(
            device,
            new BufferDesc(64, BufferUsages.CopySource),
            MemoryType.Upload);
        BufferRange window = new(8, 32);

        for (int index = 0; index < 16; index++)
        {
            MappedBuffer warmup = backend.Map(upload, MapType.Write, window);
            warmup.Bytes[0] = checked((byte)index);
            warmup.Dispose();
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int index = 0; index < 256; index++)
        {
            MappedBuffer mapping = backend.Map(upload, MapType.Write, window);
            mapping.Bytes[0] = unchecked((byte)index);
            mapping.Dispose();
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocated);
    }

    [Fact]
    public void Stale_mapping_operations_cannot_observe_or_end_a_reused_mapping_slot()
    {
        using IGraphicsBackend backend = new D3D12Backend();
        using Device device = D3D12TestSupport.CreateWarpDevice(backend);
        using Buffer upload = backend.CreateBuffer(
            device,
            new BufferDesc(64, BufferUsages.CopySource),
            MemoryType.Upload);
        BufferRange firstWindow = new(0, 16);
        BufferRange secondWindow = new(32, 16);

        MappedBuffer first = backend.Map(upload, MapType.Write, firstWindow);
        MappedBuffer stale = first;
        first.Dispose();

        MappedBuffer second = backend.Map(upload, MapType.Write, secondWindow);
        AssertInactive(ref stale);
        stale.Dispose();
        Assert.Equal(secondWindow, second.Range);
        second.Bytes.Fill(0x6d);
        second.Dispose();

        MappedBuffer retry = backend.Map(upload, MapType.Write, firstWindow);
        retry.Dispose();
    }

    [Fact]
    public void Mapping_copy_dispose_contenders_join_the_single_release()
    {
        using IGraphicsBackend backend = new D3D12Backend();
        using Device device = D3D12TestSupport.CreateWarpDevice(backend);
        using Buffer upload = backend.CreateBuffer(
            device,
            new BufferDesc(64, BufferUsages.CopySource),
            MemoryType.Upload);
        using var releaseEntered = new ManualResetEventSlim();
        using var allowRelease = new ManualResetEventSlim();
        using var ownerReady = new ManualResetEventSlim();
        using var startOwner = new ManualResetEventSlim();
        using var ownerCompleted = new ManualResetEventSlim();
        using var contenderReady = new ManualResetEventSlim();
        using var startContender = new ManualResetEventSlim();
        using var contenderEnteredDispose = new ManualResetEventSlim();
        using var contenderCompleted = new ManualResetEventSlim();
        var lease = new BlockingMappingLease(upload, releaseEntered, allowRelease);
        ulong sequence = lease.Activate(new BufferRange(0, 16));

        Exception? ownerFailure = null;
        Exception? contenderFailure = null;
        var owner = new Thread(() =>
        {
            ownerReady.Set();
            startOwner.Wait();
            try
            {
                lease.Dispose(sequence);
            }
            catch (Exception exception)
            {
                ownerFailure = exception;
            }
            finally
            {
                ownerCompleted.Set();
            }
        })
        {
            IsBackground = true,
            Name = "Mapping release owner",
        };
        var contender = new Thread(() =>
        {
            contenderReady.Set();
            startContender.Wait();
            try
            {
                contenderEnteredDispose.Set();
                lease.Dispose(sequence);
            }
            catch (Exception exception)
            {
                contenderFailure = exception;
            }
            finally
            {
                contenderCompleted.Set();
            }
        })
        {
            IsBackground = true,
            Name = "Mapping release contender",
        };

        owner.Start();
        contender.Start();
        bool ownerJoined;
        bool contenderJoined;
        try
        {
            Assert.True(ownerReady.Wait(TimeSpan.FromSeconds(5)), "The owner thread did not become ready.");
            Assert.True(contenderReady.Wait(TimeSpan.FromSeconds(5)), "The contender thread did not become ready.");

            startOwner.Set();
            Assert.True(
                releaseEntered.Wait(TimeSpan.FromSeconds(5)),
                "The mapping owner did not enter its release operation.");
            Assert.False(ownerCompleted.IsSet, "The owner completed while its native release remained blocked.");

            startContender.Set();
            Assert.True(
                contenderEnteredDispose.Wait(TimeSpan.FromSeconds(5)),
                "The contender thread did not enter Dispose.");
            Assert.False(
                contenderCompleted.Wait(TimeSpan.FromMilliseconds(250)),
                "The contender returned before the owner completed the shared release.");
            Assert.False(ownerCompleted.IsSet, "The owner completed before the release gate opened.");
        }
        finally
        {
            startOwner.Set();
            startContender.Set();
            allowRelease.Set();
            ownerJoined = owner.Join(TimeSpan.FromSeconds(10));
            contenderJoined = contender.Join(TimeSpan.FromSeconds(10));
        }

        Assert.True(ownerJoined, "The owner thread did not finish after the release gate opened.");
        Assert.True(contenderJoined, "The contender thread did not join the completed release.");
        Assert.Null(ownerFailure);
        Assert.Null(contenderFailure);
        Assert.Equal(1, lease.UnmapCount);

        lease.Dispose(sequence);
        Assert.Equal(1, lease.UnmapCount);
    }

    [Fact]
    public void Unknown_map_type_is_rejected_without_activating_the_mapping()
    {
        using IGraphicsBackend backend = new D3D12Backend();
        using Device device = D3D12TestSupport.CreateWarpDevice(backend);
        using Buffer upload = backend.CreateBuffer(
            device,
            new BufferDesc(64, BufferUsages.CopySource),
            MemoryType.Upload);
        BufferRange window = new(16, 32);

        AssertMapRejected<ArgumentOutOfRangeException>(
            backend,
            upload,
            (MapType)byte.MaxValue,
            window);

        MappedBuffer mapping = backend.Map(upload, MapType.Write, window);
        MappedBuffer copied = mapping;
        mapping.Bytes.Fill(0x3c);
        mapping.Dispose();
        copied.Dispose();
        AssertInactive(ref copied);

        MappedBuffer retry = backend.Map(upload, MapType.Write, window);
        Assert.Equal(window, retry.Range);
        retry.Dispose();
    }

    [Fact]
    public void Whole_mapping_is_resolved_and_default_or_zero_length_mappings_are_invalid()
    {
        using IGraphicsBackend backend = new D3D12Backend();
        using Device device = D3D12TestSupport.CreateWarpDevice(backend);
        using Buffer upload = backend.CreateBuffer(
            device,
            new BufferDesc(257, BufferUsages.CopySource),
            MemoryType.Upload);

        MappedBuffer whole = backend.Map(upload, MapType.Write, BufferRange.Whole);
        Assert.Equal(new BufferRange(0, 257), whole.Range);
        Assert.Equal(257, whole.Bytes.Length);
        whole.Dispose();

        AssertMapRejected<ArgumentOutOfRangeException>(
            backend,
            upload,
            MapType.Write,
            new BufferRange(257, 0));

        MappedBuffer invalid = default;
        AssertInvalidDefault(ref invalid);
        invalid.Dispose();
    }

    [Fact]
    public void Mapping_larger_than_Int32MaxValue_is_rejected_before_native_Map()
    {
        using IGraphicsBackend backend = new D3D12Backend();
        using Device device = D3D12TestSupport.CreateWarpDevice(backend);
        Assert.True(backend.TryGetCapability(device, out SparseResources? sparse));
        Assert.NotNull(sparse);
        using Buffer reserved = backend.CreateReservedBuffer(
            device,
            new BufferDesc(
                checked((ulong)int.MaxValue + 1),
                BufferUsages.CopySource));

        AssertMapRejected<ArgumentOutOfRangeException>(
            backend,
            reserved,
            MapType.Write,
            BufferRange.Whole);
        Assert.Equal(DeviceStatus.Active, device.Status);
    }

    [Fact]
    public void Resource_disposal_invalidates_an_active_mapping_once()
    {
        using IGraphicsBackend backend = new D3D12Backend();
        using Device device = D3D12TestSupport.CreateWarpDevice(backend);
        Buffer upload = backend.CreateBuffer(
            device,
            new BufferDesc(64, BufferUsages.CopySource),
            MemoryType.Upload);
        MappedBuffer mapping = backend.Map(upload, MapType.Write, BufferRange.Whole);
        MappedBuffer copied = mapping;

        upload.Dispose();

        AssertInactive(ref mapping);
        AssertInactive(ref copied);
        mapping.Dispose();
        copied.Dispose();
        upload.Dispose();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Wait_timeout_domain_is_validated_before_completion_access(
        bool validationEnabled)
    {
        using IGraphicsBackend backend = validationEnabled
            ? new ValidationLayer(new D3D12Backend())
            : new D3D12Backend();

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            backend.WaitCpu(default, TimeSpan.FromMilliseconds(-2)));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            backend.WaitCpu(
                default,
                TimeSpan.FromMilliseconds((double)int.MaxValue + 1)));
        Assert.Throws<InvalidOperationException>(() =>
            backend.WaitCpu(default, TimeSpan.Zero));
        Assert.Throws<InvalidOperationException>(() =>
            backend.WaitCpu(default, TimeSpan.FromTicks(1)));
        Assert.Throws<InvalidOperationException>(() => backend.IsComplete(default));
    }

    [Fact]
    public void Wait_fractional_timeout_polls_and_a_failed_timeout_leaves_the_completion_usable()
    {
        using IGraphicsBackend backend = new D3D12Backend();
        using Device device = D3D12TestSupport.CreateWarpDevice(backend);
        using CommandContext context = backend.CreateCommandContext(
            device,
            new CommandContextDesc(QueueType.Graphics, 0, 1));
        backend.Begin(context);
        using RecordedCommands recorded = backend.End(context);
        QueueCompletion completion = backend.Submit(
            backend.GetQueue(device, QueueType.Graphics),
            new QueueSubmitDesc([], [], [recorded], [], []));

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            backend.WaitCpu(completion, TimeSpan.FromTicks(-1)));
        WaitStatus poll = backend.WaitCpu(completion, TimeSpan.FromTicks(1));
        Assert.True(poll is WaitStatus.Completed or WaitStatus.Timeout);
        Assert.Equal(
            WaitStatus.Completed,
            backend.WaitCpu(completion, TimeSpan.FromSeconds(10)));
    }

    private static void AssertSecondMapRejected(
        IGraphicsBackend backend,
        Buffer buffer,
        in BufferRange range)
    {
        try
        {
            MappedBuffer duplicate = backend.Map(buffer, MapType.Write, range);
            duplicate.Dispose();
            Assert.Fail("A second active mapping was accepted.");
        }
        catch (InvalidOperationException)
        {
        }
    }

    private static void AssertEscapingFlushRejected(ref MappedBuffer mapping)
    {
        try
        {
            mapping.Flush(new BufferRange(16, 32));
            Assert.Fail("A Flush range escaped its mapping window.");
        }
        catch (ArgumentOutOfRangeException)
        {
        }
    }

    private static void AssertInactive(ref MappedBuffer mapping)
    {
        try
        {
            _ = mapping.Bytes;
            Assert.Fail("A disposed mapping copy became active.");
        }
        catch (InvalidOperationException)
        {
        }
        try
        {
            _ = mapping.Range;
            Assert.Fail("A disposed mapping copy exposed its Range.");
        }
        catch (InvalidOperationException)
        {
        }
        try
        {
            mapping.Flush(default);
            Assert.Fail("A disposed mapping copy accepted Flush.");
        }
        catch (InvalidOperationException)
        {
        }
        try
        {
            mapping.Invalidate(default);
            Assert.Fail("A disposed mapping copy accepted Invalidate.");
        }
        catch (InvalidOperationException)
        {
        }
    }

    private static void AssertMapRejected<TException>(
        IGraphicsBackend backend,
        Buffer buffer,
        MapType type,
        in BufferRange range)
        where TException : Exception
    {
        try
        {
            MappedBuffer mapping = backend.Map(buffer, type, range);
            mapping.Dispose();
            Assert.Fail($"The mapping was accepted instead of throwing {typeof(TException).Name}.");
        }
        catch (TException)
        {
        }
    }

    private static void AssertInvalidDefault(ref MappedBuffer mapping)
    {
        try
        {
            _ = mapping.Range;
            Assert.Fail("A default mapping exposed a Range.");
        }
        catch (InvalidOperationException)
        {
        }
        try
        {
            _ = mapping.Bytes;
            Assert.Fail("A default mapping exposed Bytes.");
        }
        catch (InvalidOperationException)
        {
        }
        try
        {
            mapping.Flush(default);
            Assert.Fail("A default mapping accepted Flush.");
        }
        catch (InvalidOperationException)
        {
        }
        try
        {
            mapping.Invalidate(default);
            Assert.Fail("A default mapping accepted Invalidate.");
        }
        catch (InvalidOperationException)
        {
        }
    }

    private sealed class BlockingMappingLease : MappingLease
    {
        private readonly ManualResetEventSlim _releaseEntered;
        private readonly ManualResetEventSlim _allowRelease;
        private int _unmapCount;

        internal BlockingMappingLease(
            Buffer buffer,
            ManualResetEventSlim releaseEntered,
            ManualResetEventSlim allowRelease)
            : base(buffer)
        {
            _releaseEntered = releaseEntered;
            _allowRelease = allowRelease;
        }

        internal int UnmapCount => Volatile.Read(ref _unmapCount);

        internal ulong Activate(in BufferRange range)
        {
            ulong sequence = PrepareNextSequence();
            Publish(sequence, range);
            return sequence;
        }

        protected override void FlushCore(in BufferRange range)
        {
        }

        protected override void InvalidateCore(in BufferRange range)
        {
        }

        protected override void UnmapCore()
        {
            Interlocked.Increment(ref _unmapCount);
            _releaseEntered.Set();
            _allowRelease.Wait();
        }
    }
}
