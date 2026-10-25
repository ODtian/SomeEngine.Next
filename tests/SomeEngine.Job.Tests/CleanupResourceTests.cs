namespace SomeEngine.Job.Tests;

public sealed class CleanupResourceTests
{
    [Fact]
    public void CleanupRestoresItsResourceAfterCancellation()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 0 });
        var source = new TaskCompletionSource();
        JobHandle dependency = scheduler.CreateExternalFenceHandle(source.Task);
        JobHandleAwaiter result = dependency.GetAwaiter();
        JobResource resource = scheduler.CreateResource();
        int[] values = [0];
        JobHandle cleanup = scheduler.ScheduleFinally(new RestoreValue(result, values),
            [JobResourceAccess.Write(resource)], JobScheduleOptions.Default, dependency);
        source.SetCanceled();

        Assert.ThrowsAny<OperationCanceledException>(cleanup.Complete);
        Assert.Equal(7, values[0]);
        scheduler.ReleaseResource(resource);
    }

    [Fact]
    public async Task CleanupCompletionIncludesItsChildWork()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 0 });
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        JobHandle dependency = scheduler.Schedule(new NoWork());
        JobResource resource = scheduler.CreateResource();
        int[] values = [0];
        JobHandle cleanup = scheduler.ScheduleFinally(
            new StartChild(scheduler, dependency.GetAwaiter(), resource, values, entered, release),
            [JobResourceAccess.Write(resource)], JobScheduleOptions.Default, dependency);
        Task completion = Task.Run(cleanup.Complete);
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
            Assert.False(cleanup.IsCompleted);
            Assert.Throws<JobResourceSafetyException>(() => scheduler.ReleaseResource(resource));
        }
        finally
        {
            release.Set();
            await completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        Assert.Equal(7, values[0]);
        scheduler.ReleaseResource(resource);
    }

    [Fact]
    public void CleanupKeepsTheDependencyFailureWhileWaitingForItsResource()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 0, MaxCompletionStates = 3 });
        var failure = new ApplicationException();
        JobHandle dependency = scheduler.Schedule(new Fail(failure));
        JobHandleAwaiter result = dependency.GetAwaiter();
        JobResource resource = scheduler.CreateResource();
        var source = new TaskCompletionSource();
        scheduler.CreateExternalFenceHandle(source.Task, [JobResourceAccess.Write(resource)]);
        int[] values = [0];
        JobHandle cleanup = scheduler.ScheduleFinally(new RestoreValue(result, values),
            [JobResourceAccess.Write(resource)], JobScheduleOptions.Default, dependency);
        try
        {
            Assert.Same(failure, Assert.Throws<ApplicationException>(dependency.Complete));
            scheduler.Schedule(new NoWork()).Complete();
            Assert.Equal(0, values[0]);
            source.SetResult();

            Assert.Same(failure, Assert.Throws<ApplicationException>(cleanup.Complete));
            int[] restored = [0];
            scheduler.Schedule(new ReadValue(values, restored), JobResourceAccess.Read(resource)).Complete();
            Assert.Equal(7, restored[0]);
        }
        finally
        {
            source.TrySetResult();
            scheduler.ReleaseResource(resource);
        }
    }

    private readonly record struct Fail(Exception Error) : IJob
    {
        public void Execute() => throw Error;
    }

    private readonly record struct RestoreValue(JobHandleAwaiter Result, int[] Values) : IJob
    {
        public void Execute()
        {
            Values[0] = 7;
            Result.GetResult();
        }
    }

    private readonly record struct ReadValue(int[] Values, int[] Output) : IJob
    {
        public void Execute() => Output[0] = Values[0];
    }

    private readonly record struct StartChild(
        Scheduler Scheduler, JobHandleAwaiter Result, JobResource Resource, int[] Values,
        ManualResetEventSlim Entered, ManualResetEventSlim Release) : IJob
    {
        public void Execute()
        {
            Result.GetResult();
            Scheduler.Schedule(new WriteAfterSignal(Values, Entered, Release), JobResourceAccess.Write(Resource));
        }
    }

    private readonly record struct WriteAfterSignal(
        int[] Values, ManualResetEventSlim Entered, ManualResetEventSlim Release) : IJob
    {
        public void Execute()
        {
            Entered.Set();
            Release.Wait();
            Values[0] = 7;
        }
    }

    private readonly struct NoWork : IJob
    {
        public void Execute() { }
    }
}
