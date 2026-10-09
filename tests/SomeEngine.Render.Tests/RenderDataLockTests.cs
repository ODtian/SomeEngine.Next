using SomeEngine.Graphics;
using SomeEngine.Render.Frame;

namespace SomeEngine.Render.Tests;

public sealed class RenderDataLockTests
{
    [Fact]
    public void PrepareExcludesReadsUntilReleased()
    {
        var dataLock = new RenderDataLock();
        Assert.True(dataLock.TryBeginPrepare(out var prepare));
        using (prepare)
        {
            Assert.False(dataLock.TryBeginRead(out _));
        }
        Assert.True(dataLock.TryBeginRead(out var read));
        read.Dispose();
    }

    [Fact]
    public void PrepareExcludesAnotherPrepareUntilReleased()
    {
        var dataLock = new RenderDataLock();
        Assert.True(dataLock.TryBeginPrepare(out var first));
        using (first)
        {
            Assert.False(dataLock.TryBeginPrepare(out _));
        }
        Assert.True(dataLock.TryBeginPrepare(out var second));
        second.Dispose();
    }

    [Fact]
    public void ReadersCoexistAndExcludePrepareUntilTheLastReaderReleases()
    {
        var dataLock = new RenderDataLock();
        Assert.True(dataLock.TryBeginRead(out var first));
        Assert.True(dataLock.TryBeginRead(out var second));
        using (first)
        using (second)
        {
            Assert.False(dataLock.TryBeginPrepare(out _));
            first.Dispose();
            Assert.False(dataLock.TryBeginPrepare(out _));
        }
        Assert.True(dataLock.TryBeginPrepare(out var prepare));
        prepare.Dispose();
    }

    [Fact]
    public void ReleasingScopesRepeatedlyPreservesSubsequentAccess()
    {
        var dataLock = new RenderDataLock();
        for (int index = 0; index < 32; index++)
        {
            Assert.True(dataLock.TryBeginPrepare(out var prepare));
            prepare.Dispose();
            prepare.Dispose();
            Assert.True(dataLock.TryBeginRead(out var read));
            read.Dispose();
            read.Dispose();
        }
    }

    [Fact]
    public async Task ReadersOnDifferentThreadsHoldPrepareUntilBothRelease()
    {
        var dataLock = new RenderDataLock();
        using var ready = new CountdownEvent(2);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task[] readers = Enumerable.Range(0, 2).Select(_ => Task.Run(async () =>
        {
            Assert.True(dataLock.TryBeginRead(out var read));
            using (read)
            {
                ready.Signal();
                await release.Task;
            }
        })).ToArray();
        try
        {
            Assert.True(ready.Wait(TimeSpan.FromSeconds(10)));
            Assert.False(dataLock.TryBeginPrepare(out _));
        }
        finally
        {
            release.SetResult();
            await Task.WhenAll(readers);
        }
        Assert.True(dataLock.TryBeginPrepare(out var prepare));
        prepare.Dispose();
    }

    [Fact]
    public void CpuAccessBehavesTheSameBeforeAndAfterGpuCompletion()
    {
        using var test = new RenderTestDevice(DeviceFeatures.ExternalTimelines,
            [new DeviceQueueDesc(QueueType.Graphics), new DeviceQueueDesc(QueueType.Compute)]);
        var dataLock = new RenderDataLock();
        var graphics = test.Backend.GetQueue(test.Device, QueueType.Graphics);
        var compute = test.Backend.GetQueue(test.Device, QueueType.Compute);
        using var gate = test.Backend.CreateExternalTimeline(test.Device, 0);
        QueueCompletion completion = test.Backend.Submit(graphics, new([], [new(gate, 1)], [], [], []));
        try
        {
            Assert.False(test.Backend.IsComplete(completion));
            Assert.True(dataLock.TryBeginRead(out var read));
            using (read)
            {
                Assert.False(dataLock.TryBeginPrepare(out _));
            }
            Assert.True(dataLock.TryBeginPrepare(out var prepare));
            using (prepare)
            {
                Assert.False(dataLock.TryBeginRead(out _));
                Assert.False(test.Backend.IsComplete(completion));
            }
        }
        finally
        {
            _ = test.Backend.Submit(compute, new([], [], [], [], [new(gate, 1)]));
            Assert.Equal(WaitStatus.Completed, test.Backend.WaitCpu(completion, TimeSpan.FromSeconds(10)));
        }
        Assert.True(dataLock.TryBeginRead(out var completedRead));
        using (completedRead)
        {
            Assert.False(dataLock.TryBeginPrepare(out _));
        }
        Assert.True(dataLock.TryBeginPrepare(out var completedPrepare));
        using (completedPrepare)
        {
            Assert.False(dataLock.TryBeginRead(out _));
        }
    }
}
