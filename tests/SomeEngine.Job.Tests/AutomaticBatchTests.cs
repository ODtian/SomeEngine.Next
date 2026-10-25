namespace SomeEngine.Job.Tests;

public sealed class AutomaticBatchTests
{
    [Fact]
    public void EachSchedulerStartsWithItsOwnBatchCost()
    {
        using var first = new Scheduler(new JobRuntimeConfig
        {
            WorkerCount = 0, AutoBatchTargetMicroseconds = 1, EnableCounters = false
        });
        using var second = new Scheduler(new JobRuntimeConfig
        {
            WorkerCount = 0, AutoBatchTargetMicroseconds = 1, EnableCounters = false
        });
        var firstData = new BatchData(64);
        var secondData = new BatchData(64);

        first.ScheduleParallel(new CountBatch(firstData, 16_384), 64,
            JobScheduleOptions.AutomaticBatchSize).Complete();
        second.ScheduleParallel(new CountBatch(secondData, 0), 64,
            JobScheduleOptions.AutomaticBatchSize).Complete();

        Assert.Equal(5, secondData.Calls[4]);
    }

    [Fact]
    public void DifferentWorkSizesKeepTheirOwnBatchCost()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig
        {
            WorkerCount = 0, AutoBatchTargetMicroseconds = 1, EnableCounters = false
        });
        var large = new BatchData(64);
        var small = new BatchData(16);

        scheduler.ScheduleParallel(new CountBatch(large, 16_384), 64,
            JobScheduleOptions.AutomaticBatchSize).Complete();
        scheduler.ScheduleParallel(new CountBatch(small, 0), 16,
            JobScheduleOptions.AutomaticBatchSize).Complete();

        Assert.Equal(2, small.Calls[1]);
    }

    [Fact]
    public void SynchronousRangesTeachTheirSchedulerBatchCost()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig
        {
            WorkerCount = 0, AutoBatchTargetMicroseconds = 1, EnableCounters = false
        });
        var heavy = new BatchData(64);
        var light = new BatchData(64);

        scheduler.ExecuteParallel(new CountBatch(heavy, 16_384), 64,
            JobScheduleOptions.AutomaticBatchSize);
        scheduler.ScheduleParallel(new CountBatch(light, 0), 64,
            JobScheduleOptions.AutomaticBatchSize).Complete();

        Assert.Equal(1, light.Calls[4]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SynchronousStatisticsFollowTheSchedulerSetting(bool enabled)
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 0, EnableCounters = enabled });
        var data = new BatchData(8);

        scheduler.ExecuteParallel(new CountBatch(data, 0), 8, batchSize: 1);
        JobRuntimeStats stats = scheduler.GetStats();

        Assert.Equal((enabled ? 1L : 0L, enabled ? 8L : 0L),
            (stats.ScheduledJobs, stats.ExecutedWorkItems));
        Assert.All(data.Calls, value => Assert.Equal(1, value));
    }

    [Fact]
    public void CategorySubmissionsUseCategoryQueueStatistics()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 1, EnableCounters = true });
        using var submitted = new ManualResetEventSlim();
        JobCategory category = scheduler.CreateCategory(new JobCategoryConfig { Name = "range" });
        var options = new JobScheduleOptions { Category = category };
        int[] result = [0];

        JobHandle work = scheduler.Schedule(new StartCategoryChild(scheduler, options, submitted, result), options);
        Assert.True(submitted.Wait(TimeSpan.FromSeconds(5)));
        work.Complete();
        JobRuntimeStats stats = scheduler.GetStats();

        Assert.Equal((2L, 0L), (stats.QueuedWorkItems, stats.LocalQueuedWorkItems));
        Assert.Equal(1, result[0]);
    }

    private readonly record struct StartCategoryChild(Scheduler Scheduler, JobScheduleOptions Options,
        ManualResetEventSlim Submitted, int[] Result) : IJob
    {
        public void Execute()
        {
            Scheduler.Schedule(new WriteResult(Result), Options);
            Submitted.Set();
        }
    }

    private readonly record struct WriteResult(int[] Result) : IJob
    {
        public void Execute() => Result[0] = 1;
    }

    private sealed class BatchData(int length)
    {
        internal int[] Calls { get; } = new int[length];
        internal double[] Values { get; } = new double[length];
    }

    private struct CountBatch(BatchData data, int iterations) : IJobParallelFor
    {
        private int _calls;

        public void Execute(int index)
        {
            double value = index + 1;
            for (int iteration = 0; iteration < iterations; iteration++)
                value = Math.Sqrt(value + 3);
            data.Values[index] = value;
            data.Calls[index] = ++_calls;
        }
    }
}
