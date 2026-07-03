using SomeEngine.Job;

namespace SomeEngine.Job.Dots.Tests;

public sealed class DotsConsumerTests
{
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
