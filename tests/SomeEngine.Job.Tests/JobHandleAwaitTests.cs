namespace SomeEngine.Job.Tests;

public sealed class JobHandleAwaitTests
{
    [Fact]
    public async Task CompletedJobCanBeAwaitedToReadItsResult()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 0 });
        var number = new NumberData();
        JobHandle handle = scheduler.Schedule(new WriteNumberJob(number, 42));
        handle.Complete();

        Assert.Equal(42, await ReadAfterJob(handle, number));
    }

    [Fact]
    public async Task UnfinishedJobCanBeAwaitedToReadItsResultOnTheCompletionThread()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 1 });
        using var started = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var number = new NumberData();
        JobHandle handle = scheduler.Schedule(new WaitingNumberJob(number, started, release));
        try
        {
            Assert.True(started.Wait(TimeSpan.FromSeconds(10)));
            Task<(int Value, int ThreadId)> result = ReadOnCompletion(handle, number);
            Assert.False(result.IsCompleted);

            release.Set();

            var displayed = await result.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal((73, number.ThreadId), displayed);
        }
        finally
        {
            release.Set();
            handle.Complete();
        }
    }

    [Fact]
    public async Task DefaultHandleCanBeAwaited()
    {
        var number = new NumberData { Value = 19 };

        Assert.Equal(19, await ReadAfterJob(default, number));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AwaitPropagatesJobFailure(bool completed)
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 1 });
        using var release = new ManualResetEventSlim();
        using var observed = new ManualResetEventSlim();
        var error = new ApplicationException("Job failed");
        JobHandle handle = scheduler.Schedule(new ThrowErrorJob(error, release));
        JobSystem.OnCompleted(handle, static (_, state) => ((ManualResetEventSlim)state!).Set(), observed);
        try
        {
            if (completed)
            {
                release.Set();
                Assert.True(observed.Wait(TimeSpan.FromSeconds(10)));
            }

            Task<int> result = ReadAfterJob(handle, new NumberData());
            release.Set();

            Assert.Same(error, await Assert.ThrowsAsync<ApplicationException>(
                () => result.WaitAsync(TimeSpan.FromSeconds(10))));
        }
        finally
        {
            release.Set();
            Assert.Same(error, Assert.Throws<ApplicationException>(handle.Complete));
        }
    }

    [Fact]
    public void CompletionBetweenCheckingAndRegisteringResumesOnTheRegisteringThread()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 0 });
        var number = new NumberData();
        JobHandle handle = scheduler.Schedule(new WriteNumberJob(number, 42));
        JobHandleAwaiter awaiter = handle.GetAwaiter();
        Assert.False(awaiter.IsCompleted);
        handle.Complete();

        int thread = 0;
        awaiter.OnCompleted(() => thread = Environment.CurrentManagedThreadId);
        Assert.Equal(Environment.CurrentManagedThreadId, thread);
    }

    [Fact]
    public async Task RegisteredWaitersKeepTheirFailureAfterCompletionRecordsAreReused()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 0, MaxCompletionStates = 1 });
        using var release = new ManualResetEventSlim(true);
        var failure = new ApplicationException();
        JobHandle handle = scheduler.Schedule(new ThrowErrorJob(failure, release));
        JobHandleAwaiter first = handle.GetAwaiter();
        JobHandleAwaiter second = handle.GetAwaiter();
        int notifications = 0;
        first.OnCompleted(() => notifications++);
        second.OnCompleted(() => notifications++);
        ValueTask shared = handle.WaitAsync();

        Assert.Same(failure, Assert.Throws<ApplicationException>(handle.Complete));
        scheduler.Schedule(new WriteNumberJob(new NumberData(), 42)).Complete();

        Assert.Equal(2, notifications);
        Assert.Same(failure, Assert.Throws<ApplicationException>(first.GetResult));
        Assert.Same(failure, Assert.Throws<ApplicationException>(second.GetResult));
        Assert.Same(failure, await Assert.ThrowsAsync<ApplicationException>(async () => await shared));
        Assert.Same(failure, await Assert.ThrowsAsync<ApplicationException>(async () => await shared));
    }

    [Fact]
    public async Task CompletedFailureCanBeSharedAsAnAsynchronousResult()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 0, MaxCompletionStates = 1 });
        using var release = new ManualResetEventSlim(true);
        var failure = new ApplicationException();
        JobHandle handle = scheduler.Schedule(new ThrowErrorJob(failure, release));
        Assert.Same(failure, Assert.Throws<ApplicationException>(handle.Complete));

        ValueTask shared = handle.WaitAsync();
        scheduler.Schedule(new WriteNumberJob(new NumberData(), 42)).Complete();

        Assert.Same(failure, await Assert.ThrowsAsync<ApplicationException>(async () => await shared));
        Assert.Same(failure, await Assert.ThrowsAsync<ApplicationException>(async () => await shared));
    }

    private static async Task<int> ReadAfterJob(JobHandle handle, NumberData number)
    {
        await handle;
        return number.Value;
    }

    private static async Task<(int Value, int ThreadId)> ReadOnCompletion(JobHandle handle, NumberData number)
    {
        await handle;
        return (number.Value, Environment.CurrentManagedThreadId);
    }

    private sealed class NumberData
    {
        public int Value;
        public int ThreadId;
    }

    private readonly record struct WriteNumberJob(NumberData Number, int Value) : IJob
    {
        public void Execute() => Number.Value = Value;
    }

    private readonly record struct ThrowErrorJob(Exception Error, ManualResetEventSlim Release) : IJob
    {
        public void Execute()
        {
            Release.Wait();
            throw Error;
        }
    }

    private readonly record struct WaitingNumberJob(
        NumberData Number, ManualResetEventSlim Started, ManualResetEventSlim Release) : IJob
    {
        public void Execute()
        {
            Started.Set();
            Release.Wait();
            Number.Value = 73;
            Number.ThreadId = Environment.CurrentManagedThreadId;
        }
    }
}
