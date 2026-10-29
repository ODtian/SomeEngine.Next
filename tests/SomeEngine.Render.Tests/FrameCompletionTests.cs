using SomeEngine.Graphics;

namespace SomeEngine.Render.Tests;

public sealed class FrameCompletionTests
{
    [Fact]
    public void PrepareWaitsForTheLatestObservedCompletionOnEveryQueue()
    {
        using var test = new RenderTestDevice(DeviceFeatures.ExternalTimelines,
            [new(QueueType.Graphics), new(QueueType.Compute), new(QueueType.Copy)]);
        var graphics = test.Backend.GetQueue(test.Device, QueueType.Graphics);
        var compute = test.Backend.GetQueue(test.Device, QueueType.Compute);
        var signalQueue = test.Backend.GetQueue(test.Device, QueueType.Copy);
        using var gate = test.Backend.CreateExternalTimeline(test.Device, 0);
        QueueCompletion first = test.Backend.Submit(graphics, new([], [new(gate, 1)], [], [], []));
        QueueCompletion latest = test.Backend.Submit(graphics, new([], [new(gate, 3)], [], [], []));
        QueueCompletion other = test.Backend.Submit(compute, new([], [new(gate, 2)], [], [], []));
        try
        {
            Assert.True(test.Coordinator.TryBeginObservation(out var firstRead));
            using (firstRead) firstRead.Complete([latest, first, latest]);
            Assert.True(test.Coordinator.TryBeginObservation(out var secondRead));
            using (secondRead) secondRead.Complete([other, first]);
            Assert.False(test.Coordinator.TryBeginPrepare(out _));

            _ = test.Backend.Submit(signalQueue, new([], [], [], [], [new(gate, 1)]));
            Assert.Equal(WaitStatus.Completed, test.Backend.WaitCpu(first, TimeSpan.FromSeconds(10)));
            Assert.False(test.Coordinator.TryBeginPrepare(out _));

            _ = test.Backend.Submit(signalQueue, new([], [], [], [], [new(gate, 2)]));
            Assert.Equal(WaitStatus.Completed, test.Backend.WaitCpu(other, TimeSpan.FromSeconds(10)));
            Assert.False(test.Coordinator.TryBeginPrepare(out _));

            _ = test.Backend.Submit(signalQueue, new([], [], [], [], [new(gate, 3)]));
            Assert.Equal(WaitStatus.Completed, test.Backend.WaitCpu(latest, TimeSpan.FromSeconds(10)));
            Assert.True(test.Coordinator.TryBeginPrepare(out var prepare));
            using (prepare) prepare.Commit();
        }
        finally
        {
            QueueCompletion released = test.Backend.Submit(signalQueue, new([], [], [], [], [new(gate, 4)]));
            Assert.Equal(WaitStatus.Completed, test.Backend.WaitCpu(released, TimeSpan.FromSeconds(10)));
            Assert.Equal(WaitStatus.Completed, test.Backend.WaitCpu(latest, TimeSpan.FromSeconds(10)));
            Assert.Equal(WaitStatus.Completed, test.Backend.WaitCpu(other, TimeSpan.FromSeconds(10)));
        }
    }
}
