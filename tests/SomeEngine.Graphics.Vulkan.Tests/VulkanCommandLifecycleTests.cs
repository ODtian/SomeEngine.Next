namespace SomeEngine.Graphics.Vulkan.Tests;

using Xunit;

public sealed class VulkanCommandLifecycleTests
{
    [Fact]
    public void Command_slots_submit_signal_wait_and_recycle()
    {
        using var backend = new VulkanBackend();
        DeviceQueueDesc[] queues = [new DeviceQueueDesc(QueueType.Graphics)];
        using Device device = backend.CreateDevice(new DeviceDesc(default, queues));
        Queue queue = backend.GetQueue(device, QueueType.Graphics);
        using CommandContext context = backend.CreateCommandContext(
            device,
            new CommandContextDesc(QueueType.Graphics, 0, 1));

        for (int iteration = 0; iteration < 8; iteration++)
        {
            backend.Begin(context);
            using RecordedCommands commands = backend.End(context);
            QueueSubmitDesc submission = new(
                [],
                [],
                [commands],
                [],
                []);
            QueueCompletion completion = backend.Submit(queue, submission);

            Assert.Equal(WaitStatus.Completed, backend.WaitCpu(completion, TimeSpan.FromSeconds(2)));
            Assert.True(backend.IsComplete(completion));
            Assert.Equal(RecordedCommandsStatus.Completed, commands.Status);
        }
    }

    [Fact]
    public void Discarded_recording_releases_slot_for_reuse()
    {
        using var backend = new VulkanBackend();
        DeviceQueueDesc[] queues = [new DeviceQueueDesc(QueueType.Graphics)];
        using Device device = backend.CreateDevice(new DeviceDesc(default, queues));
        using CommandContext context = backend.CreateCommandContext(
            device,
            new CommandContextDesc(QueueType.Graphics, 0, 1));

        backend.Begin(context);
        backend.Discard(context);
        backend.Begin(context);
        using RecordedCommands commands = backend.End(context);

        Assert.Equal(RecordedCommandsStatus.Executable, commands.Status);
    }

    [Fact]
    public void Recorded_commands_remain_submittable_after_context_disposal()
    {
        using var backend = new VulkanBackend();
        DeviceQueueDesc[] queues = [new DeviceQueueDesc(QueueType.Graphics)];
        using Device device = backend.CreateDevice(new DeviceDesc(default, queues));
        Queue queue = backend.GetQueue(device, QueueType.Graphics);
        CommandContext context = backend.CreateCommandContext(
            device,
            new CommandContextDesc(QueueType.Graphics, 0, 1));

        backend.Begin(context);
        using RecordedCommands commands = backend.End(context);
        context.Dispose();

        QueueCompletion completion = backend.Submit(
            queue,
            new QueueSubmitDesc([], [], [commands], [], []));
        Assert.Equal(
            WaitStatus.Completed,
            backend.WaitCpu(completion, TimeSpan.FromSeconds(5)));
        Assert.Equal(RecordedCommandsStatus.Completed, commands.Status);
    }

    [Fact]
    public void Disposed_context_keeps_executable_commands_visible_to_device_teardown()
    {
        var backend = new VulkanBackend();
        DeviceQueueDesc[] queues = [new DeviceQueueDesc(QueueType.Graphics)];
        Device device = backend.CreateDevice(new DeviceDesc(default, queues));
        CommandContext context = backend.CreateCommandContext(
            device,
            new CommandContextDesc(QueueType.Graphics, 0, 1));

        backend.Begin(context);
        using RecordedCommands commands = backend.End(context);
        context.Dispose();
        device.Dispose();

        Assert.Equal(RecordedCommandsStatus.Discarded, commands.Status);
        Assert.Null(backend.ReleaseFailure);
        backend.Dispose();
    }

    [Fact]
    public void Warm_command_frame_allocates_no_managed_memory_before_completion_wait()
    {
        using var backend = new VulkanBackend();
        DeviceQueueDesc[] queues = [new DeviceQueueDesc(QueueType.Graphics)];
        using Device device = backend.CreateDevice(new DeviceDesc(default, queues));
        Queue queue = backend.GetQueue(device, QueueType.Graphics);
        using Buffer vertex = backend.CreateBuffer(
            device,
            new BufferDesc(
                256,
                BufferUsages.Vertex | BufferUsages.CopySource),
            MemoryType.Upload);
        using Buffer readback = backend.CreateBuffer(
            device,
            new BufferDesc(256, BufferUsages.CopyDestination),
            MemoryType.Readback);
        using CommandContext context = backend.CreateCommandContext(
            device,
            new CommandContextDesc(QueueType.Graphics, 0, 1));
        var commands = new RecordedCommands[1];
        VertexBufferBinding[] vertices = [new VertexBufferBinding(vertex, 0, 16, 256)];
        Viewport[] viewports = [new Viewport(0, 0, 16, 16, 0, 1)];
        ScissorRect[] scissors = [new ScissorRect(0, 0, 16, 16)];
        BufferBarrier[] barriers = [new BufferBarrier(
            vertex,
            PipelineSync.None,
            PipelineSync.Copy,
            ResourceAccess.NoAccess,
            ResourceAccess.CopySource)];

        RecordAndSubmit(PipelineSync.None, ResourceAccess.NoAccess);
        backend.CollectCompleted(device);
        RecordAndSubmit(PipelineSync.Copy, ResourceAccess.CopySource);
        backend.CollectCompleted(device);

        long before = GC.GetAllocatedBytesForCurrentThread();
        backend.Begin(context);
        backend.SetVertexBuffers(context, 0, vertices);
        backend.SetViewports(context, viewports);
        backend.SetScissors(context, scissors);
        barriers[0] = new BufferBarrier(
            vertex,
            PipelineSync.Copy,
            PipelineSync.Copy,
            ResourceAccess.CopySource,
            ResourceAccess.CopySource);
        backend.Barrier(context, new BarrierBatch([], [], barriers, [], []));
        backend.CopyBuffer(context, new BufferCopy(vertex, 0, readback, 0, 256));
        RecordedCommands measuredCommands = backend.End(context);
        commands[0] = measuredCommands;
        QueueCompletion measured = backend.Submit(
            queue,
            new QueueSubmitDesc([], [], commands, [], []));
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        measuredCommands.Dispose();
        Assert.Equal(0, allocated);
        Assert.Equal(
            WaitStatus.Completed,
            backend.WaitCpu(measured, TimeSpan.FromSeconds(5)));
        backend.CollectCompleted(device);

        void RecordAndSubmit(PipelineSync beforeSync, ResourceAccess beforeAccess)
        {
            backend.Begin(context);
            backend.SetVertexBuffers(context, 0, vertices);
            backend.SetViewports(context, viewports);
            backend.SetScissors(context, scissors);
            barriers[0] = new BufferBarrier(
                vertex,
                beforeSync,
                PipelineSync.Copy,
                beforeAccess,
                ResourceAccess.CopySource);
            backend.Barrier(context, new BarrierBatch([], [], barriers, [], []));
            backend.CopyBuffer(context, new BufferCopy(vertex, 0, readback, 0, 256));
            using RecordedCommands warmupCommands = backend.End(context);
            commands[0] = warmupCommands;
            QueueCompletion warmup = backend.Submit(
                queue,
                new QueueSubmitDesc([], [], commands, [], []));
            Assert.Equal(
                WaitStatus.Completed,
                backend.WaitCpu(warmup, TimeSpan.FromSeconds(5)));
        }
    }
}
