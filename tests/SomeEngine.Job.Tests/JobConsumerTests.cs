using SomeEngine.Job;

namespace SomeEngine.Job.Tests;

public sealed class JobConsumerTests
{
    [Fact]
    public void ScheduledJobCompletesAndPublishesResult()
    {
        CounterJob.Reset();

        JobSystem.Schedule(new CounterJob()).Complete();

        Assert.Equal(1, CounterJob.Count);
    }

    private struct CounterJob : IJob
    {
        internal static int Count;

        internal static void Reset()
        {
            Count = 0;
        }

        public void Execute()
        {
            Interlocked.Increment(ref Count);
        }
    }
}
