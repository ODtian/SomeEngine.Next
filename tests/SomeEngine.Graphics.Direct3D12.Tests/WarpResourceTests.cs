using SomeEngine.Graphics.Direct3D12;
using Xunit;

namespace SomeEngine.Graphics.Direct3D12.Tests;

public sealed class WarpResourceTests
{
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
        Assert.Equal(ResourceAccess.Common, upload.InitialAccess);
        Assert.Equal(PipelineSync.Copy, readback.InitialSync);
        Assert.Equal(ResourceAccess.CopyDestination, readback.InitialAccess);
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

    [Fact]
    public void Wait_timeout_domain_is_validated_before_completion_access()
    {
        using IGraphicsBackend backend = new D3D12Backend();

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            backend.WaitCpu(default, TimeSpan.FromMilliseconds(-2)));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            backend.WaitCpu(
                default,
                TimeSpan.FromMilliseconds((double)int.MaxValue + 1)));
        Assert.Throws<InvalidOperationException>(() =>
            backend.WaitCpu(default, TimeSpan.Zero));
        Assert.Throws<InvalidOperationException>(() => backend.IsComplete(default));
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
