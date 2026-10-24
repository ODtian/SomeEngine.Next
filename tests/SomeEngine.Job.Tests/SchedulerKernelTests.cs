namespace SomeEngine.Job.Tests;

public sealed class SchedulerKernelTests
{
    private readonly SchedulerData _data = new();

    public SchedulerKernelTests()
    {
        JobSystem.Initialize(new JobRuntimeConfig { WorkerCount = 2 });
    }

    [Fact]
    public void SimpleJobExecutesExactlyOnce()
    {
        _data.Counter = 0;

        JobSystem.Schedule(new SchedulerData.IncrementJob(_data)).Complete();

        Assert.Equal(1, _data.Counter);
    }

    [Fact]
    public void WaitingThreadHelpsDrainWork()
    {
        JobSystem.Initialize(new JobRuntimeConfig { WorkerCount = 0 });
        _data.Counter = 0;

        JobSystem.Schedule(new SchedulerData.IncrementJob(_data)).Complete();

        Assert.Equal(1, _data.Counter);
    }

    [Fact]
    public void QueuePressureDoesNotLoseJobs()
    {
        _data.Counter = 0;
        var handles = new JobHandle[2_000];

        for (var i = 0; i < handles.Length; i++)
        {
            handles[i] = JobSystem.Schedule(new SchedulerData.IncrementJob(_data));
        }

        JobSystem.CombineDependencies(handles).Complete();

        Assert.Equal(handles.Length, _data.Counter);
    }

    [Fact]
    public void ShutdownLifecycleDoesNotHangAndRuntimeCanRestart()
    {
        _data.Counter = 0;

        JobSystem.Schedule(new SchedulerData.IncrementJob(_data)).Complete();
        JobSystem.Shutdown();
        JobSystem.Initialize(new JobRuntimeConfig { WorkerCount = 1 });
        JobSystem.Schedule(new SchedulerData.IncrementJob(_data)).Complete();

        Assert.Equal(2, _data.Counter);
    }

    private sealed class SchedulerData
    {
        internal int Counter;

        internal struct IncrementJob : IJob
        {
            private readonly SchedulerData _data;

            internal IncrementJob(SchedulerData data) => _data = data;

            public void Execute()
            {
                Interlocked.Increment(ref _data.Counter);
            }
        }
    }
}
