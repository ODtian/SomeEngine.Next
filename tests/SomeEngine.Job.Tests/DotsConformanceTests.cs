using System.Reflection;

namespace SomeEngine.Job.Tests;

public sealed class DotsConformanceTests
{
    private readonly ConformanceData _data = new();

    public DotsConformanceTests()
    {
        JobSystem.Initialize(new JobRuntimeConfig { WorkerCount = 2 });
        _data.Reset();
    }

    [Fact]
    public void AdapterScheduleCompletePatternMatchesCoreBehavior()
    {
        new ConformanceData.IncrementJob(_data, 5).Schedule().Complete();

        Assert.Equal(5, _data.Counter);
    }

    [Fact]
    public void AdapterDependencyChainRunsInOrder()
    {
        using var firstStarted = _data.Events.CreateSignal();
        using var firstGate = _data.Events.CreateSignal();
        using var secondRan = _data.Events.CreateSignal();
        var order = new List<int>();

        var first = new ConformanceData.BlockingRecordJob(order, 1, firstStarted, firstGate).Schedule();
        var second = new ConformanceData.SignalRecordJob(order, 2, secondRan).Schedule(first);

        Assert.True(firstStarted.Wait(TimeSpan.FromSeconds(2)));

        firstGate.Set();
        second.Complete();

        Assert.Equal([1, 2], order);
        _data.Events.AssertBefore(firstGate, secondRan);
    }

    [Fact]
    public void AdapterHandlesComposeWithCombineDependencies()
    {
        var first = new ConformanceData.IncrementJob(_data, 1).Schedule();
        var second = new ConformanceData.IncrementJob(_data, 10).Schedule();

        JobSystem.CombineDependencies([first, second]).Complete();

        Assert.Equal(11, _data.Counter);
    }

    [Fact]
    public void AdapterParallelDispatchPatternsExecuteExpectedWork()
    {
        var parallelValues = new int[6];
        var forValues = new int[7];

        new ConformanceData.MarkParallelJob(parallelValues)
            .ScheduleParallel(parallelValues.Length, 2)
            .Complete();
        new ConformanceData.MarkForJob(forValues)
            .Schedule(forValues.Length, 3)
            .Complete();

        Assert.All(parallelValues, value => Assert.Equal(1, value));
        Assert.All(forValues, value => Assert.Equal(1, value));
    }

    [Fact]
    public void AdapterChildJobTreeKeepsParentScopeSemantics()
    {
        _data.ChildValues = new int[8];
        _data.ChildChunks = ConformanceData.ArrayChunkSource.Create(
            new JobChunk(0, 0, 2),
            new JobChunk(1, 2, 3),
            new JobChunk(2, 5, 3));

        new ConformanceData.ParentSchedulesAdapterTree(_data).Schedule().Complete();

        Assert.Equal(1, _data.Counter);
        Assert.All(_data.ChildValues, value => Assert.Equal(2, value));
    }

    [Fact]
    public void AdapterManagedPayloadsExecute()
    {
        JobSystem.Initialize(new JobRuntimeConfig { WorkerCount = 0 });
        var log = new List<string>();

        new ConformanceData.ManagedJob(log, "single").Schedule().Complete();
        new ConformanceData.ManagedForJob(log, "for").Schedule(2, 1).Complete();

        Assert.Equal(["single", "for-0", "for-1"], log);
    }

    private sealed class ConformanceData
    {
        internal readonly ExecutionEvents Events = new();

        internal int Counter;
        internal int[] ChildValues = [];
        internal ArrayChunkSource ChildChunks;

        internal void Reset()
        {
            Counter = 0;
            ChildValues = [];
            ChildChunks = default;
        }

        internal readonly struct IncrementJob : IJob
        {
            private readonly ConformanceData _data;

            private readonly int _amount;

            internal IncrementJob(ConformanceData data, int amount)
            {
                _data = data;
                _amount = amount;
            }

            public void Execute()
            {
                Interlocked.Add(ref _data.Counter, _amount);
            }
        }

        internal readonly struct BlockingRecordJob : IJob
        {
            private readonly List<int> _order;
            private readonly int _value;
            private readonly JobSignal _started;
            private readonly JobSignal _gate;

            internal BlockingRecordJob(
                List<int> order,
                int value,
                JobSignal started,
                JobSignal gate)
            {
                _order = order;
                _value = value;
                _started = started;
                _gate = gate;
            }

            public void Execute()
            {
                lock (_order)
                {
                    _order.Add(_value);
                }

                _started.Set();
                _gate.Wait();
            }
        }

        internal readonly struct SignalRecordJob : IJob
        {
            private readonly List<int> _order;
            private readonly int _value;
            private readonly JobSignal _ran;

            internal SignalRecordJob(List<int> order, int value, JobSignal ran)
            {
                _order = order;
                _value = value;
                _ran = ran;
            }

            public void Execute()
            {
                lock (_order)
                {
                    _order.Add(_value);
                }

                _ran.Set();
            }
        }

        internal readonly struct MarkParallelJob : IJobParallelFor
        {
            private readonly int[] _values;

            internal MarkParallelJob(int[] values)
            {
                _values = values;
            }

            public void Execute(int index)
            {
                Interlocked.Increment(ref _values[index]);
            }
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

        internal struct ParentSchedulesAdapterTree : IJob
        {
            private readonly ConformanceData _data;

            internal ParentSchedulesAdapterTree(ConformanceData data) => _data = data;

            public void Execute()
            {
                new IncrementJob(_data, 1).Schedule();
                new MarkForJob(_data.ChildValues).Schedule(_data.ChildValues.Length, 2);
                new FillChunkJob(_data.ChildValues).Schedule(_data.ChildChunks, batchSize: 2);
            }
        }

        internal readonly struct FillChunkJob : IJobChunk
        {
            private readonly int[] _values;

            internal FillChunkJob(int[] values)
            {
                _values = values;
            }

            public void Execute(JobChunk chunk)
            {
                for (var i = chunk.Start; i < chunk.Start + chunk.Length; i++)
                {
                    Interlocked.Increment(ref _values[i]);
                }
            }
        }

        internal readonly struct ManagedJob : IJob
        {
            private readonly List<string> _log;
            private readonly string _value;

            internal ManagedJob(List<string> log, string value)
            {
                _log = log;
                _value = value;
            }

            public void Execute()
            {
                _log.Add(_value);
            }
        }

        internal readonly struct ManagedForJob : IJobFor
        {
            private readonly List<string> _log;
            private readonly string _prefix;

            internal ManagedForJob(List<string> log, string prefix)
            {
                _log = log;
                _prefix = prefix;
            }

            public void Execute(int index)
            {
                _log.Add($"{_prefix}-{index}");
            }
        }

        internal readonly struct ArrayChunkSource : IJobChunkSource
        {
            private readonly JobChunk[] _chunks;

            private ArrayChunkSource(JobChunk[] chunks)
            {
                _chunks = chunks;
            }

            public int ChunkCount => _chunks?.Length ?? 0;

            public static ArrayChunkSource Create(params JobChunk[] chunks)
            {
                return new ArrayChunkSource(chunks);
            }

            public JobChunk GetChunk(int index)
            {
                return _chunks[index];
            }
        }
    }
}
