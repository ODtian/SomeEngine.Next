namespace SomeEngine.Job.Tests;

public sealed class SchedulerStatisticsTests
{
    [Fact]
    public void EachSchedulerKeepsItsOwnJobTotals()
    {
        using var first = new Scheduler(new JobRuntimeConfig { WorkerCount = 0, EnableCounters = true });
        using var second = new Scheduler(new JobRuntimeConfig { WorkerCount = 0, EnableCounters = true });
        int[] counts = [0, 0];
        JobRuntimeStats initialFirst = first.GetStats();
        JobRuntimeStats initialSecond = second.GetStats();

        Assert.Equal((0L, 0L, 0L),
            (initialFirst.ScheduledJobs, initialFirst.ExecutedWorkItems, initialFirst.CompletedHandles));
        Assert.Equal((0L, 0L, 0L),
            (initialSecond.ScheduledJobs, initialSecond.ExecutedWorkItems, initialSecond.CompletedHandles));

        first.Schedule(new Increment(counts, 0)).Complete();
        first.Schedule(new Increment(counts, 0)).Complete();
        JobRuntimeStats earlier = first.GetStats();
        second.Schedule(new Increment(counts, 1)).Complete();
        first.Schedule(new Increment(counts, 0)).Complete();

        JobRuntimeStats firstTotals = first.GetStats();
        JobRuntimeStats secondTotals = second.GetStats();
        Assert.Equal(new[] { 3, 1 }, counts);
        Assert.Equal((2L, 2L, 2L),
            (earlier.ScheduledJobs, earlier.ExecutedWorkItems, earlier.CompletedHandles));
        Assert.Equal((3L, 3L, 3L),
            (firstTotals.ScheduledJobs, firstTotals.ExecutedWorkItems, firstTotals.CompletedHandles));
        Assert.Equal((1L, 1L, 1L),
            (secondTotals.ScheduledJobs, secondTotals.ExecutedWorkItems, secondTotals.CompletedHandles));

        first.Dispose();
        second.Dispose();
        JobRuntimeStats stoppedFirst = first.GetStats();
        JobRuntimeStats stoppedSecond = second.GetStats();
        Assert.Equal((3L, 3L, 3L),
            (stoppedFirst.ScheduledJobs, stoppedFirst.ExecutedWorkItems, stoppedFirst.CompletedHandles));
        Assert.Equal((1L, 1L, 1L),
            (stoppedSecond.ScheduledJobs, stoppedSecond.ExecutedWorkItems, stoppedSecond.CompletedHandles));
    }

    [Fact]
    public void DisabledCountersLeaveJobTotalsEmptyWhileJobsRun()
    {
        using var enabled = new Scheduler(new JobRuntimeConfig { WorkerCount = 0, EnableCounters = true });
        using var disabled = new Scheduler(new JobRuntimeConfig { WorkerCount = 0, EnableCounters = false });
        int[] counts = [0, 0];

        enabled.Schedule(new Increment(counts, 0)).Complete();
        disabled.Schedule(new Increment(counts, 1)).Complete();
        enabled.Schedule(new Increment(counts, 0)).Complete();

        JobRuntimeStats enabledTotals = enabled.GetStats();
        JobRuntimeStats disabledTotals = disabled.GetStats();
        Assert.Equal(new[] { 2, 1 }, counts);
        Assert.Equal((2L, 2L, 2L),
            (enabledTotals.ScheduledJobs, enabledTotals.ExecutedWorkItems, enabledTotals.CompletedHandles));
        Assert.Equal((0L, 0L, 0L),
            (disabledTotals.ScheduledJobs, disabledTotals.ExecutedWorkItems, disabledTotals.CompletedHandles));
        Assert.Equal(0, disabledTotals.QueuedWorkItems);
        Assert.Equal(0, disabledTotals.RefContainingJobs);
    }

    [Fact]
    public void NewSchedulingThreadsContributeToCombinedJobTotals()
    {
        const int producerCount = 4;
        TimeSpan timeout = TimeSpan.FromSeconds(10);
        using var scheduler = new Scheduler(new JobRuntimeConfig
        {
            WorkerCount = 0,
            EnableCounters = true,
            MaxQueuedWorkItems = 8,
        });
        using var submitted = new CountdownEvent(producerCount);
        using var complete = new ManualResetEventSlim();
        int[] counts = new int[producerCount];
        Exception?[] failures = new Exception?[producerCount];
        Thread[] producers = new Thread[producerCount];
        for (int index = 0; index < producers.Length; index++)
        {
            int producer = index;
            producers[index] = new Thread(() =>
            {
                try
                {
                    JobHandle handle = scheduler.Schedule(new Increment(counts, producer));
                    submitted.Signal();
                    if (!complete.Wait(timeout))
                        throw new TimeoutException();
                    handle.Complete();
                }
                catch (Exception failure)
                {
                    failures[producer] = failure;
                }
            }) { IsBackground = true };
        }

        int started = 0;
        bool allSubmitted = false;
        bool allJoined = true;
        JobRuntimeStats queued = default;
        try
        {
            foreach (Thread producer in producers)
            {
                producer.Start();
                started++;
            }
            allSubmitted = submitted.Wait(timeout);
            if (allSubmitted)
                queued = scheduler.GetStats();
        }
        finally
        {
            complete.Set();
            for (int index = 0; index < started; index++)
                allJoined &= producers[index].Join(timeout);
        }

        Assert.True(allJoined);
        Assert.All(failures, failure => Assert.Null(failure));
        Assert.True(allSubmitted);
        Assert.Equal((4L, 0L, 0L),
            (queued.ScheduledJobs, queued.ExecutedWorkItems, queued.CompletedHandles));
        Assert.All(counts, count => Assert.Equal(1, count));
        JobRuntimeStats finished = scheduler.GetStats();
        Assert.Equal((4L, 4L, 4L),
            (finished.ScheduledJobs, finished.ExecutedWorkItems, finished.CompletedHandles));
    }

    private readonly struct Increment(int[] counts, int index) : IJob
    {
        public void Execute() => Interlocked.Increment(ref counts[index]);
    }
}
