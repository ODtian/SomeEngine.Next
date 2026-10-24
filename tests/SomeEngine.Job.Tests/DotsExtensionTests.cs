namespace SomeEngine.Job.Tests;

public sealed class DotsExtensionTests
{
    private readonly DotsExtensionData _data = new();

    public DotsExtensionTests()
    {
        JobSystem.Initialize(new JobRuntimeConfig { WorkerCount = 2 });
        _data.Reset();
    }

    [Fact]
    public void ExtensionScheduleExecutesSingleJob()
    {
        new DotsExtensionData.IncrementJob(_data, 3).Schedule().Complete();

        Assert.Equal(3, _data.Counter);
    }

    [Fact]
    public void ExtensionSchedulePreservesDependencyBehavior()
    {
        using var dependencyStarted = _data.Events.CreateSignal();
        using var dependencyGate = _data.Events.CreateSignal();
        using var dependentRan = _data.Events.CreateSignal();

        var dependency = JobSystem.Schedule(
            new DotsExtensionData.BlockingJob(dependencyStarted, dependencyGate));
        var dependent = new DotsExtensionData.SignalJob(dependentRan).Schedule(dependency);

        Assert.True(dependencyStarted.Wait(TimeSpan.FromSeconds(2)));

        dependencyGate.Set();
        dependent.Complete();

        Assert.True(dependentRan.IsSet);
        _data.Events.AssertBefore(dependencyGate, dependentRan);
    }

    [Fact]
    public void ExtensionScheduleParallelMatchesCoreParallelBehavior()
    {
        var values = new int[11];

        new DotsExtensionData.MarkIndexJob(values).ScheduleParallel(values.Length, 3).Complete();

        Assert.All(values, value => Assert.Equal(1, value));
    }

    [Fact]
    public void ExtensionScheduleFromParentKeepsChildScopeSemantics()
    {
        _data.ParentValues = new int[7];

        new DotsExtensionData.ParentSchedulesExtensionChildren(_data).Schedule().Complete();

        Assert.All(_data.ParentValues, value => Assert.Equal(1, value));
    }

    private sealed class DotsExtensionData
    {
        internal readonly ExecutionEvents Events = new();

        internal int Counter;
        internal int[] ParentValues = [];

        internal void Reset()
        {
            Counter = 0;
            ParentValues = [];
        }

        internal readonly struct IncrementJob : IJob
        {
            private readonly DotsExtensionData _data;

            private readonly int _amount;

            internal IncrementJob(DotsExtensionData data, int amount)
            {
                _data = data;
                _amount = amount;
            }

            public void Execute()
            {
                Interlocked.Add(ref _data.Counter, _amount);
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

        internal readonly struct SignalJob : IJob
        {
            private readonly JobSignal _ran;

            internal SignalJob(JobSignal ran)
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
            private readonly DotsExtensionData _data;

            internal ParentSchedulesExtensionChildren(DotsExtensionData data) => _data = data;

            public void Execute()
            {
                new MarkIndexJob(_data.ParentValues).ScheduleParallel(_data.ParentValues.Length, 2);
            }
        }
    }
}
