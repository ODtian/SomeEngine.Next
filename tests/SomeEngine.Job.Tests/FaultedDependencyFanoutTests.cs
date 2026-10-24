namespace SomeEngine.Job.Tests;

public sealed class FaultedDependencyFanoutTests
{
    [Fact]
    public void ObservedDependencyFailureCancelsEveryDependentWhileOtherJobsComplete()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 4 });
        for (int round = 0; round < 256; round++)
        {
            using var started = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            var failure = new InvalidOperationException();
            int[] dependentCalls = [0];
            int[] unrelatedCalls = [0];
            JobHandle dependency = scheduler.Schedule(new FailingJob(started, release, failure));
            try
            {
                Assert.True(started.Wait(TimeSpan.FromSeconds(30)));
                JobHandle[] dependents = Enumerable.Range(0, 128)
                    .Select(_ => scheduler.Schedule(new CountingJob(dependentCalls), dependency)).ToArray();
                release.Set();
                Assert.Same(failure, Record.Exception(dependency.Complete));
                for (int index = 0; index < 128; index++)
                    scheduler.Schedule(new CountingJob(unrelatedCalls)).Complete();
                foreach (JobHandle dependent in dependents)
                {
                    Exception? reported = Record.Exception(dependent.Complete);
                    if (reported is not null)
                    {
                        Assert.Same(failure, reported);
                    }
                    Assert.True(dependent.IsCompleted);
                }
                Assert.Equal(0, dependentCalls[0]);
                Assert.Equal(128, unrelatedCalls[0]);
            }
            finally
            {
                release.Set();
            }
        }
    }

    private readonly struct FailingJob(ManualResetEventSlim started, ManualResetEventSlim release,
        InvalidOperationException failure) : IJob
    {
        public void Execute()
        {
            started.Set();
            if (!release.Wait(TimeSpan.FromSeconds(30))) throw new TimeoutException();
            throw failure;
        }
    }

    private readonly struct CountingJob(int[] calls) : IJob
    {
        public void Execute() => Interlocked.Increment(ref calls[0]);
    }
}
