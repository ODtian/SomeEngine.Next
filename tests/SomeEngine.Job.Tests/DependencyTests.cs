namespace SomeEngine.Job.Tests;

public sealed class DependencyTests
{
    private readonly DependencyData _data = new();

    public DependencyTests()
    {
        JobSystem.Initialize(new JobRuntimeConfig { WorkerCount = 2 });
        _data.Reset();
    }

    [Fact]
    public async Task DependentJobStartsOnlyAfterDependencyCompletes()
    {
        using var gate = new ManualResetEventSlim();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 0 });
        var dependency = scheduler.Schedule(new DependencyData.BlockingJob(_data, gate, started),
            new JobScheduleOptions(JobPriority.Low));
        var dependent = scheduler.Schedule(new DependencyData.DependentJob(_data),
            new JobScheduleOptions(JobPriority.High), dependency);
        Task completing = Task.Run(dependent.Complete);
        try
        {
            Task first = await Task.WhenAny(started.Task, completing).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Same(started.Task, first);
        }
        finally
        {
            gate.Set();
            await completing.WaitAsync(TimeSpan.FromSeconds(10));
            dependency.Complete();
        }
        Assert.Equal(new[] { "dependency completed", "dependent started" }, _data.Events);
    }

    [Fact]
    public void CombineTwoHandlesCompletesAfterBothInputs()
    {
        var a = JobSystem.Schedule(new DependencyData.IncrementJob(_data));
        var b = JobSystem.Schedule(new DependencyData.IncrementJob(_data));

        JobSystem.CombineDependencies([a, b]).Complete();

        Assert.Equal(2, _data.Counter);
    }

    [Fact]
    public void CombineManyHandlesCompletesAfterAllInputs()
    {
        var handles = new JobHandle[64];
        for (var i = 0; i < handles.Length; i++)
        {
            handles[i] = JobSystem.Schedule(new DependencyData.IncrementJob(_data));
        }

        JobSystem.CombineDependencies(handles).Complete();

        Assert.Equal(handles.Length, _data.Counter);
    }

    [Fact]
    public void EmptyCombineReturnsCompletedHandle()
    {
        var handle = JobSystem.CombineDependencies([]);

        Assert.True(handle.IsCompleted);
        handle.Complete();
    }

    [Fact]
    public void DuplicateCombineInputIsHandledOnce()
    {
        var dependency = JobSystem.Schedule(new DependencyData.IncrementJob(_data));
        var combined = JobSystem.CombineDependencies([dependency, dependency, dependency]);

        combined.Complete();

        Assert.Equal(1, _data.Counter);
    }

    [Fact]
    public void FaultedDependencyPreventsDependentExecutionAndPropagatesFault()
    {
        var dependency = JobSystem.Schedule(new DependencyData.ThrowingJob());
        var dependent = JobSystem.Schedule(new DependencyData.DependentJob(_data), dependency);

        var ex = Assert.Throws<InvalidOperationException>(() => dependent.Complete());

        Assert.False(_data.DependentRan);
    }

    [Fact]
    public void CleanupRunsAfterFaultedDependencyAndCanRethrowIt()
    {
        var dependency = JobSystem.Schedule(new DependencyData.ThrowingJob());
        var finalizer = JobSystem.ScheduleFinally(
            new DependencyData.ObservingFinalizerJob(_data, dependency),
            JobScheduleOptions.Default,
            dependency);

        var ex = Assert.Throws<InvalidOperationException>(() => finalizer.Complete());

        Assert.True(_data.FinalizerRan);
    }

    [Fact]
    public void ObservingDependencyFailuresAllowsFurtherWorkAndCleanShutdown()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig
        {
            WorkerCount = 0,
            MaxCompletionStates = 4,
        });
        for (int i = 0; i < 32; i++)
        {
            JobHandle input = scheduler.Schedule(new DependencyData.ThrowingJob());
            JobHandle output = scheduler.Schedule(new DependencyData.DependentJob(_data), input);
            Assert.Throws<InvalidOperationException>(output.Complete);
        }
        Assert.False(_data.DependentRan);
        scheduler.Schedule(new DependencyData.IncrementJob(_data)).Complete();
        Assert.Equal(1, _data.Counter);
    }

    private sealed class DependencyData
    {
        internal readonly System.Collections.Concurrent.ConcurrentQueue<string> Events = new();
        internal int Counter;
        internal bool DependentRan;
        internal bool FinalizerRan;

        internal void Reset()
        {
            Counter = 0;
            DependentRan = false;
            FinalizerRan = false;
        }

        internal readonly struct BlockingJob : IJob
        {
            private readonly DependencyData _data;

            private readonly ManualResetEventSlim _gate;
            private readonly TaskCompletionSource _started;

            internal BlockingJob(DependencyData data, ManualResetEventSlim gate, TaskCompletionSource started)
            {
                _data = data;
                _gate = gate;
                _started = started;
            }

            public void Execute()
            {
                _started.TrySetResult();
                _gate.Wait();
                _data.Events.Enqueue("dependency completed");
            }
        }

        internal struct DependentJob : IJob
        {
            private readonly DependencyData _data;

            internal DependentJob(DependencyData data) => _data = data;

            public void Execute()
            {
                _data.Events.Enqueue("dependent started");
                _data.DependentRan = true;
            }
        }

        internal struct IncrementJob : IJob
        {
            private readonly DependencyData _data;

            internal IncrementJob(DependencyData data) => _data = data;

            public void Execute()
            {
                Interlocked.Increment(ref _data.Counter);
            }
        }

        internal struct ThrowingJob : IJob
        {
            public void Execute()
            {
                throw new InvalidOperationException("dependency failed");
            }
        }

        internal readonly struct ObservingFinalizerJob : IJob
        {
            private readonly DependencyData _data;

            private readonly JobHandle _dependency;

            internal ObservingFinalizerJob(DependencyData data, JobHandle dependency)
            {
                _data = data;
                _dependency = dependency;
            }

            public void Execute()
            {
                _data.FinalizerRan = true;
                _dependency.Complete();
            }
        }
    }
}
