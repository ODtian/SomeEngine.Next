namespace SomeEngine.Job.Tests;

public sealed class NestedCompletionTests
{
    [Fact]
    public void CompletingAChildPreservesEverySiblingAndChild()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig
        {
            WorkerCount = 0,
            ManagedPayloadPolicy = ManagedPayloadPolicy.Allow,
        });
        var counts = new int[6];

        JobHandle first = scheduler.Schedule(new ActionJob(() => counts[0]++));
        JobHandle parent = scheduler.Schedule(new ActionJob(() =>
        {
            counts[1]++;
            scheduler.Schedule(new ActionJob(() => counts[3]++));
            scheduler.Schedule(new ActionJob(() => counts[4]++));
            JobHandle child = scheduler.Schedule(new ActionJob(() => counts[5]++));
            child.Complete();
        }));
        JobHandle sibling = scheduler.Schedule(new ActionJob(() => counts[2]++));

        scheduler.CombineDependencies([first, parent, sibling]).Complete();

        Assert.All(counts, count => Assert.Equal(1, count));
    }

    private readonly struct ActionJob(Action action) : IJob
    {
        public void Execute() => action();
    }
}
