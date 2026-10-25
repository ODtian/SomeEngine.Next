namespace SomeEngine.Job.Tests;

public sealed class SchedulerResourceBindingTests
{
    [Fact]
    public void ArraySliceWriterAndMemoryReaderShareTheirSchedulerData()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
        int[] values = [3, 5, 7];
        int[] result = [0];
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        JobHandle writer = scheduler.Schedule(
            new WriteValue(values, 1, 17, entered, release),
            JobResourceAccess.Write(scheduler, values, values.AsSpan(1, 1)));
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            ReadOnlyMemory<int> memory = values.AsMemory(1, 2);
            JobHandle reader = scheduler.Schedule(
                new CopyValue(memory, result),
                JobResourceAccess.Read(scheduler, memory, 0, 1));
            release.Set();
            reader.Complete();

            Assert.Equal(17, result[0]);
        }
        finally
        {
            release.Set();
            writer.Complete();
        }
    }

    [Fact]
    public void DotsAndResourceProvidersUseEachSelectedScheduler()
    {
        using var first = new Scheduler(new JobRuntimeConfig { WorkerCount = 0 });
        using var second = new Scheduler(new JobRuntimeConfig { WorkerCount = 0 });
        var firstData = new CounterData([2, 3]);
        var secondData = new CounterData([5, 7]);
        int[] result = new int[2];

        JobHandle incremented = new IncrementValues(firstData.Values).Schedule(
            first, JobResourceAccess.Write<CounterData, CounterResources>(first, ref firstData));
        JobHandle scaled = new ScaleValues(firstData.Values).Schedule(
            first, 2, 1, JobResourceAccess.Write(first, firstData.Values), incremented);
        JobHandle added = new AddValues(firstData.Values).ScheduleParallel(
            first, 2, 1, JobResourceAccess.Write(first, firstData.Values.AsMemory()), scaled);
        new CopyChunk(firstData.Values, result).Schedule(
            first, new TwoChunks(), 1,
            [JobResourceAccess.Read(first, firstData.Values), JobResourceAccess.Write(first, result)], added).Complete();
        new IncrementValues(secondData.Values).Schedule(
            second, JobResourceAccess.Write<CounterData, CounterResources>(second, ref secondData)).Complete();

        Assert.Equal([9, 11, 6, 8], result.Concat(secondData.Values));
    }

    private readonly record struct CounterData(int[] Values);

    private readonly struct CounterResources : IJobResourceProvider<CounterData, WholeAccess>
    {
        public static JobResourceAccess Read(Scheduler scheduler, ref CounterData data, WholeAccess access) =>
            JobResourceAccess.Read(scheduler, data.Values);

        public static JobResourceAccess Write(Scheduler scheduler, ref CounterData data, WholeAccess access) =>
            JobResourceAccess.Write(scheduler, data.Values);

        public static JobResourceAccess Exclusive(Scheduler scheduler, ref CounterData data, WholeAccess access) =>
            JobResourceAccess.Exclusive(scheduler, data.Values);
    }

    private readonly record struct IncrementValues(int[] Values) : IJob
    {
        public void Execute()
        {
            for (int i = 0; i < Values.Length; i++)
                Values[i]++;
        }
    }

    private readonly record struct ScaleValues(int[] Values) : IJobFor
    {
        public void Execute(int index) => Values[index] *= 2;
    }

    private readonly record struct AddValues(int[] Values) : IJobParallelFor
    {
        public void Execute(int index) => Values[index] += 3;
    }

    private readonly struct TwoChunks : IJobChunkSource
    {
        public int ChunkCount => 2;
        public JobChunk GetChunk(int index) => new(index, index, 1);
    }

    private readonly record struct CopyChunk(int[] Values, int[] Result) : IJobChunk
    {
        public void Execute(JobChunk chunk) => Result[chunk.Start] = Values[chunk.Start];
    }

    private readonly record struct WriteValue(
        int[] Values,
        int Index,
        int Value,
        ManualResetEventSlim Entered,
        ManualResetEventSlim Release) : IJob
    {
        public void Execute()
        {
            Entered.Set();
            Release.Wait();
            Values[Index] = Value;
        }
    }

    private readonly record struct CopyValue(ReadOnlyMemory<int> Source, int[] Result) : IJob
    {
        public void Execute() => Result[0] = Source.Span[0];
    }
}
