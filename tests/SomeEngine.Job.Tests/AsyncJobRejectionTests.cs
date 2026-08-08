namespace SomeEngine.Job.Tests;

public sealed class AsyncJobRejectionTests
{
    public AsyncJobRejectionTests()
    {
        JobSystem.ResetForTesting(workerCount: 2);
    }

    [Fact]
    public void AsyncVoidJobIsRejectedBeforeResourceOrSchedulerMutation()
    {
        AsyncVoidJob.Reset();
        var access = JobResourceAccess.Write(new JobResourceKey());
        JobRuntimeStats before = JobSystem.GetStats();

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
            JobSystem.Schedule(new AsyncVoidJob(), access));

        Assert.Contains("async", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("synchronously", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, Volatile.Read(ref AsyncVoidJob.BodyCount));
        Assert.False(SpinWait.SpinUntil(
            static () => Volatile.Read(ref AsyncVoidJob.ContinuationCount) != 0,
            TimeSpan.FromMilliseconds(100)));
        AssertStatsEqual(before, JobSystem.GetStats());
    }

    [Fact]
    public void ExplicitAsyncVoidJobImplementationIsRejected()
    {
        ExplicitAsyncVoidJob.Reset();
        JobRuntimeStats before = JobSystem.GetStats();

        Assert.Throws<InvalidOperationException>(() =>
            JobSystem.Schedule(new ExplicitAsyncVoidJob()));

        Assert.Equal(0, Volatile.Read(ref ExplicitAsyncVoidJob.BodyCount));
        Assert.False(SpinWait.SpinUntil(
            static () => Volatile.Read(ref ExplicitAsyncVoidJob.ContinuationCount) != 0,
            TimeSpan.FromMilliseconds(100)));
        AssertStatsEqual(before, JobSystem.GetStats());
    }

    [Fact]
    public void AsyncVoidParallelJobIsRejectedEvenForAnEmptyRange()
    {
        AsyncVoidParallelJob.Reset();
        JobRuntimeStats before = JobSystem.GetStats();

        Assert.Throws<InvalidOperationException>(() =>
            JobSystem.ScheduleParallel(
                new AsyncVoidParallelJob(),
                length: 0,
                batchSize: 1));
        Assert.Throws<InvalidOperationException>(() =>
            JobSystem.ScheduleParallel(
                new AsyncVoidParallelJob(),
                length: 4,
                batchSize: 1));

        Assert.Equal(0, Volatile.Read(ref AsyncVoidParallelJob.BodyCount));
        Assert.False(SpinWait.SpinUntil(
            static () => Volatile.Read(ref AsyncVoidParallelJob.ContinuationCount) != 0,
            TimeSpan.FromMilliseconds(100)));
        AssertStatsEqual(before, JobSystem.GetStats());
    }

    [Fact]
    public void SynchronousJobAndParallelJobRemainSchedulable()
    {
        SynchronousJob.Count = 0;
        SynchronousParallelJob.Count = 0;

        JobSystem.Schedule(new SynchronousJob()).Complete();
        JobSystem.ScheduleParallel(
            new SynchronousParallelJob(),
            length: 4,
            batchSize: 1).Complete();

        Assert.Equal(1, Volatile.Read(ref SynchronousJob.Count));
        Assert.Equal(4, Volatile.Read(ref SynchronousParallelJob.Count));
    }

    private static void AssertStatsEqual(JobRuntimeStats expected, JobRuntimeStats actual)
    {
        Assert.Equal(expected.ScheduledJobs, actual.ScheduledJobs);
        Assert.Equal(expected.ExecutedWorkItems, actual.ExecutedWorkItems);
        Assert.Equal(expected.CompletedHandles, actual.CompletedHandles);
        Assert.Equal(expected.FaultedWorkItems, actual.FaultedWorkItems);
        Assert.Equal(expected.WaitedCompletes, actual.WaitedCompletes);
        Assert.Equal(expected.QueuedWorkItems, actual.QueuedWorkItems);
        Assert.Equal(expected.LocalQueuedWorkItems, actual.LocalQueuedWorkItems);
        Assert.Equal(expected.StolenWorkItems, actual.StolenWorkItems);
        Assert.Equal(expected.RefFreeJobs, actual.RefFreeJobs);
        Assert.Equal(expected.RefContainingJobs, actual.RefContainingJobs);
        Assert.Equal(expected.ManagedPayloadWarnings, actual.ManagedPayloadWarnings);
        Assert.Equal(expected.ResourceConflictChecks, actual.ResourceConflictChecks);
        Assert.Equal(expected.ResourceConflictCheckSteps, actual.ResourceConflictCheckSteps);
        Assert.Equal(expected.QueueHighWater, actual.QueueHighWater);
        Assert.Equal(expected.CompletionStateHighWater, actual.CompletionStateHighWater);
        Assert.Equal(expected.ResourceStateHighWater, actual.ResourceStateHighWater);
    }

    private struct AsyncVoidJob : IJob
    {
        internal static int BodyCount;
        internal static int ContinuationCount;

        internal static void Reset()
        {
            BodyCount = 0;
            ContinuationCount = 0;
        }

        public async void Execute()
        {
            Interlocked.Increment(ref BodyCount);
            await Task.Yield();
            Interlocked.Increment(ref ContinuationCount);
        }
    }

    private struct ExplicitAsyncVoidJob : IJob
    {
        internal static int BodyCount;
        internal static int ContinuationCount;

        internal static void Reset()
        {
            BodyCount = 0;
            ContinuationCount = 0;
        }

        async void IJob.Execute()
        {
            Interlocked.Increment(ref BodyCount);
            await Task.Yield();
            Interlocked.Increment(ref ContinuationCount);
        }
    }

    private struct AsyncVoidParallelJob : IJobParallelFor
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

    private struct SynchronousJob : IJob
    {
        internal static int Count;

        public void Execute()
        {
            Interlocked.Increment(ref Count);
        }
    }

    private struct SynchronousParallelJob : IJobParallelFor
    {
        internal static int Count;

        public void Execute(int index)
        {
            _ = index;
            Interlocked.Increment(ref Count);
        }
    }
}
