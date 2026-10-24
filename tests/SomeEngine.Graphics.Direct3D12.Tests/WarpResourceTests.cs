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
        using IGraphicsBackend backend = D3D12GraphicsBackend.Create();
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
        using IGraphicsBackend backend = D3D12GraphicsBackend.Create();
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
    public void RecordedCopyRetainsDisposedBufferUntilReadbackCompletes()
    {
        using var backend = D3D12GraphicsBackend.Create();
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

        QueueCompletion completion = backend.Submit(
            backend.GetQueue(device, QueueType.Copy),
            new QueueSubmitDesc([], [], [commands], [], []));
        commands.Dispose();
        Assert.Equal(
            WaitStatus.Completed,
            backend.WaitCpu(completion, TimeSpan.FromSeconds(10)));
        backend.CollectCompleted(device);

        using Buffer afterCompletion = backend.CreateBuffer(device, pooledDescription);

        using (MappedBuffer mapping = backend.Map(readback, MapType.Read, range))
        {
            mapping.Invalidate(range);
            for (int index = 0; index < mapping.Bytes.Length; index++)
                Assert.Equal(unchecked((byte)(index * 31 + 7)), mapping.Bytes[index]);
        }

    }

    [Fact]
    public void LargeBufferReportsRequestedSize()
    {
        using var backend = D3D12GraphicsBackend.Create();
        using Device device = D3D12TestSupport.CreateWarpDevice(backend);
        Assert.True(backend.TryGetCapability(device, out D3D12Diagnostics? diagnostics));
        Assert.NotNull(diagnostics);

        using Buffer large = backend.CreateBuffer(
            device,
            new BufferDesc(33UL * 1024 * 1024, BufferUsages.CopyDestination));
        Assert.Equal(33UL * 1024 * 1024, large.Info.Size);

    }


    [Fact]
    public void Mapping_uses_absolute_windows_and_one_shared_terminal_sequence()
    {
        using IGraphicsBackend backend = D3D12GraphicsBackend.Create();
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
    public void Stale_mapping_operations_cannot_observe_or_end_a_reused_mapping_slot()
    {
        using IGraphicsBackend backend = D3D12GraphicsBackend.Create();
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
    public void Unknown_map_type_is_rejected_without_activating_the_mapping()
    {
        using IGraphicsBackend backend = D3D12GraphicsBackend.Create();
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
        using IGraphicsBackend backend = D3D12GraphicsBackend.Create();
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
        using IGraphicsBackend backend = D3D12GraphicsBackend.Create();
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
        using IGraphicsBackend backend = D3D12GraphicsBackend.Create();
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
            ? new ValidationLayer(D3D12GraphicsBackend.Create())
            : D3D12GraphicsBackend.Create();

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
        using IGraphicsBackend backend = D3D12GraphicsBackend.Create();
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


}
