using SomeEngine.Job;

namespace SomeEngine.Job.Dots.Tests;

public sealed class DotsConsumerTests
{
    public DotsConsumerTests() => JobSystem.Initialize(new JobRuntimeConfig { WorkerCount = 2 });

    [Fact]
    public void AdapterScheduleCompletesSingleWork()
    {
        int[] values = [0];
        new IncrementJob(values, 3).Schedule().Complete();
        Assert.Equal(3, values[0]);
    }

    [Fact]
    public void AdapterScheduleCompletesEveryIndex()
    {
        var values = new int[4];
        new MarkForJob(values).Schedule(values.Length, 2).Complete();
        Assert.All(values, value => Assert.Equal(1, value));
    }

    [Fact]
    public void AdapterScheduleCompletesEveryChunk()
    {
        var values = new int[5];
        var chunks = new ArrayChunkSource([new JobChunk(0, 0, 2), new JobChunk(1, 2, 3)]);
        new FillChunkJob(values).Schedule(chunks, batchSize: 1).Complete();
        Assert.All(values, value => Assert.Equal(1, value));
    }

    [Fact]
    public void AsyncVoidJobForIsRejectedBeforeExecution()
    {
        int[] counts = [0, 0];
        Assert.Throws<InvalidOperationException>(() => new AsyncJobFor(counts).Schedule(length: 4, batchSize: 1));
        Assert.Equal(new[] { 0, 0 }, counts);
    }

    [Fact]
    public void AsyncVoidJobChunkIsRejectedBeforeReadingItsSource()
    {
        int[] counts = [0, 0];
        int[] reads = [0, 0];
        var source = new CountingChunkSource(reads);
        Assert.Throws<InvalidOperationException>(() => new AsyncJobChunk(counts).Schedule(source, batchSize: 1));
        Assert.Equal(new[] { 0, 0 }, reads);
        Assert.Equal(new[] { 0, 0 }, counts);
    }

    [Fact]
    [Trait("Category", "Performance")]
    public void RepeatedJobForSchedulingDoesNotThrow()
    {
        JobSystem.Initialize(new JobRuntimeConfig { WorkerCount = 0 });
        for (int index = 0; index < 160; index++)
            new EmptyJobFor().Schedule(length: 1, batchSize: 1).Complete();
    }

    [Fact]
    [Trait("Category", "Performance")]
    public void RepeatedJobChunkSchedulingDoesNotThrow()
    {
        JobSystem.Initialize(new JobRuntimeConfig { WorkerCount = 0 });
        var source = new ArrayChunkSource([new JobChunk(0, 0, 0)]);
        for (int index = 0; index < 160; index++)
            new EmptyJobChunk().Schedule(source, batchSize: 1).Complete();
    }

    private readonly struct IncrementJob(int[] values, int amount) : IJob
    {
        public void Execute() => Interlocked.Add(ref values[0], amount);
    }

    private readonly struct MarkForJob(int[] values) : IJobFor
    {
        public void Execute(int index) => Interlocked.Increment(ref values[index]);
    }

    private readonly struct FillChunkJob(int[] values) : IJobChunk
    {
        public void Execute(JobChunk chunk)
        {
            for (int index = chunk.Start; index < chunk.Start + chunk.Length; index++)
                Interlocked.Increment(ref values[index]);
        }
    }

    private readonly struct AsyncJobFor(int[] counts) : IJobFor
    {
        public async void Execute(int index)
        {
            Interlocked.Increment(ref counts[0]);
            await Task.Yield();
            Interlocked.Increment(ref counts[1]);
        }
    }

    private readonly struct AsyncJobChunk(int[] counts) : IJobChunk
    {
        public async void Execute(JobChunk chunk)
        {
            Interlocked.Increment(ref counts[0]);
            await Task.Yield();
            Interlocked.Increment(ref counts[1]);
        }
    }

    private readonly struct CountingChunkSource(int[] reads) : IJobChunkSource
    {
        public int ChunkCount { get { Interlocked.Increment(ref reads[0]); return 1; } }
        public JobChunk GetChunk(int index)
        {
            Interlocked.Increment(ref reads[1]);
            return new JobChunk(index, 0, 0);
        }
    }

    private readonly struct ArrayChunkSource(JobChunk[] chunks) : IJobChunkSource
    {
        public int ChunkCount => chunks.Length;
        public JobChunk GetChunk(int index) => chunks[index];
    }

    private readonly struct EmptyJobFor : IJobFor
    {
        public void Execute(int index) { }
    }

    private readonly struct EmptyJobChunk : IJobChunk
    {
        public void Execute(JobChunk chunk) { }
    }
}
