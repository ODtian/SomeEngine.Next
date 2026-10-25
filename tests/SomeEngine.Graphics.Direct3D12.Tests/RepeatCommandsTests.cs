using System.Buffers.Binary;
using Xunit;

namespace SomeEngine.Graphics.Direct3D12.Tests;

public sealed class RepeatCommandsTests
{
    [Fact]
    public void RepeatingTimestampCommandsProducesNewValuesForEachSubmission()
    {
        using IGraphicsBackend backend = D3D12GraphicsBackend.Create();
        using Device device = D3D12TestSupport.CreateWarpDevice(backend);
        using QueryPool pool = backend.CreateQueryPool(device, new(QueryType.Timestamp, QueueType.Graphics, 1));
        using Buffer destination = backend.CreateBuffer(device,
            new BufferDesc(pool.ResultInfo.ResultStride, BufferUsages.QueryResolve), MemoryType.Readback);
        using CommandContext timestamp = backend.CreateCommandContext(device, new(QueueType.Graphics, 0, 1));
        using CommandContext resolve = backend.CreateCommandContext(device, new(QueueType.Graphics, 0, 1));
        Queue queue = backend.GetQueue(device, QueueType.Graphics);
        ulong previous = 0;
        for (int frame = 0; frame < 2; frame++)
        {
            RecordedCommands writes;
            if (frame == 0)
            {
                backend.Begin(timestamp);
                backend.ResetQueries(timestamp, pool, 0, 1);
                backend.WriteTimestamp(timestamp, pool, 0);
                writes = backend.End(timestamp);
            }
            else Assert.True(backend.TryRepeatCommands(timestamp, out writes));
            using (writes)
            {
                QueueCompletion written = backend.Submit(queue, new([], [], [writes], [], []));
                Assert.Equal(WaitStatus.Completed, backend.WaitCpu(written, TimeSpan.FromSeconds(10)));
            }
            backend.Begin(resolve);
            backend.ResolveQueries(resolve, pool, 0, 1, destination, BufferRange.Whole);
            using (RecordedCommands reads = backend.End(resolve))
            {
                QueueCompletion resolved = backend.Submit(queue, new([], [], [reads], [], []));
                Assert.Equal(WaitStatus.Completed, backend.WaitCpu(resolved, TimeSpan.FromSeconds(10)));
            }
            backend.CollectCompleted(device);
            using MappedBuffer result = backend.Map(destination, MapType.Read, BufferRange.Whole);
            result.Invalidate(result.Range);
            ulong current = BinaryPrimitives.ReadUInt64LittleEndian(result.Bytes);
            Assert.True(current > previous, $"Submission {frame}: previous timestamp {previous}, current {current}.");
            previous = current;
        }
    }

    [Fact]
    public void RepeatingCopiesUsesCurrentBufferContentsAndNewSubmissionTickets()
    {
        using IGraphicsBackend backend = D3D12GraphicsBackend.Create();
        using Device device = D3D12TestSupport.CreateWarpDevice(backend);
        using Buffer source = backend.CreateBuffer(device,
            new BufferDesc(64, BufferUsages.CopySource), MemoryType.Upload);
        using Buffer destination = backend.CreateBuffer(device,
            new BufferDesc(64, BufferUsages.CopyDestination), MemoryType.Readback);
        using CommandContext context = backend.CreateCommandContext(device, new(QueueType.Copy, 0, 1));
        Queue queue = backend.GetQueue(device, QueueType.Copy);
        Assert.False(backend.TryRepeatCommands(context, out _));
        for (int frame = 0; frame < 8; frame++)
        {
            byte value = checked((byte)(frame + 1));
            using (MappedBuffer mapping = backend.Map(source, MapType.Write, BufferRange.Whole))
            {
                mapping.Bytes.Fill(value);
                mapping.Flush(mapping.Range);
            }
            RecordedCommands commands;
            if (frame == 0)
            {
                backend.Begin(context);
                backend.CopyBuffer(context, new(source, 0, destination, 0, 64));
                commands = backend.End(context);
            }
            else Assert.True(backend.TryRepeatCommands(context, out commands));
            using (commands)
            {
                QueueCompletion completion = backend.Submit(queue, new([], [], [commands], [], []));
                Assert.Equal(WaitStatus.Completed, backend.WaitCpu(completion, TimeSpan.FromSeconds(10)));
            }
            backend.CollectCompleted(device);
            using MappedBuffer result = backend.Map(destination, MapType.Read, BufferRange.Whole);
            result.Invalidate(result.Range);
            Assert.All(result.Bytes.ToArray(), item => Assert.Equal(value, item));
        }
        backend.Begin(context);
        backend.CopyBuffer(context, new(source, 0, destination, 0, 32));
        backend.Discard(context);
        Assert.False(backend.TryRepeatCommands(context, out _));
    }

    [Fact]
    public void ExpiredResourcesDeclineWithoutPreventingTheNextRecording()
    {
        using IGraphicsBackend backend = D3D12GraphicsBackend.Create();
        using Device device = D3D12TestSupport.CreateWarpDevice(backend);
        using Buffer destination = backend.CreateBuffer(device,
            new BufferDesc(64, BufferUsages.CopyDestination), MemoryType.Readback);
        using CommandContext context = backend.CreateCommandContext(device, new(QueueType.Copy, 0, 1));
        using (Buffer source = backend.CreateBuffer(device,
            new BufferDesc(64, BufferUsages.CopySource), MemoryType.Upload))
        {
            backend.Begin(context);
            backend.CopyBuffer(context, new(source, 0, destination, 0, 64));
            using RecordedCommands commands = backend.End(context);
            QueueCompletion completion = backend.Submit(backend.GetQueue(device, QueueType.Copy),
                new([], [], [commands], [], []));
            Assert.Equal(WaitStatus.Completed, backend.WaitCpu(completion, TimeSpan.FromSeconds(10)));
        }
        backend.CollectCompleted(device);
        Assert.False(backend.TryRepeatCommands(context, out _));
        backend.Begin(context);
        using RecordedCommands empty = backend.End(context);
    }
}
