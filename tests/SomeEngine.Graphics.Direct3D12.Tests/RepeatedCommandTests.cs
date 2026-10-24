using SomeEngine.Graphics.Validation;
using Xunit;

namespace SomeEngine.Graphics.Direct3D12.Tests;

public sealed class RepeatedCommandTests
{
    [Fact]
    public void RepeatedCopiesReadCurrentDataAndFollowResourceAndRangeChanges()
    {
        using ValidationLayer backend = new(D3D12GraphicsBackend.Create());
        using Device device = D3D12TestSupport.CreateWarpDevice(backend);
        using Buffer destination = backend.CreateBuffer(device,
            new BufferDesc(64, BufferUsages.CopyDestination), MemoryType.Readback);
        using CommandContext context = backend.CreateCommandContext(device, new(QueueType.Copy, 0, 1));
        Queue queue = backend.GetQueue(device, QueueType.Copy);
        Buffer source = CreateSource();
        try
        {
            for (int frame = 0; frame < 24; frame++)
            {
                if (frame == 12)
                {
                    source.Dispose();
                    source = CreateSource();
                }
                byte[] data = Enumerable.Range(0, 96).Select(index => (byte)(index + frame * 7)).ToArray();
                using (MappedBuffer mapping = backend.Map(source, MapType.Write, BufferRange.Whole))
                {
                    data.CopyTo(mapping.Bytes);
                    mapping.Flush(new(0, 96));
                }
                ulong offset = frame < 8 ? 0ul : (ulong)(frame % 2 * 16);
                if (frame % 5 == 0)
                {
                    backend.Begin(context);
                    backend.CopyBuffer(context, new(source, 32, destination, 0, 64));
                    backend.Discard(context);
                }
                backend.Begin(context);
                backend.CopyBuffer(context, new(source, offset, destination, 0, 64));
                using RecordedCommands commands = backend.End(context);
                // Current command references must survive public wrapper disposal,
                // including when the same copy has been submitted previously.
                if (frame == 23)
                    source.Dispose();
                QueueCompletion completion = backend.Submit(queue, new([], [], [commands], [], []));
                Assert.Equal(WaitStatus.Completed, backend.WaitCpu(completion, TimeSpan.FromSeconds(10)));
                using MappedBuffer result = backend.Map(destination, MapType.Read, BufferRange.Whole);
                result.Invalidate(new(0, 64));
                Assert.Equal(data.AsSpan((int)offset, 64).ToArray(), result.Bytes.ToArray());
                backend.CollectCompleted(device);
            }
        }
        finally
        {
            source.Dispose();
        }

        Buffer CreateSource() => backend.CreateBuffer(device,
            new BufferDesc(96, BufferUsages.CopySource), MemoryType.Upload);
    }

    [Fact]
    public unsafe void NativeCopiesBetweenManagedCopiesPreserveOrderAcrossRepeatedRecordings()
    {
        using ValidationLayer backend = new(D3D12GraphicsBackend.Create());
        using Device device = D3D12TestSupport.CreateWarpDevice(backend);
        using Buffer source = backend.CreateBuffer(device,
            new BufferDesc(96, BufferUsages.CopySource), MemoryType.Upload);
        using Buffer destination = backend.CreateBuffer(device,
            new BufferDesc(96, BufferUsages.CopyDestination), MemoryType.Readback);
        using CommandContext context = backend.CreateCommandContext(device, new(QueueType.Copy, 0, 1));
        Queue queue = backend.GetQueue(device, QueueType.Copy);

        for (int frame = 0; frame < 16; frame++)
        {
            byte[] data = Enumerable.Range(0, 96).Select(index => (byte)(index * 3 + frame)).ToArray();
            using (MappedBuffer mapping = backend.Map(source, MapType.Write, BufferRange.Whole))
            {
                data.CopyTo(mapping.Bytes);
                mapping.Flush(new(0, 96));
            }
            backend.Begin(context);
            backend.CopyBuffer(context, new(source, 0, destination, 0, 32));
            if (frame % 4 == 3)
            {
                D3D12CommandListBorrow native = backend.BorrowCommandList(context, [source, destination]);
                native.Pointer->CopyBufferRegion(backend.GetNativeResource(destination), 32,
                    backend.GetNativeResource(source), 32, 32);
            }
            else
            {
                backend.CopyBuffer(context, new(source, 32, destination, 32, 32));
            }
            backend.CopyBuffer(context, new(source, 64, destination, 64, 32));
            using RecordedCommands commands = backend.End(context);
            QueueCompletion completion = backend.Submit(queue, new([], [], [commands], [], []));
            Assert.Equal(WaitStatus.Completed, backend.WaitCpu(completion, TimeSpan.FromSeconds(10)));
            using MappedBuffer result = backend.Map(destination, MapType.Read, BufferRange.Whole);
            result.Invalidate(new(0, 96));
            Assert.Equal(data, result.Bytes.ToArray());
            backend.CollectCompleted(device);
        }
    }
}
