namespace SomeEngine.Job.Tests;

public sealed class CompletionStateTests
{
    public CompletionStateTests()
    {
        JobSystem.ResetForTesting(workerCount: 0);
    }

    [Fact]
    public void DefaultHandleIsCompleted()
    {
        var handle = default(JobHandle);

        Assert.True(handle.IsCompleted);
        handle.Complete();
    }

    [Fact]
    public void CompleteIsIdempotent()
    {
        LifecycleJobs.Counter = 0;
        var handle = JobSystem.Schedule(new LifecycleJobs.IncrementJob());

        handle.Complete();
        handle.Complete();

        Assert.Equal(1, LifecycleJobs.Counter);
        Assert.True(handle.IsCompleted);
    }

    [Fact]
    public void SequentialJobsDoNotExhaustCompletionStatePool()
    {
        LifecycleJobs.Counter = 0;

        for (var i = 0; i < 1_000; i++)
        {
            JobSystem.Schedule(new LifecycleJobs.IncrementJob()).Complete();
        }

        Assert.Equal(1_000, LifecycleJobs.Counter);
    }

    [Fact]
    public void OldHandleStaysSafeAfterStateReuse()
    {
        var old = JobSystem.Schedule(new LifecycleJobs.NoopJob());
        old.Complete();

        var reused = JobSystem.Schedule(new LifecycleJobs.NoopJob());

        Assert.Equal(old.Index, reused.Index);
        Assert.NotEqual(old.Version, reused.Version);
        Assert.True(old.IsCompleted);
        old.Complete();

        reused.Complete();
        Assert.True(reused.IsCompleted);
    }

    private static class LifecycleJobs
    {
        internal static int Counter;

        internal struct IncrementJob : IJob
        {
            public void Execute()
            {
                Counter++;
            }
        }

        internal struct NoopJob : IJob
        {
            public void Execute()
            {
            }
        }
    }
}
