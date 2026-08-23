namespace SomeEngine.Graphics.Vulkan.Tests;

using Xunit;

public sealed class VulkanCopyTests
{
    [Fact]
    public void Synchronization2_and_copy_commands_round_trip_buffer_bytes()
    {
        using var backend = new VulkanBackend();
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
}
