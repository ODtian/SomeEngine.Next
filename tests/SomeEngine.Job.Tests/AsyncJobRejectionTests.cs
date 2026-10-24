namespace SomeEngine.Job.Tests;

public sealed class AsyncJobRejectionTests
{
    public AsyncJobRejectionTests() => JobSystem.Initialize(new JobRuntimeConfig { WorkerCount = 2 });

    [Fact]
    public void AsyncVoidJobIsRejectedBeforeExecution()
    {
        int[] counts = [0, 0];
        var access = JobResourceAccess.Write(JobSystem.CreateResource("async job rejection"));
        Assert.Throws<InvalidOperationException>(() => JobSystem.Schedule(new AsyncVoidJob(counts), access));
        Assert.Equal(new[] { 0, 0 }, counts);
    }

    [Fact]
    public void ExplicitAsyncVoidJobImplementationIsRejected()
    {
        int[] counts = [0, 0];
        Assert.Throws<InvalidOperationException>(() => JobSystem.Schedule(new ExplicitAsyncVoidJob(counts)));
        Assert.Equal(new[] { 0, 0 }, counts);
    }

    [Fact]
    public void AsyncVoidParallelJobIsRejectedEvenForAnEmptyRange()
    {
        int[] counts = [0, 0];
        Assert.Throws<InvalidOperationException>(() =>
            JobSystem.ScheduleParallel(new AsyncVoidParallelJob(counts), length: 0, batchSize: 1));
        Assert.Throws<InvalidOperationException>(() =>
            JobSystem.ScheduleParallel(new AsyncVoidParallelJob(counts), length: 4, batchSize: 1));
        Assert.Equal(new[] { 0, 0 }, counts);
    }

    [Fact]
    public void SynchronousJobAndParallelJobRemainSchedulable()
    {
        int[] counts = [0, 0];
        JobSystem.Schedule(new SynchronousJob(counts)).Complete();
        JobSystem.ScheduleParallel(new SynchronousParallelJob(counts), length: 4, batchSize: 1).Complete();
        Assert.Equal(new[] { 1, 4 }, counts);
    }

    private readonly struct AsyncVoidJob(int[] counts) : IJob
    {
        public async void Execute()
        {
            Interlocked.Increment(ref counts[0]);
            await Task.Yield();
            Interlocked.Increment(ref counts[1]);
        }
    }

    private readonly struct ExplicitAsyncVoidJob(int[] counts) : IJob
    {
        async void IJob.Execute()
        {
            Interlocked.Increment(ref counts[0]);
            await Task.Yield();
            Interlocked.Increment(ref counts[1]);
        }
    }

    private readonly struct AsyncVoidParallelJob(int[] counts) : IJobParallelFor
    {
        public async void Execute(int index)
        {
            Interlocked.Increment(ref counts[0]);
            await Task.Yield();
            Interlocked.Increment(ref counts[1]);
        }
    }

    private readonly struct SynchronousJob(int[] counts) : IJob
    {
        public void Execute() => Interlocked.Increment(ref counts[0]);
    }

    private readonly struct SynchronousParallelJob(int[] counts) : IJobParallelFor
    {
        public void Execute(int index) => Interlocked.Increment(ref counts[1]);
    }
}
