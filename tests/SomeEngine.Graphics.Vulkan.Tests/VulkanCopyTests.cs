namespace SomeEngine.Graphics.Vulkan.Tests;

using Xunit;

public sealed class VulkanCopyTests
{
    [Fact]
    public void Synchronization2_and_copy_commands_round_trip_buffer_bytes()
    {
        using var backend = VulkanGraphicsBackend.Create();
        DeviceQueueDesc[] queues = [new DeviceQueueDesc(QueueType.Graphics)];
        using Device device = backend.CreateDevice(new DeviceDesc(default, queues));
        Queue queue = backend.GetQueue(device, QueueType.Graphics);
        using Buffer upload = backend.CreateBuffer(
            device,
            new BufferDesc(4096, BufferUsages.CopySource),
            MemoryType.Upload);
        using Buffer gpu = backend.CreateBuffer(
            device,
            new BufferDesc(4096, BufferUsages.CopySource | BufferUsages.CopyDestination));
        using Buffer readback = backend.CreateBuffer(
            device,
            new BufferDesc(4096, BufferUsages.CopyDestination),
            MemoryType.Readback);
        using (MappedBuffer mapping = backend.Map(upload, MapType.Write, BufferRange.Whole))
        {
            for (int index = 0; index < mapping.Bytes.Length; index++)
                mapping.Bytes[index] = unchecked((byte)(index * 31));
            mapping.Flush(mapping.Range);
        }

        using CommandContext context = backend.CreateCommandContext(
            device,
            new CommandContextDesc(QueueType.Graphics, 0, 1));
        backend.Begin(context);
        backend.Barrier(context, new BufferBarrier(
            upload,
            PipelineSync.None,
            PipelineSync.Copy,
            ResourceAccess.NoAccess,
            ResourceAccess.CopySource));
        backend.Barrier(context, new BufferBarrier(
            gpu,
            PipelineSync.None,
            PipelineSync.Copy,
            ResourceAccess.NoAccess,
            ResourceAccess.CopyDestination));
        backend.CopyBuffer(context, new BufferCopy(upload, 0, gpu, 0, 4096));
        backend.Barrier(context, new BufferBarrier(
            gpu,
            PipelineSync.Copy,
            PipelineSync.Copy,
            ResourceAccess.CopyDestination,
            ResourceAccess.CopySource));
        backend.CopyBuffer(context, new BufferCopy(gpu, 0, readback, 0, 4096));
        using RecordedCommands commands = backend.End(context);
        QueueSubmitDesc submission = new([], [], [commands], [], []);
        QueueCompletion completion = backend.Submit(queue, submission);
        Assert.Equal(WaitStatus.Completed, backend.WaitCpu(completion, TimeSpan.FromSeconds(2)));

        using MappedBuffer result = backend.Map(readback, MapType.Read, BufferRange.Whole);
        result.Invalidate(result.Range);
        for (int index = 0; index < result.Bytes.Length; index++)
            Assert.Equal(unchecked((byte)(index * 31)), result.Bytes[index]);
    }

    [Fact]
    public void Copy_scope_orders_buffer_reads_and_fills()
    {
        using var messages = new SynchronizationMessages();
        System.Diagnostics.Trace.Listeners.Add(messages);
        try
        {
            using var backend = VulkanGraphicsBackend.Create(new VulkanBackendOptions(EnableDebugMessages: true));
            DeviceQueueDesc[] queues = [new DeviceQueueDesc(QueueType.Graphics)];
            using Device device = backend.CreateDevice(new DeviceDesc(default, queues));
            Queue queue = backend.GetQueue(device, QueueType.Graphics);
            using Buffer upload = backend.CreateBuffer(device,
                new BufferDesc(32, BufferUsages.CopySource), MemoryType.Upload);
            using Buffer gpu = backend.CreateBuffer(device,
                new BufferDesc(32, BufferUsages.CopySource | BufferUsages.CopyDestination));
            using Buffer readback = backend.CreateBuffer(device,
                new BufferDesc(64, BufferUsages.CopyDestination), MemoryType.Readback);
            using (MappedBuffer mapping = backend.Map(upload, MapType.Write, BufferRange.Whole))
            {
                for (int index = 0; index < mapping.Bytes.Length; index++)
                    mapping.Bytes[index] = unchecked((byte)(index * 31));
                mapping.Flush(mapping.Range);
            }

            using CommandContext context = backend.CreateCommandContext(device,
                new CommandContextDesc(QueueType.Graphics, 0, 1));
            backend.Begin(context);
            backend.Barrier(context, new BufferBarrier(upload, PipelineSync.None, PipelineSync.Copy,
                ResourceAccess.NoAccess, ResourceAccess.CopySource));
            backend.Barrier(context, new BufferBarrier(gpu, PipelineSync.None, PipelineSync.Copy,
                ResourceAccess.NoAccess, ResourceAccess.CopyDestination));
            backend.CopyBuffer(context, new BufferCopy(upload, 0, gpu, 0, 32));
            backend.Barrier(context, new BufferBarrier(gpu, PipelineSync.Copy, PipelineSync.Copy,
                ResourceAccess.CopyDestination, ResourceAccess.CopySource));
            backend.CopyBuffer(context, new BufferCopy(gpu, 0, readback, 0, 32));

            // The read must finish before fill, and the two fills must remain ordered.
            backend.Barrier(context, new BufferBarrier(gpu, PipelineSync.Copy, PipelineSync.Copy,
                ResourceAccess.CopySource, ResourceAccess.CopyDestination));
            backend.ClearBuffer(context, gpu, BufferRange.Whole, 0x11223344u);
            backend.Barrier(context, new BufferBarrier(gpu, PipelineSync.Copy, PipelineSync.Copy,
                ResourceAccess.CopyDestination, ResourceAccess.CopyDestination));
            backend.ClearBuffer(context, gpu, new BufferRange(4, 8), 0xA1B2C3D4u);
            backend.Barrier(context, new BufferBarrier(gpu, PipelineSync.Copy, PipelineSync.Copy,
                ResourceAccess.CopyDestination, ResourceAccess.CopySource));
            backend.CopyBuffer(context, new BufferCopy(gpu, 0, readback, 32, 32));
            using RecordedCommands commands = backend.End(context);
            QueueSubmitDesc submission = new([], [], [commands], [], []);
            QueueCompletion completion = backend.Submit(queue, submission);
            Assert.Equal(WaitStatus.Completed, backend.WaitCpu(completion, TimeSpan.FromSeconds(2)));

            using MappedBuffer result = backend.Map(readback, MapType.Read, BufferRange.Whole);
            result.Invalidate(result.Range);
            for (int index = 0; index < 32; index++)
                Assert.Equal(unchecked((byte)(index * 31)), result.Bytes[index]);
            for (int word = 0; word < 8; word++)
            {
                uint expected = word is 1 or 2 ? 0xA1B2C3D4u : 0x11223344u;
                Assert.Equal(expected, System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(
                    result.Bytes.Slice(32 + word * sizeof(uint), sizeof(uint))));
            }
        }
        finally
        {
            System.Diagnostics.Trace.Listeners.Remove(messages);
        }
        Assert.DoesNotContain(messages.Values, message => message.Contains("SYNC-HAZARD", StringComparison.Ordinal));
    }

    private sealed class SynchronizationMessages : System.Diagnostics.TraceListener
    {
        internal readonly System.Collections.Concurrent.ConcurrentQueue<string> Values = new();
        public override void Write(string? message)
        {
            if (message is not null) Values.Enqueue(message);
        }
        public override void WriteLine(string? message) => Write(message);
    }
}
