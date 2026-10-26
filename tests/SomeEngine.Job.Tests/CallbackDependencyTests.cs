namespace SomeEngine.Job.Tests;

public sealed class CallbackDependencyTests
{
    [Fact]
    public void CallbackCompletingUnfinishedWorkReportsInvalidOperation()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 0 });
        int value = 0;
        JobHandle input = scheduler.Schedule(
            new CallbackJob(() => value = 7), new JobScheduleOptions(JobPriority.Low));
        JobHandle callback = scheduler.Schedule(
            new CallbackJob(input.Complete), new JobScheduleOptions(JobPriority.High));

        Assert.Throws<InvalidOperationException>(callback.Complete);
        input.Complete();

        Assert.Equal(7, value);
    }

    [Fact]
    public void ChildDependingOnCombinedAncestorReportsInvalidOperation()
    {
        var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 1 });
        using var inputsReady = new ManualResetEventSlim();
        using var submissionFinished = new ManualResetEventSlim();
        JobHandle combined = default;
        Exception? submissionFailure = null;
        JobHandle parent = scheduler.Schedule(new CallbackJob(() =>
        {
            Assert.True(inputsReady.Wait(TimeSpan.FromSeconds(10)));
            scheduler.Schedule(new CallbackJob(() =>
            {
                submissionFailure = Record.Exception(() =>
                    scheduler.Schedule(new CallbackJob(() => { }), combined));
                submissionFinished.Set();
            }));
        }));
        JobHandle successor = scheduler.Schedule(new CallbackJob(() => { }), parent);
        JobHandle otherSuccessor = scheduler.Schedule(new CallbackJob(() => { }), parent);
        combined = scheduler.CombineDependencies([successor, otherSuccessor]);
        inputsReady.Set();

        Assert.True(submissionFinished.Wait(TimeSpan.FromSeconds(10)));
        Assert.IsType<InvalidOperationException>(submissionFailure);
        combined.Complete();
        parent.Complete();
        scheduler.Dispose();
    }

    private readonly struct CallbackJob(Action action) : IJob
    {
        public void Execute() => action();
    }
}
