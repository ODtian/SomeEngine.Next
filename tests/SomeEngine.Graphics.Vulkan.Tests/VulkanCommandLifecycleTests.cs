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
}
