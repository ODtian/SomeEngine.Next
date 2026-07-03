using System.Reflection;

namespace SomeEngine.Job.Tests;

public sealed class DotsConformanceTests
{
    public DotsConformanceTests()
    {
        JobSystem.ResetForTesting(workerCount: 2);
        ConformanceJobs.Reset();
    }

    [Fact]
    public void AdapterPublicSurfaceIsExplicit()
    {
        var publicTypes = typeof(IJobFor).Assembly
            .GetExportedTypes()
            .Select(type => type.FullName!)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            [
                "SomeEngine.Job.IJobChunk",
                "SomeEngine.Job.IJobChunkSource",
                "SomeEngine.Job.IJobFor",
                "SomeEngine.Job.JobChunk",
                "SomeEngine.Job.JobChunkExtensions",
                "SomeEngine.Job.JobForExtensions",
                "SomeEngine.Job.JobScheduleExtensions"
            ],
            publicTypes);
    }

    [Fact]
    public void AdapterScheduleCompletePatternMatchesCoreBehavior()
    {
        new ConformanceJobs.IncrementJob(5).Schedule().Complete();

        Assert.Equal(5, ConformanceJobs.Counter);
    }

    [Fact]
    public void AdapterDependencyChainRunsInOrder()
    {
        using var firstStarted = new ManualResetEventSlim();
        using var firstGate = new ManualResetEventSlim();
        using var secondRan = new ManualResetEventSlim();
        var order = new List<int>();

        var first = new ConformanceJobs.BlockingRecordJob(order, 1, firstStarted, firstGate).Schedule();
        var second = new ConformanceJobs.SignalRecordJob(order, 2, secondRan).Schedule(first);

        Assert.True(firstStarted.Wait(TimeSpan.FromSeconds(2)));
        Assert.False(secondRan.Wait(TimeSpan.FromMilliseconds(100)));

        firstGate.Set();
        second.Complete();

        Assert.Equal([1, 2], order);
    }

    [Fact]
    public void AdapterHandlesComposeWithCombineDependencies()
    {
        var first = new ConformanceJobs.IncrementJob(1).Schedule();
        var second = new ConformanceJobs.IncrementJob(10).Schedule();

        JobSystem.CombineDependencies([first, second]).Complete();

        Assert.Equal(11, ConformanceJobs.Counter);
    }

    [Fact]
    public void AdapterParallelDispatchPatternsExecuteExpectedWork()
    {
        var parallelValues = new int[6];
        var forValues = new int[7];

        new ConformanceJobs.MarkParallelJob(parallelValues)
            .ScheduleParallel(parallelValues.Length, 2)
            .Complete();
        new ConformanceJobs.MarkForJob(forValues)
            .Schedule(forValues.Length, 3)
            .Complete();

        Assert.All(parallelValues, value => Assert.Equal(1, value));
        Assert.All(forValues, value => Assert.Equal(1, value));
    }

    [Fact]
    public void AdapterChildJobTreeKeepsParentScopeSemantics()
    {
        ConformanceJobs.ChildValues = new int[8];
        ConformanceJobs.ChildChunks = ConformanceJobs.ArrayChunkSource.Create(
            new JobChunk(0, 0, 2),
            new JobChunk(1, 2, 3),
            new JobChunk(2, 5, 3));

        new ConformanceJobs.ParentSchedulesAdapterTree().Schedule().Complete();

        Assert.Equal(1, ConformanceJobs.Counter);
        Assert.All(ConformanceJobs.ChildValues, value => Assert.Equal(2, value));
    }

    [Fact]
    public void AdapterManagedPayloadsUseRefContainingLane()
    {
        JobSystem.ResetForTesting(new JobRuntimeConfig { WorkerCount = 0 });
        var log = new List<string>();

        new ConformanceJobs.ManagedJob(log, "single").Schedule().Complete();
        new ConformanceJobs.ManagedForJob(log, "for").Schedule(2, 1).Complete();

        var stats = JobSystem.GetStats();
        Assert.Equal(2, stats.RefContainingJobs);
        Assert.Equal(["single", "for-0", "for-1"], log);
    }

    private static class ConformanceJobs
    {
        internal static int Counter;
        internal static int[] ChildValues = [];
        internal static ArrayChunkSource ChildChunks;

        internal static void Reset()
        {
            Counter = 0;
            ChildValues = [];
            ChildChunks = default;
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

        internal readonly struct BlockingRecordJob : IJob
        {
            private readonly List<int> _order;
            private readonly int _value;
            private readonly ManualResetEventSlim _started;
            private readonly ManualResetEventSlim _gate;

            internal BlockingRecordJob(
                List<int> order,
                int value,
                ManualResetEventSlim started,
                ManualResetEventSlim gate)
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
            private readonly ManualResetEventSlim _ran;

            internal SignalRecordJob(List<int> order, int value, ManualResetEventSlim ran)
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
            public void Execute()
            {
                new IncrementJob(1).Schedule();
                new MarkForJob(ChildValues).Schedule(ChildValues.Length, 2);
                new FillChunkJob(ChildValues).Schedule(ChildChunks, batchSize: 2);
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
