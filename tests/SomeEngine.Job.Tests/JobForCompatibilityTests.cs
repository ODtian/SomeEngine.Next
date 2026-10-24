namespace SomeEngine.Job.Tests;

public sealed class JobForCompatibilityTests
{
    private readonly JobForData _data = new();

    public JobForCompatibilityTests()
    {
        JobSystem.Initialize(new JobRuntimeConfig { WorkerCount = 2 });
        _data.Reset();
    }

    [Fact]
    public void JobForAdapterExecutesEveryIndexExactlyOnce()
    {
        var values = new int[13];

        new JobForData.MarkForJob(values).Schedule(values.Length, 4).Complete();

        Assert.All(values, value => Assert.Equal(1, value));
    }

    [Fact]
    public void JobForAdapterPreservesDependencyBehavior()
    {
        using var dependencyStarted = _data.Events.CreateSignal();
        using var dependencyGate = _data.Events.CreateSignal();
        using var indexRan = _data.Events.CreateSignal();
        var values = new int[3];

        var dependency = JobSystem.Schedule(new JobForData.BlockingJob(dependencyStarted, dependencyGate));
        var dependent = new JobForData.SignalForJob(values, indexRan).Schedule(values.Length, 1, dependency);

        Assert.True(dependencyStarted.Wait(TimeSpan.FromSeconds(2)));

        dependencyGate.Set();
        dependent.Complete();

        Assert.True(indexRan.IsSet);
        Assert.Equal([1, 1, 1], values);
        _data.Events.AssertBefore(dependencyGate, indexRan);
    }

    [Fact]
    public void JobForAdapterScheduledFromParentKeepsChildScopeSemantics()
    {
        _data.ParentValues = new int[9];

        JobSystem.Schedule(new JobForData.ParentSchedulesForJob(_data)).Complete();

        Assert.All(_data.ParentValues, value => Assert.Equal(1, value));
    }

    [Fact]
    public void JobForAdapterExecutesArrayAndListResults()
    {
        JobSystem.Initialize(new JobRuntimeConfig { WorkerCount = 0 });
        _data.Values = new int[5];
        var managedLog = new List<int>();

        new JobForData.ArrayForJob(_data).Schedule(_data.Values.Length, 2).Complete();
        new JobForData.RefContainingForJob(managedLog).Schedule(3, 2).Complete();

        Assert.Equal([1, 1, 1, 1, 1], _data.Values);
        Assert.Equal([0, 1, 2], managedLog);
    }

    private sealed class JobForData
    {
        internal readonly ExecutionEvents Events = new();

        internal int[] ParentValues = [];
        internal int[] Values = [];

        internal void Reset()
        {
            ParentValues = [];
            Values = [];
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
            private readonly JobSignal _ran;

            internal SignalForJob(int[] values, JobSignal ran)
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
            private readonly JobSignal _started;
            private readonly JobSignal _gate;

            internal BlockingJob(JobSignal started, JobSignal gate)
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
            private readonly JobForData _data;

            internal ParentSchedulesForJob(JobForData data) => _data = data;

            public void Execute()
            {
                new MarkForJob(_data.ParentValues).Schedule(_data.ParentValues.Length, 3);
            }
        }

        internal struct ArrayForJob : IJobFor
        {
            private readonly JobForData _data;

            internal ArrayForJob(JobForData data) => _data = data;

            public void Execute(int index)
            {
                Interlocked.Increment(ref _data.Values[index]);
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
