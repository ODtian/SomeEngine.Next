using SomeEngine.Graphics.Direct3D12;
using Xunit;

namespace SomeEngine.Graphics.Direct3D12.Tests;

public sealed class BufferMappingTests
{
    [Fact]
    public void Warp_mapping_flushes_upload_and_invalidates_readback_ranges()
    {
        Assert.True(OperatingSystem.IsWindows());
        using Device device = new(new Options
        {
            UseWarpAdapter = true,
            EnableDebugLayer = true,
            EnableGpuValidation = false,
        });
        BufferHandle upload = device.CreateBuffer(new BufferDesc(32, BufferUsage.CopySource), MemoryType.Upload);
        using (BufferMapping mapping = device.MapBuffer(upload, BufferMapMode.Write, new BufferRange(5, 11)))
        {
            for (int index = 0; index < mapping.Span.Length; index++) mapping.Span[index] = checked((byte)(31 + index));
            Assert.Throws<InvalidOperationException>(() =>
                device.MapBuffer(upload, BufferMapMode.Write, BufferRange.Whole));
            Assert.Throws<InvalidOperationException>(() => device.DestroyBuffer(upload));
        }
        Assert.Throws<InvalidOperationException>(() => device.MapBuffer(upload, BufferMapMode.Read, BufferRange.Whole));

        BufferHandle readback = device.CreateBuffer(new BufferDesc(32, BufferUsage.CopyDestination), MemoryType.Readback);
        GpuCompletion completion;
        using (ICommandContext commands = device.AcquireCommandContext(QueueType.Copy))
        {
            commands.CopyBuffer(upload, 0, readback, 0, 32);
            completion = device.Submit(QueueType.Copy, [commands.Finish()]);
        }
        Assert.True(device.Wait(completion, TimeSpan.FromSeconds(10)));
        BufferMapping read = device.MapBuffer(readback, BufferMapMode.Read, new BufferRange(5, 11));
        Assert.Equal(Enumerable.Range(31, 11).Select(static value => (byte)value).ToArray(), read.Span.ToArray());
        read.Dispose();
        Assert.True(read.IsDisposed);
        bool disposedAccessRejected = false;
        try { _ = read.Span.Length; }
        catch (ObjectDisposedException) { disposedAccessRejected = true; }
        Assert.True(disposedAccessRejected);

        device.DestroyBuffer(readback);
        device.DestroyBuffer(upload);
        device.CollectGarbage();
        Assert.DoesNotContain(device.DrainDiagnostics(), static diagnostic =>
            diagnostic.Severity is GraphicsDiagnosticSeverity.Error or GraphicsDiagnosticSeverity.Corruption);
    }
}
