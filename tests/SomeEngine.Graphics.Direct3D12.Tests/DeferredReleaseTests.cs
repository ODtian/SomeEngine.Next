using SomeEngine.Graphics.Validation;
using Xunit;

namespace SomeEngine.Graphics.Direct3D12.Tests;

public sealed class DeferredReleaseTests
{
    [Fact]
    public void SubmittedCopyKeepsDisposedSourceAliveUntilCollection()
    {
        using IGraphicsBackend backend = D3D12GraphicsBackend.Create();
        using Device device = D3D12TestSupport.CreateWarpDevice(backend);
        using Buffer source = backend.CreateBuffer(device, new BufferDesc(64, BufferUsages.CopySource), MemoryType.Upload);
        using Buffer destination = backend.CreateBuffer(device, new BufferDesc(64, BufferUsages.CopyDestination), MemoryType.Readback);
        using (MappedBuffer data = backend.Map(source, MapType.Write, BufferRange.Whole))
        {
            data.Bytes.Fill(7);
            data.Flush(data.Range);
        }
        using CommandContext context = backend.CreateCommandContext(device, new(QueueType.Copy, 0, 1));
        backend.Begin(context);
        backend.CopyBuffer(context, new(source, 0, destination, 0, 64));
        using RecordedCommands commands = backend.End(context);
        QueueCompletion completion = backend.Submit(backend.GetQueue(device, QueueType.Copy), new([], [], [commands], [], []));
        Assert.True(backend.TryGetCapability(device, out D3D12Diagnostics? diagnostics));
        int liveObjects = diagnostics!.LiveNativeObjects;

        source.Dispose();
        Assert.Equal(WaitStatus.Completed, backend.WaitCpu(completion, TimeSpan.FromSeconds(10)));
        backend.CollectCompleted(device);

        using MappedBuffer result = backend.Map(destination, MapType.Read, BufferRange.Whole);
        result.Invalidate(result.Range);
        Assert.All(result.Bytes.ToArray(), value => Assert.Equal(7, value));
        Assert.Equal(liveObjects - 1, diagnostics.LiveNativeObjects);
    }

    [Fact]
    public void DisposingARecordedResourceReportsItsValidationCode()
    {
        using ValidationLayer backend = new(D3D12GraphicsBackend.Create());
        using Device device = D3D12TestSupport.CreateWarpDevice(backend);
        using Buffer source = backend.CreateBuffer(device, new BufferDesc(64, BufferUsages.CopySource), MemoryType.Upload);
        using Buffer destination = backend.CreateBuffer(device, new BufferDesc(64, BufferUsages.CopyDestination), MemoryType.Readback);
        using CommandContext context = backend.CreateCommandContext(device, new(QueueType.Copy, 0, 1));
        backend.Begin(context);
        backend.CopyBuffer(context, new(source, 0, destination, 0, 64));
        using RecordedCommands commands = backend.End(context);

        GraphicsValidationException failure = Assert.Throws<GraphicsValidationException>(source.Dispose);
        Assert.Equal(ValidationError.ResourceDisposedWhileRecorded, failure.Error);
        commands.Dispose();
        source.Dispose();
        Assert.Equal(RecordedCommandsStatus.Discarded, commands.Status);
    }
}
