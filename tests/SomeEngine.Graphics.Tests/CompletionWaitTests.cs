using SomeEngine.Graphics.Null;
using Xunit;

namespace SomeEngine.Graphics.Tests;

public sealed class CompletionWaitTests
{
    [Fact]
    public void WaitIdle_snapshots_all_published_queues_and_honors_timeout()
    {
        using Device device = new(new Options { AutoCompleteSubmissions = false });
        Assert.True(device.WaitIdle(TimeSpan.Zero));
        BufferHandle source = device.CreateBuffer(new BufferDesc(4, BufferUsage.CopySource), MemoryType.Upload);
        BufferHandle destination = device.CreateBuffer(new BufferDesc(4, BufferUsage.CopyDestination), MemoryType.Readback);
        GpuCompletion completion;
        using (ICommandContext commands = device.AcquireCommandContext(QueueType.Copy))
        {
            commands.CopyBuffer(source, 0, destination, 0, 4);
            completion = device.Submit(QueueType.Copy, [commands.Finish()]);
        }
        Assert.False(device.WaitIdle(TimeSpan.Zero));
        device.AdvanceCompletion(completion);
        Assert.True(device.WaitIdle(TimeSpan.Zero));
        device.DestroyBuffer(destination);
        device.DestroyBuffer(source);
    }
}
