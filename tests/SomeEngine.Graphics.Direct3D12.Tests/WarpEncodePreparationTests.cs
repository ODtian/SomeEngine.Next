using SomeEngine.Graphics.Direct3D12;
using Xunit;

namespace SomeEngine.Graphics.Direct3D12.Tests;

public sealed class WarpEncodePreparationTests
{
    [Fact]
    public void Second_begin_is_rejected_without_replacing_the_active_recording()
    {
        using IGraphicsBackend backend = new D3D12Backend();
        using Device device = D3D12TestSupport.CreateWarpDevice(backend);
        using Buffer upload = backend.CreateBuffer(
            device,
            new BufferDesc(64, BufferUsages.CopySource),
            MemoryType.Upload);
        using Buffer readback = backend.CreateBuffer(
            device,
            new BufferDesc(64, BufferUsages.CopyDestination),
            MemoryType.Readback);
        using CommandContext context = backend.CreateCommandContext(
            device,
            new CommandContextDesc(QueueType.Copy, 0, 1));
        Queue queue = backend.GetQueue(device, QueueType.Copy);

        backend.Begin(context, new CommandRecordingDesc(0, 0, 2));
        backend.CopyBuffer(context, new BufferCopy(upload, 0, readback, 0, 64));
        Assert.Throws<InvalidOperationException>(() => backend.Begin(context));

        using RecordedCommands commands = backend.End(context);
        QueueCompletion completion = backend.Submit(
            queue,
            new QueueSubmitDesc([], [], [commands], [], []));
        Assert.Equal(
            WaitStatus.Completed,
            backend.WaitCpu(completion, TimeSpan.FromSeconds(10)));
        backend.CollectCompleted(device);
    }

    [Fact]
    public void Warm_copy_barrier_clear_recording_allocates_no_managed_memory()
    {
        using IGraphicsBackend backend = new D3D12Backend();
        using Device device = D3D12TestSupport.CreateWarpDevice(backend);
        using Buffer upload = backend.CreateBuffer(
            device,
            new BufferDesc(256, BufferUsages.CopySource),
            MemoryType.Upload);
        using Buffer destination = backend.CreateBuffer(
            device,
            new BufferDesc(256, BufferUsages.CopySource | BufferUsages.CopyDestination),
            MemoryType.DeviceLocal);
        using Buffer readback = backend.CreateBuffer(
            device,
            new BufferDesc(256, BufferUsages.CopyDestination),
            MemoryType.Readback);
        using CommandContext context = backend.CreateCommandContext(
            device,
            new CommandContextDesc(QueueType.Copy, 0, 1));
        Queue queue = backend.GetQueue(device, QueueType.Copy);

        RecordSubmitWait(backend, device, context, queue, upload, destination, readback);

        long before = GC.GetAllocatedBytesForCurrentThread();
        backend.Begin(context, new CommandRecordingDesc(0, 0, 8));
        backend.CopyBuffer(context, new BufferCopy(upload, 0, destination, 0, 256));
        backend.Barrier(context, new BufferBarrier(
            destination,
            PipelineSync.Copy,
            PipelineSync.Copy,
            ResourceAccess.CopyDestination,
            ResourceAccess.CopySource));
        backend.CopyBuffer(context, new BufferCopy(destination, 0, readback, 0, 256));
        using RecordedCommands measuredCommands = backend.End(context);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocated);
        QueueCompletion completion = backend.Submit(
            queue,
            new QueueSubmitDesc([], [], [measuredCommands], [], []));
        Assert.Equal(
            WaitStatus.Completed,
            backend.WaitCpu(completion, TimeSpan.FromSeconds(10)));
        backend.CollectCompleted(device);
    }

    [Fact]
    public void Invalid_second_native_borrow_resource_preserves_recording_for_retry()
    {
        using var backend = new D3D12Backend();
        using Device device = D3D12TestSupport.CreateWarpDevice(backend);
        using Buffer upload = backend.CreateBuffer(
            device,
            new BufferDesc(64, BufferUsages.CopySource),
            MemoryType.Upload);
        using Buffer readback = backend.CreateBuffer(
            device,
            new BufferDesc(64, BufferUsages.CopyDestination),
            MemoryType.Readback);
        using CommandContext context = backend.CreateCommandContext(
            device,
            new CommandContextDesc(QueueType.Copy, 0, 1));
        Queue queue = backend.GetQueue(device, QueueType.Copy);

        backend.Begin(context, new CommandRecordingDesc(0, 0, 1));
        backend.CopyBuffer(context, new BufferCopy(upload, 0, readback, 0, 64));
        Resource[] invalid = [upload, null!];
        Assert.Throws<ArgumentException>(() => backend.BorrowCommandList(context, invalid));

        D3D12CommandListBorrow borrow = backend.BorrowCommandList(context, [upload, readback]);
        Assert.True(borrow.IsValid);
        using RecordedCommands commands = backend.End(context);
        QueueCompletion completion = backend.Submit(
            queue,
            new QueueSubmitDesc([], [], [commands], [], []));
        Assert.Equal(
            WaitStatus.Completed,
            backend.WaitCpu(completion, TimeSpan.FromSeconds(10)));
        Assert.Equal(DeviceStatus.Active, device.Status);
        backend.CollectCompleted(device);
    }

    private static void RecordSubmitWait(
        IGraphicsBackend backend,
        Device device,
        CommandContext context,
        Queue queue,
        Buffer upload,
        Buffer destination,
        Buffer readback)
    {
        backend.Begin(context, new CommandRecordingDesc(0, 0, 8));
        backend.CopyBuffer(context, new BufferCopy(upload, 0, destination, 0, 256));
        backend.Barrier(context, new BufferBarrier(
            destination,
            PipelineSync.Copy,
            PipelineSync.Copy,
            ResourceAccess.CopyDestination,
            ResourceAccess.CopySource));
        backend.CopyBuffer(context, new BufferCopy(destination, 0, readback, 0, 256));
        using RecordedCommands commands = backend.End(context);
        QueueCompletion completion = backend.Submit(
            queue,
            new QueueSubmitDesc([], [], [commands], [], []));
        Assert.Equal(
            WaitStatus.Completed,
            backend.WaitCpu(completion, TimeSpan.FromSeconds(10)));
        backend.CollectCompleted(device);
    }
}
