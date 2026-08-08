using SomeEngine.Job;

namespace SomeEngine.Job.Dots.Tests;

public sealed class DotsConsumerTests
{
    public DotsConsumerTests()
    {
        JobSystem.Initialize(new JobRuntimeConfig { WorkerCount = 2 });
        Counter = 0;
    }

    [Fact]
    public void AdapterSchedulePatternsCompleteWork()
    {
        var forValues = new int[4];
        var chunkValues = new int[5];
        var chunks = ArrayChunkSource.Create(
            new JobChunk(0, 0, 2),
            new JobChunk(1, 2, 3));

        new IncrementJob(3).Schedule().Complete();
        new MarkForJob(forValues).Schedule(forValues.Length, 2).Complete();
        new FillChunkJob(chunkValues).Schedule(chunks, batchSize: 1).Complete();

        Assert.Equal(3, Counter);
        Assert.All(forValues, value => Assert.Equal(1, value));
        Assert.All(chunkValues, value => Assert.Equal(1, value));
    }

    [Fact]
    public void AsyncVoidJobForIsRejectedBeforeSchedulingOrExecution()
    {
        AsyncJobFor.Reset();
        JobRuntimeStats before = JobSystem.GetStats();

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
            new AsyncJobFor().Schedule(length: 4, batchSize: 1));

        Assert.Contains("async", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, Volatile.Read(ref AsyncJobFor.BodyCount));
        Assert.False(SpinWait.SpinUntil(
            static () => Volatile.Read(ref AsyncJobFor.ContinuationCount) != 0,
            TimeSpan.FromMilliseconds(100)));
        AssertStatsEqual(before, JobSystem.GetStats());
    }

    [Fact]
    public void AsyncVoidJobChunkIsRejectedBeforeReadingItsSource()
    {
        AsyncJobChunk.Reset();
        CountingChunkSource.Reset();
        var source = new CountingChunkSource();
        JobRuntimeStats before = JobSystem.GetStats();

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
            new AsyncJobChunk().Schedule(source, batchSize: 1));

        Assert.Contains("async", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, Volatile.Read(ref CountingChunkSource.ChunkCountReads));
        Assert.Equal(0, Volatile.Read(ref CountingChunkSource.GetChunkCalls));
        Assert.Equal(0, Volatile.Read(ref AsyncJobChunk.BodyCount));
        Assert.False(SpinWait.SpinUntil(
            static () => Volatile.Read(ref AsyncJobChunk.ContinuationCount) != 0,
            TimeSpan.FromMilliseconds(100)));
        AssertStatsEqual(before, JobSystem.GetStats());
    }

    [Fact]
    [Trait("Category", "Performance")]
    public void WarmedJobForSchedulePathDoesNotAllocateForAsyncValidation()
    {
        JobSystem.Initialize(new JobRuntimeConfig { WorkerCount = 0 });

        for (var i = 0; i < 128; i++)
        {
            new AllocationJobFor().Schedule(length: 1, batchSize: 1).Complete();
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 32; i++)
        {
            new AllocationJobFor().Schedule(length: 1, batchSize: 1).Complete();
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.InRange(allocated, 0, 1_024);
    }

    [Fact]
    [Trait("Category", "Performance")]
    public void WarmedJobChunkSchedulePathDoesNotAllocateForAsyncValidation()
    {
        JobSystem.Initialize(new JobRuntimeConfig { WorkerCount = 0 });
        var source = new AllocationChunkSource();

        for (var i = 0; i < 128; i++)
        {
            new AllocationJobChunk().Schedule(source, batchSize: 1).Complete();
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 32; i++)
        {
            new AllocationJobChunk().Schedule(source, batchSize: 1).Complete();
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.InRange(allocated, 0, 1_024);
    }

    private static void AssertStatsEqual(JobRuntimeStats expected, JobRuntimeStats actual)
    {
        Assert.Equal(expected.ScheduledJobs, actual.ScheduledJobs);
        Assert.Equal(expected.ExecutedWorkItems, actual.ExecutedWorkItems);
        Assert.Equal(expected.CompletedHandles, actual.CompletedHandles);
        Assert.Equal(expected.FaultedWorkItems, actual.FaultedWorkItems);
        Assert.Equal(expected.QueuedWorkItems, actual.QueuedWorkItems);
        Assert.Equal(expected.RefFreeJobs, actual.RefFreeJobs);
        Assert.Equal(expected.RefContainingJobs, actual.RefContainingJobs);
        Assert.Equal(expected.QueueHighWater, actual.QueueHighWater);
        Assert.Equal(expected.CompletionStateHighWater, actual.CompletionStateHighWater);
        Assert.Equal(expected.ResourceStateHighWater, actual.ResourceStateHighWater);
    }

    private static int Counter;

    private readonly struct IncrementJob : IJob
    {
        private readonly int _amount;

        public IncrementJob(int amount)
        {
            _amount = amount;
        }

        public void Execute()
        {
            Interlocked.Add(ref Counter, _amount);
        }
    }

    private readonly struct MarkForJob : IJobFor
    {
        private readonly int[] _values;

        public MarkForJob(int[] values)
        {
            _values = values;
        }

        public void Execute(int index)
        {
            Interlocked.Increment(ref _values[index]);
        }
    }

    private readonly struct FillChunkJob : IJobChunk
    {
        private readonly int[] _values;

        public FillChunkJob(int[] values)
        {
            _values = values;
        }

        public void Execute(JobChunk chunk)
        {
            for (var index = chunk.Start; index < chunk.Start + chunk.Length; index++)
            {
                Interlocked.Increment(ref _values[index]);
            }
        }
    }

    private readonly struct AllocationJobFor : IJobFor
    {
        public void Execute(int index)
        {
            _ = index;
        }
    }

    private readonly struct AllocationJobChunk : IJobChunk
    {
        public void Execute(JobChunk chunk)
        {
            _ = chunk;
        }
    }

    private readonly struct AllocationChunkSource : IJobChunkSource
    {
        public int ChunkCount => 1;

        public JobChunk GetChunk(int index)
        {
            return new JobChunk(index, start: 0, length: 0);
        }
    }

    private struct AsyncJobFor : IJobFor
    {
        internal static int BodyCount;
        internal static int ContinuationCount;

        internal static void Reset()
        {
            BodyCount = 0;
            ContinuationCount = 0;
        }

        public async void Execute(int index)
        {
            _ = index;
            Interlocked.Increment(ref BodyCount);
            await Task.Yield();
            Interlocked.Increment(ref ContinuationCount);
        }
    }

    private struct AsyncJobChunk : IJobChunk
    {
        internal static int BodyCount;
        internal static int ContinuationCount;

        internal static void Reset()
        {
            BodyCount = 0;
            ContinuationCount = 0;
        }

        public async void Execute(JobChunk chunk)
        {
            _ = chunk;
            Interlocked.Increment(ref BodyCount);
            await Task.Yield();
            Interlocked.Increment(ref ContinuationCount);
        }
    }

    private readonly struct CountingChunkSource : IJobChunkSource
    {
        internal static int ChunkCountReads;
        internal static int GetChunkCalls;

        internal static void Reset()
        {
            ChunkCountReads = 0;
            GetChunkCalls = 0;
        }

        public int ChunkCount
        {
            get
            {
                Interlocked.Increment(ref ChunkCountReads);
                return 1;
            }
        }

        public JobChunk GetChunk(int index)
        {
            Interlocked.Increment(ref GetChunkCalls);
            return new JobChunk(index, start: 0, length: 0);
        }
    }

    private readonly struct ArrayChunkSource : IJobChunkSource
    {
        private readonly JobChunk[] _chunks;

        private ArrayChunkSource(JobChunk[] chunks)
        {
            _chunks = chunks;
        }

        public int ChunkCount => _chunks.Length;

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
