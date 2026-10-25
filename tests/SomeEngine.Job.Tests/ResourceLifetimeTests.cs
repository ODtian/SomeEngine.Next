using System.Runtime.CompilerServices;

namespace SomeEngine.Job.Tests;

public sealed class ResourceLifetimeTests
{
    [Theory]
    [InlineData(JobSafetyMode.Checked)]
    [InlineData(JobSafetyMode.Fast)]
    public void ResourceReleaseFollowsDependencyAndAttachedReaderCompletion(JobSafetyMode mode)
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 2, SafetyMode = mode });
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var ready = new DataReady();
        int[] values = [23];
        int[] result = [0];
        JobResourceToken token = scheduler.CreateResourceToken();
        JobHandle dependency = scheduler.CreateExternalFenceHandle(ready);
        JobHandle work = scheduler.Schedule(
            new StartDataReader(scheduler, token, new WeakReference(values), entered, release, result),
            JobResourceAccess.Read(token), dependency);
        try
        {
            Assert.Throws<JobResourceSafetyException>(() => scheduler.ReleaseResourceToken(token));
            ready.Signal();
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            Assert.Throws<JobResourceSafetyException>(() => scheduler.ReleaseResourceToken(token));
        }
        finally
        {
            ready.Signal();
            release.Set();
            work.Complete();
        }

        scheduler.ReleaseResourceToken(token);
        Assert.Equal(23, result[0]);
        GC.KeepAlive(values);
    }

    [Theory]
    [InlineData(JobSafetyMode.Checked)]
    [InlineData(JobSafetyMode.Fast)]
    public void ContainerDataSurvivesItsDependencyAndAttachedReader(JobSafetyMode mode)
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 2, SafetyMode = mode });
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var ready = new DataReady();
        int[] result = [0];
        JobHandle dependency = scheduler.CreateExternalFenceHandle(ready);
        (JobHandle work, WeakReference data) = ScheduleDataReader(scheduler, dependency, entered, release, result);
        bool retainedWhileWaiting;
        bool retainedByReader;
        try
        {
            Collect();
            retainedWhileWaiting = data.IsAlive;
            ready.Signal();
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            Collect();
            retainedByReader = data.IsAlive;
        }
        finally
        {
            ready.Signal();
            release.Set();
            work.Complete();
        }

        Assert.Equal((true, true, 23), (retainedWhileWaiting, retainedByReader, result[0]));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (JobHandle, WeakReference) ScheduleDataReader(
        Scheduler scheduler,
        JobHandle dependency,
        ManualResetEventSlim entered,
        ManualResetEventSlim release,
        int[] result)
    {
        int[] values = [23];
        var data = new WeakReference(values);
        JobResourceToken token = scheduler.GetResourceToken(values);
        JobHandle work = scheduler.Schedule(
            new StartDataReader(scheduler, token, data, entered, release, result),
            JobResourceAccess.Read(token),
            dependency);
        return (work, data);
    }

    private static void Collect()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    private readonly record struct StartDataReader(
        Scheduler Scheduler,
        JobResourceToken Token,
        WeakReference Data,
        ManualResetEventSlim Entered,
        ManualResetEventSlim Release,
        int[] Result) : IJob
    {
        public void Execute() => Scheduler.Schedule(
            new ReadData(Data, Entered, Release, Result), JobResourceAccess.Read(Token));
    }

    private readonly record struct ReadData(
        WeakReference Data,
        ManualResetEventSlim Entered,
        ManualResetEventSlim Release,
        int[] Result) : IJob
    {
        public void Execute()
        {
            Entered.Set();
            Release.Wait();
            if (Data.Target is int[] values)
                Result[0] = values[0];
        }
    }

    private sealed class DataReady : IJobExternalFence
    {
        private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool IsSignaled => _completion.Task.IsCompleted;
        public void OnSignaled(Action<object?> continuation, object? state) =>
            _completion.Task.GetAwaiter().OnCompleted(() => continuation(state));
        internal void Signal() => _completion.TrySetResult();
    }
}
