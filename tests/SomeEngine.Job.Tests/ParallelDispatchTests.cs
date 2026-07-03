namespace SomeEngine.Job.Tests;

public sealed class ParallelDispatchTests
{
    public ParallelDispatchTests()
    {
        JobSystem.ResetForTesting(workerCount: 2);
        ParallelJobs.Reset();
    }

    [Fact]
    public void LengthZeroReturnsCompletedHandleForDefaultDependency()
    {
        var values = Array.Empty<int>();

        var handle = JobSystem.ScheduleParallel(new ParallelJobs.MarkIndexJob(values), 0, 4);

        Assert.True(handle.IsCompleted);
        handle.Complete();
    }

    [Fact]
    public void InvalidBatchSizeThrowsEvenForZeroLength()
    {
        var values = Array.Empty<int>();

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            JobSystem.ScheduleParallel(new ParallelJobs.MarkIndexJob(values), values.Length, 0));
    }

    [Fact]
    public void LengthNotDivisibleByBatchSizeExecutesEveryIndexOnce()
    {
        var values = new int[10];

        JobSystem.ScheduleParallel(new ParallelJobs.MarkIndexJob(values), values.Length, 4).Complete();

        Assert.All(values, value => Assert.Equal(1, value));
    }

    [Fact]
    public void BatchSizeOneExecutesEveryIndexOnce()
    {
        var values = new int[8];

        JobSystem.ScheduleParallel(new ParallelJobs.MarkIndexJob(values), values.Length, 1).Complete();

        Assert.All(values, value => Assert.Equal(1, value));
    }

    [Fact]
    public void BatchSizeLargerThanLengthExecutesEveryIndexOnce()
    {
        var values = new int[5];

        JobSystem.ScheduleParallel(new ParallelJobs.MarkIndexJob(values), values.Length, 50).Complete();

        Assert.All(values, value => Assert.Equal(1, value));
    }

    [Fact]
    public void InvalidBatchSizeThrows()
    {
        var values = new int[1];

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            JobSystem.ScheduleParallel(new ParallelJobs.MarkIndexJob(values), values.Length, 0));
    }

    [Fact]
    public void ParallelBatchCountExceedingQueueCapacityFailsBeforePreparingState()
    {
        JobSystem.ResetForTesting(new JobRuntimeConfig
        {
            WorkerCount = 0,
            MaxQueuedWorkItems = 4,
            MaxCompletionStates = 4,
            MaxResourceStates = 4
        });
        var values = new int[5];

        var ex = Assert.Throws<InvalidOperationException>(() =>
            JobSystem.ScheduleParallel(new ParallelJobs.MarkIndexJob(values), values.Length, 1));

        Assert.Contains("Job queue capacity exhausted", ex.Message);
        Assert.Equal(0, JobSystem.GetStats().CompletionStateHighWater);
    }

    [Fact]
    public void ParallelJobScheduledFromParentAttachesToParentScope()
    {
        ParallelJobs.ParentValues = new int[9];

        JobSystem.Schedule(new ParallelJobs.ParentSchedulesParallelJob()).Complete();

        Assert.All(ParallelJobs.ParentValues, value => Assert.Equal(1, value));
    }

    private static class ParallelJobs
    {
        internal static int[] ParentValues = [];

        internal static void Reset()
        {
            ParentValues = [];
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

        internal struct ParentSchedulesParallelJob : IJob
        {
            public void Execute()
            {
                JobSystem.ScheduleParallel(new MarkIndexJob(ParentValues), ParentValues.Length, 2);
            }
        }
    }
}
