namespace SomeEngine.Job.Tests;

public sealed class CompletionStateTests
{
    private readonly LifecycleData _data = new();

    public CompletionStateTests()
    {
        JobSystem.Initialize(new JobRuntimeConfig { WorkerCount = 0 });
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
        _data.Counter = 0;
        var handle = JobSystem.Schedule(new LifecycleData.IncrementJob(_data));

        handle.Complete();
        handle.Complete();

        Assert.Equal(1, _data.Counter);
        Assert.True(handle.IsCompleted);
    }

    [Fact]
    public void SequentialJobsDoNotExhaustCompletionStatePool()
    {
        _data.Counter = 0;

        for (var i = 0; i < 1_000; i++)
        {
            JobSystem.Schedule(new LifecycleData.IncrementJob(_data)).Complete();
        }

        Assert.Equal(1_000, _data.Counter);
    }

    [Fact]
    public void OldHandleStaysSafeAfterStateReuse()
    {
        var old = JobSystem.Schedule(new LifecycleData.NoopJob());
        old.Complete();

        var reused = JobSystem.Schedule(new LifecycleData.NoopJob());

        Assert.True(old.IsCompleted);
        old.Complete();

        reused.Complete();
        Assert.True(reused.IsCompleted);
    }

    private sealed class LifecycleData
    {
        internal int Counter;

        internal struct IncrementJob : IJob
        {
            private readonly LifecycleData _data;

            internal IncrementJob(LifecycleData data) => _data = data;

            public void Execute()
            {
                _data.Counter++;
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
