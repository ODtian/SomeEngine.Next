namespace SomeEngine.Job.Tests;

public sealed class RuntimeSafetyTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Fact]
    public void ConcurrentSchedulingFromMultipleThreadsIsSafe()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
        int[] counts = new int[400];
        JobHandle[] handles = new JobHandle[counts.Length];
        Parallel.For(0, counts.Length, index =>
            handles[index] = scheduler.Schedule(new Increment(counts, index)));
        scheduler.CombineDependencies(handles).Complete();
        Assert.All(counts, count => Assert.Equal(1, count));
    }

    [Fact]
    public async Task ConcurrentCompleteOnSameHandleIsSafe()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 1 });
        using var gate = new ManualResetEventSlim();
        int[] counts = [0];
        JobHandle handle = scheduler.Schedule(new WaitAndIncrement(gate, counts));
        Task[] readers = Enumerable.Range(0, 4)
            .Select(_ => Task.Run(handle.Complete)).ToArray();
        gate.Set();
        await Task.WhenAll(readers).WaitAsync(Timeout);
        Assert.Equal(1, counts[0]);
        Assert.True(handle.IsCompleted);
    }

    [Fact]
    public void RootAndChildFailuresAreObservableThroughTheirHandles()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 0 });
        JobHandle root = scheduler.Schedule(new ThrowingJob());
        Assert.Equal("job failed", Assert.Throws<InvalidOperationException>(root.Complete).Message);
        JobHandle parent = scheduler.Schedule(new SpawnFailingChild());
        Assert.Equal("job failed", Assert.Throws<InvalidOperationException>(parent.Complete).Message);
    }

    [Fact]
    public void SchedulersDoNotExecuteEachOthersWork()
    {
        using var first = new Scheduler(new JobRuntimeConfig { WorkerCount = 0 });
        using var second = new Scheduler(new JobRuntimeConfig { WorkerCount = 0 });
        int[] counts = [0, 0];
        JobHandle a = first.Schedule(new Increment(counts, 0));
        JobHandle b = second.Schedule(new Increment(counts, 1));
        b.Complete();
        Assert.Equal(new[] { 0, 1 }, counts);
        Assert.False(a.IsCompleted);
        a.Complete();
        Assert.Equal(new[] { 1, 1 }, counts);
    }

    [Fact]
    public void CrossSchedulerDependenciesAreRejectedWithoutExecutingWork()
    {
        using var first = new Scheduler(new JobRuntimeConfig { WorkerCount = 0 });
        using var second = new Scheduler(new JobRuntimeConfig { WorkerCount = 0 });
        int[] counts = [0];
        JobHandle dependency = first.Schedule(new Increment(counts, 0));
        Assert.Throws<ArgumentException>(() => second.Schedule(new Increment(counts, 0), dependency));
        Assert.Throws<ArgumentException>(() => second.CombineDependencies([dependency]));
        Assert.Equal(0, counts[0]);
        dependency.Complete();
        Assert.Equal(1, counts[0]);
    }

    [Fact]
    public void DisposeDrainsAcceptedWorkWithoutDedicatedWorkers()
    {
        var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 0 });
        int[] counts = [0];
        JobHandle parent = scheduler.Schedule(new SpawnChild(counts));
        scheduler.Dispose();
        Assert.True(parent.IsCompleted);
        parent.Complete();
        Assert.Equal(1, counts[0]);
        Assert.Throws<ObjectDisposedException>(() => scheduler.Schedule(new Increment(counts, 0)));
        scheduler.Dispose();
    }

    [Fact]
    public async Task ClosingWaitsForRunningParentsAndTheirLaterChildren()
    {
        var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 1 });
        using var started = new ManualResetEventSlim();
        using var gate = new ManualResetEventSlim();
        int[] counts = [0];
        JobHandle parent = scheduler.Schedule(new WaitAndSpawn(started, gate, counts));
        Task? closing = null;
        try
        {
            Assert.True(started.Wait(Timeout));
            closing = Task.Run(scheduler.Dispose);
            Assert.True(SpinWait.SpinUntil(() => RejectsNewResource(scheduler), Timeout));
            Assert.False(parent.IsCompleted);
            Assert.False(closing.IsCompleted);
        }
        finally
        {
            gate.Set();
            if (closing is not null)
                await closing.WaitAsync(Timeout);
            else
                scheduler.Dispose();
        }
        parent.Complete();
        Assert.Equal(1, counts[0]);
    }

    [Fact]
    public void ShutdownReportsUnobservedFailuresAfterDrainingOtherWork()
    {
        var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 0 });
        int[] counts = [0];
        _ = scheduler.Schedule(new ThrowingJob());
        _ = scheduler.Schedule(new Increment(counts, 0));
        AggregateException error = Assert.Throws<AggregateException>(scheduler.Dispose);
        Assert.Contains(error.InnerExceptions, exception => exception.Message == "job failed");
        Assert.Equal(1, counts[0]);
        scheduler.Dispose();
    }

    [Fact]
    public void AJobCannotDisposeItsOwnScheduler()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 0 });
        JobHandle handle = scheduler.Schedule(new StopScheduler(scheduler));
        Assert.Throws<InvalidOperationException>(handle.Complete);
    }

    [Fact]
    public void ReplacingTheDefaultDoesNotReinterpretOldHandles()
    {
        JobSystem.Initialize(new JobRuntimeConfig { WorkerCount = 0 });
        int[] counts = [0, 0];
        JobHandle old = JobSystem.Schedule(new Increment(counts, 0));
        JobSystem.Initialize(new JobRuntimeConfig { WorkerCount = 0 });
        try
        {
            Assert.Equal(1, counts[0]);
            JobHandle current = JobSystem.Schedule(new Increment(counts, 1));
            old.Complete();
            Assert.True(old.IsCompleted);
            Assert.False(current.IsCompleted);
            Assert.Equal(0, counts[1]);
            current.Complete();
            Assert.Equal(1, counts[1]);
        }
        finally { JobSystem.Shutdown(); }
    }

    [Fact]
    public void DefaultShutdownRequiresExplicitRestart()
    {
        JobSystem.Initialize(new JobRuntimeConfig { WorkerCount = 0 });
        int[] counts = [0];
        JobHandle handle = JobSystem.Schedule(new Increment(counts, 0));
        JobSystem.Shutdown();
        Assert.Equal(1, counts[0]);
        handle.Complete();
        Assert.Throws<InvalidOperationException>(() => JobSystem.Schedule(new Increment(counts, 0)));
        JobSystem.Initialize(new JobRuntimeConfig { WorkerCount = 0 });
        try
        {
            JobSystem.Schedule(new Increment(counts, 0)).Complete();
            Assert.Equal(2, counts[0]);
        }
        finally { JobSystem.Shutdown(); }
    }

    [Fact]
    public void ResourcesAndTokensFromAnotherSchedulerAreRejected()
    {
        using var first = new Scheduler(new JobRuntimeConfig { WorkerCount = 0, SafetyMode = JobSafetyMode.Strict });
        using var second = new Scheduler(new JobRuntimeConfig { WorkerCount = 0, SafetyMode = JobSafetyMode.Strict });
        JobResource resource = first.CreateResource("first data");
        JobResourceToken token = first.CreateResourceToken("first token");
        int[] counts = [0];
        Assert.Throws<JobResourceSafetyException>(() =>
            second.Schedule(new Increment(counts, 0), JobResourceAccess.Read(resource), default));
        Assert.Throws<JobResourceSafetyException>(() =>
            second.Schedule(new Increment(counts, 0), JobResourceAccess.Write(token), default));
        Assert.Throws<JobResourceSafetyException>(() => second.ReleaseResource(resource));
        Assert.Throws<JobResourceSafetyException>(() => second.ReleaseResourceToken(token));
        first.Schedule(new Increment(counts, 0), JobResourceAccess.Read(resource), default).Complete();
        Assert.Equal(1, counts[0]);
        first.ReleaseResource(resource);
        first.ReleaseResourceToken(token);
    }

    private static bool RejectsNewResource(Scheduler scheduler)
    {
        try
        {
            JobResource resource = scheduler.CreateResource("shutdown probe");
            scheduler.ReleaseResource(resource);
            return false;
        }
        catch (ObjectDisposedException) { return true; }
    }

    private readonly struct Increment(int[] counts, int index) : IJob
    {
        public void Execute() => Interlocked.Increment(ref counts[index]);
    }

    private readonly struct WaitAndIncrement(ManualResetEventSlim gate, int[] counts) : IJob
    {
        public void Execute()
        {
            if (!gate.Wait(Timeout)) throw new TimeoutException("Job gate was not released.");
            Interlocked.Increment(ref counts[0]);
        }
    }

    private readonly struct SpawnChild(int[] counts) : IJob
    {
        public void Execute() => JobSystem.Schedule(new Increment(counts, 0));
    }

    private readonly struct WaitAndSpawn(
        ManualResetEventSlim started, ManualResetEventSlim gate, int[] counts) : IJob
    {
        public void Execute()
        {
            started.Set();
            if (!gate.Wait(Timeout)) throw new TimeoutException("Parent gate was not released.");
            JobSystem.Schedule(new Increment(counts, 0));
        }
    }

    private readonly struct StopScheduler(Scheduler scheduler) : IJob
    {
        public void Execute() => scheduler.Dispose();
    }

    private readonly struct SpawnFailingChild : IJob
    {
        public void Execute() => JobSystem.Schedule(new ThrowingJob());
    }

    private readonly struct ThrowingJob : IJob
    {
        public void Execute() => throw new InvalidOperationException("job failed");
    }
}
