namespace SomeEngine.Job.Tests;

public sealed class JobSystemTests
{
    public JobSystemTests() => JobSystem.Initialize(new JobRuntimeConfig { WorkerCount = 2 });

    [Fact]
    public void SchedulingOneJobIncrementsItsResultOnce()
    {
        int[] counts = [0];
        JobSystem.Schedule(new IncrementJob(counts)).Complete();
        Assert.Equal(1, counts[0]);
    }

    [Fact]
    public void ParallelDispatchWritesEveryIndex()
    {
        var values = new int[100];
        JobSystem.ScheduleParallel(new WriteIndexJob(values), values.Length, 10).Complete();
        Assert.Equal(Enumerable.Range(0, values.Length).Select(index => index * 2), values);
    }

    [Fact]
    public void RecursiveCompletionWaitsForEveryChild()
    {
        int[] counts = [0];
        JobSystem.Schedule(new RecursiveJob(counts, 10)).Complete();
        Assert.Equal(11, counts[0]);
    }

    [Fact]
    public void CompletingSequentialJobsAllowsFurtherScheduling()
    {
        int[] counts = [0];
        for (int index = 0; index < 17_000; index++)
            JobSystem.Schedule(new IncrementJob(counts)).Complete();
        Assert.Equal(17_000, counts[0]);
    }

    [Fact]
    public void CompletingDependencyChainsAllowsFurtherScheduling()
    {
        int[] counts = [0];
        for (int index = 0; index < 17_000; index++)
        {
            JobHandle first = JobSystem.Schedule(new IncrementJob(counts));
            JobSystem.Schedule(new IncrementJob(counts), first).Complete();
        }
        Assert.Equal(34_000, counts[0]);
    }

    private readonly struct IncrementJob(int[] counts) : IJob
    {
        public void Execute() => Interlocked.Increment(ref counts[0]);
    }

    private readonly struct WriteIndexJob(int[] values) : IJobParallelFor
    {
        public void Execute(int index) => values[index] = index * 2;
    }

    private readonly struct RecursiveJob(int[] counts, int depth) : IJob
    {
        public void Execute()
        {
            Interlocked.Increment(ref counts[0]);
            if (depth > 0) JobSystem.Schedule(new RecursiveJob(counts, depth - 1)).Complete();
        }
    }
}
