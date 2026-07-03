namespace SomeEngine.Job.Tests;

public sealed class JobForCompatibilityTests
{
    public JobForCompatibilityTests()
    {
        JobSystem.ResetForTesting(workerCount: 2);
        JobForJobs.Reset();
    }

    [Fact]
    public void JobForAdapterExecutesEveryIndexExactlyOnce()
    {
        var values = new int[13];

        new JobForJobs.MarkForJob(values).Schedule(values.Length, 4).Complete();

        Assert.All(values, value => Assert.Equal(1, value));
    }

    [Fact]
    public void JobForAdapterPreservesDependencyBehavior()
    {
        using var dependencyStarted = new ManualResetEventSlim();
        using var dependencyGate = new ManualResetEventSlim();
        using var indexRan = new ManualResetEventSlim();
        var values = new int[3];

        var dependency = JobSystem.Schedule(new JobForJobs.BlockingJob(dependencyStarted, dependencyGate));
        var dependent = new JobForJobs.SignalForJob(values, indexRan).Schedule(values.Length, 1, dependency);

        Assert.True(dependencyStarted.Wait(TimeSpan.FromSeconds(2)));
        Assert.False(indexRan.Wait(TimeSpan.FromMilliseconds(100)));

        dependencyGate.Set();
        dependent.Complete();

        Assert.True(indexRan.IsSet);
        Assert.Equal([1, 1, 1], values);
    }

    [Fact]
    public void JobForAdapterScheduledFromParentKeepsChildScopeSemantics()
    {
        JobForJobs.ParentValues = new int[9];

        JobSystem.Schedule(new JobForJobs.ParentSchedulesForJob()).Complete();

        Assert.All(JobForJobs.ParentValues, value => Assert.Equal(1, value));
    }

    [Fact]
    public void JobForAdapterPreservesRefFreeAndRefContainingLaneClassification()
    {
        JobSystem.ResetForTesting(new JobRuntimeConfig { WorkerCount = 0 });
        JobForJobs.StaticValues = new int[5];
        var managedLog = new List<int>();

        new JobForJobs.RefFreeForJob().Schedule(JobForJobs.StaticValues.Length, 2).Complete();
        new JobForJobs.RefContainingForJob(managedLog).Schedule(3, 2).Complete();

        var stats = JobSystem.GetStats();
        Assert.Equal(1, stats.RefFreeJobs);
        Assert.Equal(1, stats.RefContainingJobs);
        Assert.Equal([1, 1, 1, 1, 1], JobForJobs.StaticValues);
        Assert.Equal([0, 1, 2], managedLog);
    }

    private static class JobForJobs
    {
        internal static int[] ParentValues = [];
        internal static int[] StaticValues = [];

        internal static void Reset()
        {
            ParentValues = [];
            StaticValues = [];
        }

        internal readonly struct MarkForJob : IJobFor
        {
            private readonly int[] _values;

            internal MarkForJob(int[] values)
            {
                _values = values;
            }

            public void Execute(int index)
            {
                Interlocked.Increment(ref _values[index]);
            }
        }

        internal readonly struct SignalForJob : IJobFor
        {
            private readonly int[] _values;
            private readonly ManualResetEventSlim _ran;

            internal SignalForJob(int[] values, ManualResetEventSlim ran)
            {
                _values = values;
                _ran = ran;
            }

            public void Execute(int index)
            {
                Interlocked.Increment(ref _values[index]);
                _ran.Set();
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

        internal struct ParentSchedulesForJob : IJob
        {
            public void Execute()
            {
                new MarkForJob(ParentValues).Schedule(ParentValues.Length, 3);
            }
        }

        internal struct RefFreeForJob : IJobFor
        {
            public void Execute(int index)
            {
                Interlocked.Increment(ref StaticValues[index]);
            }
        }

        internal readonly struct RefContainingForJob : IJobFor
        {
            private readonly List<int> _log;

            internal RefContainingForJob(List<int> log)
            {
                _log = log;
            }

            public void Execute(int index)
            {
                lock (_log)
                {
                    _log.Add(index);
                }
            }
        }
    }
}
