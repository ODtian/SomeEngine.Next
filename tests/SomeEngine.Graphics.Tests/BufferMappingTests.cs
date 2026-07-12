using SomeEngine.Graphics.Null;
using Xunit;

namespace SomeEngine.Graphics.Tests;

public sealed class BufferMappingTests
{
    [Fact]
    public void Null_mapping_enforces_memory_type_range_mode_and_unmap_lifetime()
    {
        using Device device = new();
        BufferHandle upload = device.CreateBuffer(new BufferDesc(16, BufferUsage.CopySource), MemoryType.Upload);
        using (BufferMapping mapping = device.MapBuffer(upload, BufferMapMode.Write, new BufferRange(4, 6)))
        {
            Assert.Equal(new BufferRange(4, 6), mapping.Range);
            mapping.Span.Fill(0x5A);
            Assert.Throws<InvalidOperationException>(() =>
                device.MapBuffer(upload, BufferMapMode.Write, BufferRange.Whole));
            Assert.Throws<InvalidOperationException>(() => device.DestroyBuffer(upload));
        }
        Assert.Throws<InvalidOperationException>(() =>
            device.MapBuffer(upload, BufferMapMode.Read, BufferRange.Whole));

        BufferHandle readback = device.CreateBuffer(new BufferDesc(16, BufferUsage.CopyDestination), MemoryType.Readback);
        using (ICommandContext commands = device.AcquireCommandContext(QueueType.Copy))
        {
            commands.CopyBuffer(upload, 0, readback, 0, 16);
            device.Submit(QueueType.Copy, [commands.Finish()]);
        }
        BufferMapping read = device.MapBuffer(readback, BufferMapMode.Read, BufferRange.Whole);
        Assert.Equal(new byte[] { 0, 0, 0, 0, 0x5A, 0x5A, 0x5A, 0x5A, 0x5A, 0x5A, 0, 0, 0, 0, 0, 0 }, read.Span.ToArray());
        read.Dispose();
        Assert.True(read.IsDisposed);
        bool disposedAccessRejected = false;
        try { _ = read.Span.Length; }
        catch (ObjectDisposedException) { disposedAccessRejected = true; }
        Assert.True(disposedAccessRejected);
        device.DestroyBuffer(readback);
        device.DestroyBuffer(upload);
    }
}
