namespace SomeEngine.Job.Tests;

public sealed class SchedulerKernelTests
{
    public SchedulerKernelTests()
    {
        JobSystem.Initialize(new JobRuntimeConfig { WorkerCount = 2 });
    }

    [Fact]
    public void SimpleJobExecutesExactlyOnce()
    {
        SchedulerJobs.Counter = 0;

        JobSystem.Schedule(new SchedulerJobs.IncrementJob()).Complete();

        Assert.Equal(1, SchedulerJobs.Counter);
    }

    [Fact]
    public void WaitingThreadHelpsDrainWork()
    {
        JobSystem.Initialize(new JobRuntimeConfig { WorkerCount = 0 });
        SchedulerJobs.Counter = 0;

        JobSystem.Schedule(new SchedulerJobs.IncrementJob()).Complete();

        Assert.Equal(1, SchedulerJobs.Counter);
    }

    [Fact]
    public void QueuePressureDoesNotLoseJobs()
    {
        SchedulerJobs.Counter = 0;
        var handles = new JobHandle[2_000];

        for (var i = 0; i < handles.Length; i++)
        {
            handles[i] = JobSystem.Schedule(new SchedulerJobs.IncrementJob());
        }

        JobSystem.CombineDependencies(handles).Complete();

        Assert.Equal(handles.Length, SchedulerJobs.Counter);
    }

    [Fact]
    public void ShutdownLifecycleDoesNotHangAndRuntimeCanRestart()
    {
        SchedulerJobs.Counter = 0;

        JobSystem.Schedule(new SchedulerJobs.IncrementJob()).Complete();
        JobSystem.Shutdown();
        JobSystem.Initialize(new JobRuntimeConfig { WorkerCount = 1 });
        JobSystem.Schedule(new SchedulerJobs.IncrementJob()).Complete();

        Assert.Equal(2, SchedulerJobs.Counter);
    }

    private static class SchedulerJobs
    {
        internal static int Counter;

        internal struct IncrementJob : IJob
        {
            public void Execute()
            {
                Interlocked.Increment(ref Counter);
            }
        }
    }
}
