namespace SomeEngine.Job.Tests;

public sealed class SchedulerQueuePrimitiveTests
{
    [Fact]
    public async Task ConcurrentProducersExecuteEveryWorkItemOnce()
    {
        JobSystem.Shutdown();
        JobSystem.Initialize(new JobRuntimeConfig { WorkerCount = 4 });
        var counts = new int[1024];
        try
        {
            Task[] producers = Enumerable.Range(0, 8).Select(producer => Task.Run(() =>
            {
                var handles = new JobHandle[128];
                for (int i = 0; i < handles.Length; i++)
                    handles[i] = JobSystem.Schedule(new CountJob(counts, producer * 128 + i));
                JobSystem.CombineDependencies(handles).Complete();
            })).ToArray();
            await Task.WhenAll(producers).WaitAsync(TimeSpan.FromSeconds(30));
            Assert.All(counts, count => Assert.Equal(1, count));
        }
        finally
        {
            JobSystem.Shutdown();
        }
    }
    private readonly struct CountJob(int[] counts, int index) : IJob
    {
        public void Execute() => Interlocked.Increment(ref counts[index]);
    }
}
