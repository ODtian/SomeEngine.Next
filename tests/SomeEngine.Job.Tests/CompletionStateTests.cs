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
    public void RepeatedCompletionObservesTheSameFailure()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 0 });
        var failure = new ApplicationException();
        JobHandle handle = scheduler.Schedule(new FailingJob(failure));

        Assert.Same(failure, Assert.Throws<ApplicationException>(handle.Complete));
        Assert.Same(failure, Assert.Throws<ApplicationException>(handle.Complete));
    }

    private readonly struct FailingJob(Exception failure) : IJob
    {
        public void Execute() => throw failure;
    }

    [Fact]
    public async Task ConcurrentCompletionWaitersObserveTheSameFailure()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 0 });
        using var firstEntered = new ManualResetEventSlim();
        using var secondEntered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var failure = new ApplicationException();
        JobHandle handle = scheduler.Schedule(new WaitingFailure(firstEntered, release, failure));
        Task<Exception> first = Task.Run(() => Record.Exception(handle.Complete));
        try
        {
            Assert.True(firstEntered.Wait(TimeSpan.FromSeconds(10)));
            JobHandle helper = scheduler.Schedule(new WaitingSuccess(secondEntered, release));
            Task<Exception> second = Task.Run(() => Record.Exception(handle.Complete));
            Assert.True(secondEntered.Wait(TimeSpan.FromSeconds(10)));
            release.Set();

            Exception[] results = await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.All(results, result => Assert.Same(failure, result));
            helper.Complete();
        }
        finally
        {
            release.Set();
        }
    }

    private readonly struct WaitingFailure(
        ManualResetEventSlim entered, ManualResetEventSlim release, Exception failure) : IJob
    {
        public void Execute()
        {
            entered.Set();
            release.Wait();
            throw failure;
        }
    }

    private readonly struct WaitingSuccess(ManualResetEventSlim entered, ManualResetEventSlim release) : IJob
    {
        public void Execute()
        {
            entered.Set();
            release.Wait();
        }
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
