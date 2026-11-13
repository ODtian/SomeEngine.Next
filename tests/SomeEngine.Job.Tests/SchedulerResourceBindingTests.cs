namespace SomeEngine.Job.Tests;

public sealed class SchedulerResourceBindingTests
{
    [Fact]
    public void ArraySliceWriterAndMemoryReaderShareTheirSchedulerData()
    {
        using var scheduler = new Scheduler(new JobConfiguration { WorkerCount = 2 });
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
    public void ResourceProvidersUseEachSelectedScheduler()
    {
        using var first = new Scheduler(new JobConfiguration { WorkerCount = 0 });
        using var second = new Scheduler(new JobConfiguration { WorkerCount = 0 });
        var firstData = new CounterData([2, 3]);
        var secondData = new CounterData([5, 7]);
        int[] result = new int[2];

        JobHandle incremented = first.Schedule(new IncrementValues(firstData.Values),
            JobResourceAccess.Write<CounterData, CounterResources>(first, ref firstData));
        JobHandle scaled = first.ScheduleParallel(new ScaleValues(firstData.Values), 2, 1,
            [JobResourceAccess.Write(first, firstData.Values)], incremented);
        JobHandle added = first.ScheduleParallel(new AddValues(firstData.Values), 2, 1,
            [JobResourceAccess.Write(first, firstData.Values.AsMemory())], scaled);
        first.ScheduleParallel(new CopyValues(firstData.Values, result), 2, 1,
            [JobResourceAccess.Read(first, firstData.Values), JobResourceAccess.Write(first, result)], added).Complete();
        second.Schedule(new IncrementValues(secondData.Values),
            JobResourceAccess.Write<CounterData, CounterResources>(second, ref secondData)).Complete();

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

    private readonly record struct ScaleValues(int[] Values) : IJobParallelFor
    {
        public void Execute(int index) => Values[index] *= 2;
    }

    private readonly record struct AddValues(int[] Values) : IJobParallelFor
    {
        public void Execute(int index) => Values[index] += 3;
    }

    private readonly record struct CopyValues(int[] Values, int[] Result) : IJobParallelFor
    {
        public void Execute(int index) => Result[index] = Values[index];
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
