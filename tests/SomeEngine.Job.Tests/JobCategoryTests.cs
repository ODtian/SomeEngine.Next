namespace SomeEngine.Job.Tests;

public sealed class JobCategoryTests
{
    [Fact]
    public void FailedWorkReturnsItsCategoryQuota()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 0 });
        JobCategory category = scheduler.CreateCategory(new JobCategoryConfig { MaxInFlight = 1 });
        var options = new JobScheduleOptions { Category = category };
        JobHandle failed = scheduler.Schedule(new ThrowWork(), options);
        Assert.Throws<ApplicationException>(failed.Complete);
        int[] result = [0];
        scheduler.Schedule(new Store(result, 17), options).Complete();
        Assert.Equal(17, result[0]);
    }

    [Fact]
    public async Task SchedulerCloseFinishesQueuedCategoryWork()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 0 });
        JobCategory category = scheduler.CreateCategory(new JobCategoryConfig { MaxInFlight = 1 });
        var options = new JobScheduleOptions { Category = category };
        int[] result = [0];
        _ = scheduler.Schedule(new Store(result, 9), options);
        ValueTask<JobHandle> submitted = scheduler.ScheduleAsync(new Store(result, 17), options);
        scheduler.Dispose();
        (await submitted).Complete();
        Assert.Equal(17, result[0]);
    }

    [Fact]
    public void NestedSynchronousRangesReuseTheirCategoryBudget()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
        JobCategory category = scheduler.CreateCategory(new JobCategoryConfig
        {
            MaxInFlight = 1, MaxExecuting = 1,
        });
        var options = new JobScheduleOptions { Category = category };
        int[] values = new int[4];
        scheduler.ExecuteParallel(new RunNested(scheduler, options, values), 1, options: options);
        Assert.Equal([1, 1, 1, 1], values);
    }

    [Fact]
    public void ConcurrentNestedSynchronousRangesReuseTheirCategoryBudget()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
        JobCategory category = scheduler.CreateCategory(new JobCategoryConfig
        {
            MaxInFlight = 1, MaxExecuting = 2,
        });
        var options = new JobScheduleOptions { Category = category };
        using var entered = new Barrier(2);
        int[][] values = [new int[4], new int[4]];
        scheduler.ExecuteParallel(new RunConcurrentNested(scheduler, options, entered, values), 2, options: options);
        Assert.Equal([1, 1, 1, 1], values[0]);
        Assert.Equal([1, 1, 1, 1], values[1]);
    }

    [Fact]
    public async Task QueuedCategoryWorkKeepsItsSubmittingScope()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 0 });
        JobCategory category = scheduler.CreateCategory(new JobCategoryConfig { MaxInFlight = 1 });
        var options = new JobScheduleOptions { Category = category };
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        JobHandle first = scheduler.Schedule(new Block(entered, release), options);
        Task completed = Task.Run(first.Complete);
        int[] values = [0];
        int[] result = [0];
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            JobHandle parent = scheduler.Schedule(new QueueChild(scheduler, options, values));
            JobHandle follower = scheduler.Schedule(new Copy(values, result),
                new JobScheduleOptions(JobPriority.High), parent);
            _ = scheduler.Schedule(new ReleaseGate(release), new JobScheduleOptions(JobPriority.Low));
            follower.Complete();
            Assert.Equal(23, result[0]);
        }
        finally
        {
            release.Set();
            await completed;
        }
    }

    [Fact]
    public async Task QuotaWaitRetainsTheActualContainer()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
        JobCategory category = scheduler.CreateCategory(new JobCategoryConfig { MaxInFlight = 1 });
        var options = new JobScheduleOptions { Category = category };
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        int[] result = [0];
        JobHandle first = scheduler.Schedule(new Block(entered, release), options);
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            (ValueTask<JobHandle> submitted, WeakReference data) = QueueWeakRead(scheduler, options, result);
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            Assert.True(data.IsAlive);
            release.Set();
            (await submitted).Complete();
            Assert.Equal(23, result[0]);
        }
        finally
        {
            release.Set();
            first.Complete();
        }
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static (ValueTask<JobHandle>, WeakReference) QueueWeakRead(
        Scheduler scheduler, JobScheduleOptions options, int[] result)
    {
        int[] values = [23];
        var data = new WeakReference(values);
        JobResourceToken token = scheduler.GetResourceToken(values);
        return (scheduler.ScheduleAsync(new ReadWeak(data, result), JobResourceAccess.Read(token), options), data);
    }

    [Fact]
    public async Task ParallelSubmissionWaitsForQuotaAndReturnsCompleteResults()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
        JobCategory category = scheduler.CreateCategory(new JobCategoryConfig
        {
            Name = "decode", MaxInFlight = 1, MaxExecuting = 1, ServiceShare = 1,
        });
        var options = new JobScheduleOptions { Category = category };
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        int[] values = new int[4];
        int[] result = [0];
        JobHandle first = scheduler.Schedule(new Block(entered, release), options);
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            ValueTask<JobHandle> admitted = scheduler.ScheduleParallelAsync(
                new AddRange(values), values.Length, 1, [JobResourceAccess.Write(scheduler, values)], options);
            Assert.False(admitted.IsCompleted);
            release.Set();
            (await admitted).Complete();
            scheduler.Schedule(new Store(result, 17), options).Complete();
            Assert.Equal([1, 1, 1, 1, 17], values.Append(result[0]));
        }
        finally
        {
            release.Set();
            first.Complete();
        }
    }

    [Fact]
    public async Task CancelledQuotaWaitLetsTheNextRequestProceed()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
        JobCategory category = scheduler.CreateCategory(new JobCategoryConfig
        {
            Name = "decode", MaxInFlight = 1, MaxExecuting = 1, ServiceShare = 1,
        });
        var options = new JobScheduleOptions { Category = category };
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var cancellation = new CancellationTokenSource();
        int[] result = [0];
        JobHandle first = scheduler.Schedule(new Block(entered, release), options);
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            ValueTask<JobHandle> canceled = scheduler.ScheduleAsync(
                new Store(result, 9), options, cancellationToken: cancellation.Token);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceled.AsTask());
            ValueTask<JobHandle> next = scheduler.ScheduleAsync(new Store(result, 17), options);
            release.Set();
            (await next).Complete();
            Assert.Equal(17, result[0]);
        }
        finally
        {
            release.Set();
            first.Complete();
        }
    }

    [Fact]
    public async Task SynchronousCallerUsesTheSameExecutionLimit()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 0 });
        JobCategory category = scheduler.CreateCategory(new JobCategoryConfig
        {
            Name = "decode", MaxInFlight = 4, MaxExecuting = 1, ServiceShare = 1,
        });
        var options = new JobScheduleOptions { Category = category };
        var events = new System.Collections.Concurrent.ConcurrentQueue<string>();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        JobHandle first = scheduler.Schedule(new RecordBlocked(events, entered, release), options);
        Task completed = Task.Run(first.Complete);
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            _ = scheduler.Schedule(new ReleaseGate(release));
            scheduler.ExecuteParallel(new RecordRange(events), 1, options: options);
            release.Set();
            await completed;
            Assert.Equal(["first-start", "first-end", "range"], events);
        }
        finally
        {
            release.Set();
            await completed;
        }
    }

    [Fact]
    public void ServiceShareAdvancesBackgroundDuringForegroundWork()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 0 });
        JobCategory foreground = scheduler.CreateCategory(new JobCategoryConfig
        {
            Name = "frame", MaxInFlight = 1, MaxExecuting = 1, ServiceShare = 4,
        });
        JobCategory background = scheduler.CreateCategory(new JobCategoryConfig
        {
            Name = "decode", MaxInFlight = 1, MaxExecuting = 1, ServiceShare = 1,
        });
        var options = new JobScheduleOptions { Category = foreground, Priority = JobPriority.High };
        var data = new ForegroundData(scheduler, options);
        JobHandle frame = scheduler.Schedule(new ContinueForeground(data), options);
        scheduler.Schedule(new RecordBackground(data),
            new JobScheduleOptions { Category = background, Priority = JobPriority.Low }).Complete();
        frame.Complete();

        Assert.Equal(4, data.ForegroundAtBackground);
    }

    [Fact]
    public void ExecutionLimitOrdersCallbacksAcrossWorkTypes()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
        JobCategory category = scheduler.CreateCategory(new JobCategoryConfig
        {
            Name = "decode", MaxInFlight = 4, MaxExecuting = 1, ServiceShare = 1,
        });
        var options = new JobScheduleOptions { Category = category, Priority = JobPriority.High };
        var events = new System.Collections.Concurrent.ConcurrentQueue<string>();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        JobHandle first = scheduler.Schedule(new RecordBlocked(events, entered, release), options);
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            JobHandle second = scheduler.Schedule(new RecordFinished(events), options);
            scheduler.Schedule(new ReleaseGate(release), new JobScheduleOptions(JobPriority.Low)).Complete();
            first.Complete();
            second.Complete();
            Assert.Equal(["first-start", "first-end", "second-start", "second-end"], events);
        }
        finally
        {
            release.Set();
            first.Complete();
        }
    }

    [Fact]
    public async Task InFlightLimitWaitsForThePriorWorkAndItsChildren()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
        JobCategory category = scheduler.CreateCategory(new JobCategoryConfig
        {
            Name = "decode",
            MaxInFlight = 1,
            MaxExecuting = 1,
            ServiceShare = 1,
        });
        var options = new JobScheduleOptions { Category = category };
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        int[] result = [0];
        JobHandle first = scheduler.Schedule(new StartChild(scheduler, entered, release), options);
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            Assert.Throws<InvalidOperationException>(() => scheduler.Schedule(new Store(result, 9), options));
            ValueTask<JobHandle> admitted = scheduler.ScheduleAsync(new Store(result, 17), options);
            Assert.False(admitted.IsCompleted);
            release.Set();
            JobHandle second = await admitted;
            second.Complete();
            Assert.Equal(17, result[0]);
        }
        finally
        {
            release.Set();
            first.Complete();
        }
    }

    private readonly record struct StartChild(
        Scheduler Scheduler, ManualResetEventSlim Entered, ManualResetEventSlim Release) : IJob
    {
        public void Execute() => Scheduler.Schedule(new Block(Entered, Release));
    }

    private readonly record struct Block(ManualResetEventSlim Entered, ManualResetEventSlim Release) : IJob
    {
        public void Execute()
        {
            Entered.Set();
            Release.Wait();
        }
    }

    private readonly record struct Store(int[] Result, int Value) : IJob
    {
        public void Execute() => Result[0] = Value;
    }

    private readonly record struct RecordBlocked(
        System.Collections.Concurrent.ConcurrentQueue<string> Events,
        ManualResetEventSlim Entered, ManualResetEventSlim Release) : IJob
    {
        public void Execute()
        {
            Events.Enqueue("first-start");
            Entered.Set();
            Release.Wait();
            Events.Enqueue("first-end");
        }
    }

    private readonly record struct RecordFinished(
        System.Collections.Concurrent.ConcurrentQueue<string> Events) : IJob
    {
        public void Execute()
        {
            Events.Enqueue("second-start");
            Events.Enqueue("second-end");
        }
    }

    private readonly record struct RecordRange(
        System.Collections.Concurrent.ConcurrentQueue<string> Events) : IJobParallelFor
    {
        public void Execute(int index) => Events.Enqueue("range");
    }

    private readonly record struct AddRange(int[] Values) : IJobParallelFor
    {
        public void Execute(int index) => Values[index]++;
    }

    private readonly struct ThrowWork : IJob
    {
        public void Execute() => throw new ApplicationException();
    }

    private readonly record struct RunNested(Scheduler Scheduler, JobScheduleOptions Options, int[] Values)
        : IJobParallelFor
    {
        public void Execute(int index) => Scheduler.ExecuteParallel(new AddRange(Values), Values.Length, options: Options);
    }

    private readonly record struct RunConcurrentNested(
        Scheduler Scheduler, JobScheduleOptions Options, Barrier Entered, int[][] Values) : IJobParallelFor
    {
        public void Execute(int index)
        {
            Entered.SignalAndWait();
            Scheduler.ExecuteParallel(new AddRange(Values[index]), Values[index].Length, options: Options);
        }
    }

    private readonly record struct QueueChild(Scheduler Scheduler, JobScheduleOptions Options, int[] Values) : IJob
    {
        public void Execute() => _ = Scheduler.ScheduleAsync(new Store(Values, 23), Options);
    }

    private readonly record struct Copy(int[] Values, int[] Result) : IJob
    {
        public void Execute() => Result[0] = Values[0];
    }

    private readonly record struct ReadWeak(WeakReference Data, int[] Result) : IJob
    {
        public void Execute() => Result[0] = ((int[])Data.Target!)[0];
    }

    private readonly record struct ReleaseGate(ManualResetEventSlim Release) : IJob
    {
        public void Execute() => Release.Set();
    }

    private sealed class ForegroundData(Scheduler scheduler, JobScheduleOptions options)
    {
        internal Scheduler Scheduler { get; } = scheduler;
        internal JobScheduleOptions Options { get; } = options;
        internal int Starts;
        internal int ForegroundAtBackground;
        internal bool BackgroundStarted;
    }

    private readonly record struct ContinueForeground(ForegroundData Data) : IJob
    {
        public void Execute()
        {
            Data.Starts++;
            if (!Data.BackgroundStarted && Data.Starts < 32)
                Data.Scheduler.Schedule(this, Data.Options);
        }
    }

    private readonly record struct RecordBackground(ForegroundData Data) : IJob
    {
        public void Execute()
        {
            Data.ForegroundAtBackground = Data.Starts;
            Data.BackgroundStarted = true;
        }
    }
}
