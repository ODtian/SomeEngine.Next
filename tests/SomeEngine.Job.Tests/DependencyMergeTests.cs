namespace SomeEngine.Job.Tests;

public sealed class DependencyMergeTests
{
    [Fact]
    public void SingleEffectivePredecessorReturnsItsHandle()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 0 });
        JobHandle predecessor = scheduler.Schedule(new EmptyJob());

        JobHandle combined = scheduler.CombineDependencies([default, predecessor, predecessor]);
        combined.Complete();

        Assert.Equal(predecessor, combined);
    }

    private readonly struct EmptyJob : IJob
    {
        public void Execute() { }
    }

    [Fact]
    public void LargeDuplicateInputsKeepAllPredecessorsForTheirConsumer()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 0 });
        int[] values = new int[256];
        JobHandle[] inputs = new JobHandle[512];
        for (int index = 0; index < values.Length; index++)
        {
            JobHandle source = scheduler.Schedule(new WriteValue(values, index), new JobScheduleOptions(JobPriority.Low));
            inputs[index] = source;
            inputs[inputs.Length - index - 1] = source;
        }
        int total = 0;
        JobHandle combined = scheduler.CombineDependencies(inputs);
        JobHandle consumer = scheduler.Schedule(new CallbackJob(() => total = values.Sum()),
            new JobScheduleOptions(JobPriority.High), combined);

        consumer.Complete();

        Assert.Equal(256, total);
    }

    [Fact]
    public void LargeDuplicateInputsDeliverFailureToTheirConsumer()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 0 });
        JobHandle[] inputs = new JobHandle[512];
        for (int index = 0; index < 255; index++)
        {
            JobHandle source = scheduler.Schedule(new EmptyJob());
            inputs[index] = source;
            inputs[inputs.Length - index - 1] = source;
        }
        JobHandle failed = scheduler.Schedule(new CallbackJob(() => throw new InvalidOperationException()));
        inputs[255] = failed;
        inputs[256] = failed;
        bool consumerRan = false;
        JobHandle consumer = scheduler.Schedule(new CallbackJob(() => consumerRan = true),
            scheduler.CombineDependencies(inputs));

        Assert.Throws<InvalidOperationException>(consumer.Complete);
        Assert.False(consumerRan);
    }

    private readonly record struct WriteValue(int[] Values, int Index) : IJob
    {
        public void Execute() => Values[Index] = 1;
    }

    private readonly record struct CallbackJob(Action Callback) : IJob
    {
        public void Execute() => Callback();
    }
}
