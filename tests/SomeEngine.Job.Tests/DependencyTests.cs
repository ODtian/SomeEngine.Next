namespace SomeEngine.Job.Tests;

public sealed class DependencyTests
{
    public DependencyTests()
    {
        JobSystem.ResetForTesting(workerCount: 2);
        DependencyJobs.Reset();
    }

    [Fact]
    public void DependentJobStartsOnlyAfterDependencyCompletes()
    {
        using var gate = new ManualResetEventSlim();
        using var started = new ManualResetEventSlim();
        var dependency = JobSystem.Schedule(new DependencyJobs.BlockingJob(gate, started));
        var dependent = JobSystem.Schedule(new DependencyJobs.DependentJob(), dependency);

        Assert.True(started.Wait(1_000));
        Assert.False(DependencyJobs.DependentRan);

        gate.Set();
        dependent.Complete();

        Assert.True(DependencyJobs.DependencyRan);
        Assert.True(DependencyJobs.DependentRan);
    }

    [Fact]
    public void CombineTwoHandlesCompletesAfterBothInputs()
    {
        var a = JobSystem.Schedule(new DependencyJobs.IncrementJob());
        var b = JobSystem.Schedule(new DependencyJobs.IncrementJob());

        JobSystem.CombineDependencies([a, b]).Complete();

        Assert.Equal(2, DependencyJobs.Counter);
    }

    [Fact]
    public void CombineManyHandlesCompletesAfterAllInputs()
    {
        var handles = new JobHandle[64];
        for (var i = 0; i < handles.Length; i++)
        {
            handles[i] = JobSystem.Schedule(new DependencyJobs.IncrementJob());
        }

        JobSystem.CombineDependencies(handles).Complete();

        Assert.Equal(handles.Length, DependencyJobs.Counter);
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
        var dependency = JobSystem.Schedule(new DependencyJobs.IncrementJob());
        var combined = JobSystem.CombineDependencies([dependency, dependency, dependency]);

        combined.Complete();

        Assert.Equal(1, DependencyJobs.Counter);
    }

    [Fact]
    public void FaultedDependencyPreventsDependentExecutionAndPropagatesFault()
    {
        var dependency = JobSystem.Schedule(new DependencyJobs.ThrowingJob());
        var dependent = JobSystem.Schedule(new DependencyJobs.DependentJob(), dependency);

        var ex = Assert.Throws<InvalidOperationException>(() => dependent.Complete());

        Assert.Equal("dependency failed", ex.Message);
        Assert.False(DependencyJobs.DependentRan);
    }

    private static class DependencyJobs
    {
        internal static int Counter;
        internal static bool DependencyRan;
        internal static bool DependentRan;

        internal static void Reset()
        {
            Counter = 0;
            DependencyRan = false;
            DependentRan = false;
        }

        internal readonly struct BlockingJob : IJob
        {
            private readonly ManualResetEventSlim _gate;
            private readonly ManualResetEventSlim _started;

            internal BlockingJob(ManualResetEventSlim gate, ManualResetEventSlim started)
            {
                _gate = gate;
                _started = started;
            }

            public void Execute()
            {
                _started.Set();
                _gate.Wait();
                DependencyRan = true;
            }
        }

        internal struct DependentJob : IJob
        {
            public void Execute()
            {
                DependentRan = true;
            }
        }

        internal struct IncrementJob : IJob
        {
            public void Execute()
            {
                Interlocked.Increment(ref Counter);
            }
        }

        internal struct ThrowingJob : IJob
        {
            public void Execute()
            {
                throw new InvalidOperationException("dependency failed");
            }
        }
    }
}
