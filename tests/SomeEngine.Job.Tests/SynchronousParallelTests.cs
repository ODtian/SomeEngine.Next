namespace SomeEngine.Job.Tests;

public sealed class SynchronousParallelTests
{
    [Fact]
    public void ResourceScopedExecutionReturnsAfterItsChildrenConsumeTheData()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 0 });
        JobResource resource = scheduler.CreateResource();
        int[] values = new int[4];

        scheduler.ExecuteParallel(new RangeJob(index =>
            scheduler.Schedule(new CallbackJob(() => values[index] = 11), JobResourceAccess.Write(resource))),
            values.Length, batchSize: 1, [JobResourceAccess.Read(resource)]);

        Assert.Equal([11, 11, 11, 11], values);
    }

    [Fact]
    public void NestedExecutionUsesTheCurrentResourcePermissions()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 0 });
        JobResource resource = scheduler.CreateResource();
        JobHandle parent = scheduler.Schedule(new CallbackJob(() =>
            scheduler.ExecuteParallel(new RangeJob(_ => { }), 2, batchSize: 1,
                [JobResourceAccess.Write(resource)])), JobResourceAccess.Read(resource));

        Assert.Throws<JobResourceSafetyException>(parent.Complete);
    }

    [Fact]
    public async Task FailureIsDeliveredAfterEveryParticipantExits()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
        using var rangeStarted = new ManualResetEventSlim();
        using var releaseRange = new ManualResetEventSlim();
        var events = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var execution = Task.Run(() =>
        {
            Exception? failure = Record.Exception(() => scheduler.ExecuteParallel(new RangeJob(index =>
            {
                if (index == 0)
                    throw new InvalidOperationException();
                rangeStarted.Set();
                Assert.True(releaseRange.Wait(TimeSpan.FromSeconds(10)));
                events.Enqueue("range exited");
            }), 2));
            events.Enqueue("failure delivered");
            return failure;
        });
        try
        {
            Assert.True(rangeStarted.Wait(TimeSpan.FromSeconds(10)));
        }
        finally
        {
            releaseRange.Set();
        }

        Assert.IsType<InvalidOperationException>(await execution.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(["range exited", "failure delivered"], events);
    }

    [Fact]
    public void ExecuteParallelReadsPrecedingResourceWritesBeforeReturning()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 0 });
        JobResource resource = scheduler.CreateResource();
        int input = 0;
        int[] values = new int[4];
        scheduler.Schedule(new CallbackJob(() => input = 9), JobResourceAccess.Write(resource));

        scheduler.ExecuteParallel(new RangeJob(index => values[index] = input), values.Length,
            batchSize: 1, [JobResourceAccess.Read(resource)]);

        Assert.Equal([9, 9, 9, 9], values);
    }

    [Fact]
    public void ExecuteParallelReturnsCompleteResultsAndUsesCallingThread()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
        int[] values = new int[128];
        int[] threads = new int[128];
        int caller = Environment.CurrentManagedThreadId;

        scheduler.ExecuteParallel(new FillJob(values, threads), values.Length, batchSize: 4);

        Assert.All(values, value => Assert.Equal(7, value));
        Assert.Contains(caller, threads);
    }

    private readonly record struct FillJob(int[] Values, int[] Threads) : IJobParallelFor
    {
        public void Execute(int index)
        {
            Values[index] = 7;
            Threads[index] = Environment.CurrentManagedThreadId;
        }
    }

    [Fact]
    public void NestedParticipantsKeepTheCurrentScopeForChildDependencies()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
        using var parentReady = new ManualResetEventSlim();
        using var helperFinished = new ManualResetEventSlim();
        Exception? failure = null;
        JobHandle parent = default;
        parent = scheduler.Schedule(new CallbackJob(() =>
        {
            Assert.True(parentReady.Wait(TimeSpan.FromSeconds(10)));
            scheduler.ExecuteParallel(new RangeJob(index =>
            {
                if (index == 0)
                    Assert.True(helperFinished.Wait(TimeSpan.FromSeconds(10)));
                else
                {
                    failure = Record.Exception(() => scheduler.Schedule(new CallbackJob(() => { }), parent));
                    helperFinished.Set();
                }
            }), 2);
        }));
        parentReady.Set();

        parent.Complete();

        Assert.IsType<InvalidOperationException>(failure);
    }

    private readonly record struct CallbackJob(Action Callback) : IJob
    {
        public void Execute() => Callback();
    }

    private readonly record struct RangeJob(Action<int> Callback) : IJobParallelFor
    {
        public void Execute(int index) => Callback(index);
    }
}
