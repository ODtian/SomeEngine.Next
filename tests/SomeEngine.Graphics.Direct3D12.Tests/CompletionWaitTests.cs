using SomeEngine.Graphics.Direct3D12;
using Xunit;

namespace SomeEngine.Graphics.Direct3D12.Tests;

public sealed class CompletionWaitTests
{
    [Fact]
    public void Warp_WaitIdle_waits_for_the_exact_multi_queue_snapshot()
    {
        Assert.True(OperatingSystem.IsWindows());
        using Device device = new(new Options { UseWarpAdapter = true });
        Assert.True(device.WaitIdle(TimeSpan.Zero));
        BufferHandle source = device.CreateBuffer(new BufferDesc(4, BufferUsage.CopySource), MemoryType.Upload);
        BufferHandle destination = device.CreateBuffer(new BufferDesc(4, BufferUsage.CopyDestination), MemoryType.Readback);
        using (ICommandContext commands = device.AcquireCommandContext(QueueType.Copy))
        {
            commands.CopyBuffer(source, 0, destination, 0, 4);
            device.Submit(QueueType.Copy, [commands.Finish()]);
        }
        Assert.True(device.WaitIdle(TimeSpan.FromSeconds(10)));
        device.DestroyBuffer(destination);
        device.DestroyBuffer(source);
        device.CollectGarbage();
    }
}
