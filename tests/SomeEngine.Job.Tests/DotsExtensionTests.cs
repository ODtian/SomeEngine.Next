namespace SomeEngine.Job.Tests;

public sealed class DotsExtensionTests
{
    public DotsExtensionTests()
    {
        JobSystem.ResetForTesting(workerCount: 2);
        DotsExtensionJobs.Reset();
    }

    [Fact]
    public void ExtensionScheduleExecutesSingleJob()
    {
        new DotsExtensionJobs.IncrementJob(3).Schedule().Complete();

        Assert.Equal(3, DotsExtensionJobs.Counter);
    }

    [Fact]
    public void ExtensionSchedulePreservesDependencyBehavior()
    {
        using var dependencyStarted = new ManualResetEventSlim();
        using var dependencyGate = new ManualResetEventSlim();
        using var dependentRan = new ManualResetEventSlim();

        var dependency = JobSystem.Schedule(
            new DotsExtensionJobs.BlockingJob(dependencyStarted, dependencyGate));
        var dependent = new DotsExtensionJobs.SignalJob(dependentRan).Schedule(dependency);

        Assert.True(dependencyStarted.Wait(TimeSpan.FromSeconds(2)));
        Assert.False(dependentRan.Wait(TimeSpan.FromMilliseconds(100)));

        dependencyGate.Set();
        dependent.Complete();

        Assert.True(dependentRan.IsSet);
    }

    [Fact]
    public void ExtensionScheduleParallelMatchesCoreParallelBehavior()
    {
        var values = new int[11];

        new DotsExtensionJobs.MarkIndexJob(values).ScheduleParallel(values.Length, 3).Complete();

        Assert.All(values, value => Assert.Equal(1, value));
    }

    [Fact]
    public void ExtensionScheduleFromParentKeepsChildScopeSemantics()
    {
        DotsExtensionJobs.ParentValues = new int[7];

        new DotsExtensionJobs.ParentSchedulesExtensionChildren().Schedule().Complete();

        Assert.All(DotsExtensionJobs.ParentValues, value => Assert.Equal(1, value));
    }

    private static class DotsExtensionJobs
    {
        internal static int Counter;
        internal static int[] ParentValues = [];

        internal static void Reset()
        {
            Counter = 0;
            ParentValues = [];
        }

        internal readonly struct IncrementJob : IJob
        {
            private readonly int _amount;

            internal IncrementJob(int amount)
            {
                _amount = amount;
            }

            public void Execute()
            {
                Interlocked.Add(ref Counter, _amount);
            }
        }

        internal readonly struct BlockingJob : IJob
        {
            private readonly ManualResetEventSlim _started;
            private readonly ManualResetEventSlim _gate;

            internal BlockingJob(ManualResetEventSlim started, ManualResetEventSlim gate)
            {
                _started = started;
                _gate = gate;
            }

            public void Execute()
            {
                _started.Set();
                _gate.Wait();
            }
        }

        internal readonly struct SignalJob : IJob
        {
            private readonly ManualResetEventSlim _ran;

            internal SignalJob(ManualResetEventSlim ran)
            {
                _ran = ran;
            }

            public void Execute()
            {
                _ran.Set();
            }
        }

        internal readonly struct MarkIndexJob : IJobParallelFor
        {
            private readonly int[] _values;

            internal MarkIndexJob(int[] values)
            {
                _values = values;
            }

            public void Execute(int index)
            {
                Interlocked.Increment(ref _values[index]);
            }
        }

        internal struct ParentSchedulesExtensionChildren : IJob
        {
            public void Execute()
            {
                new MarkIndexJob(ParentValues).ScheduleParallel(ParentValues.Length, 2);
            }
        }
    }
}
