namespace SomeEngine.Job.Tests;

public sealed class JobConsumerTests
{
    [Fact]
    public void ScheduledJobCompletesAndPublishesResult()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 1 });
        int[] counts = [0];
        scheduler.Schedule(new CounterJob(counts)).Complete();
        Assert.Equal(1, counts[0]);
    }

    private readonly struct CounterJob(int[] counts) : IJob
    {
        public void Execute() => Interlocked.Increment(ref counts[0]);
    }
}
