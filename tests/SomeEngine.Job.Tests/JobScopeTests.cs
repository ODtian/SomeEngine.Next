namespace SomeEngine.Job.Tests;

public sealed class JobScopeTests
{
    public JobScopeTests()
    {
        JobSystem.ResetForTesting(workerCount: 2);
        ScopeJobs.Reset();
    }

    [Fact]
    public void ParentWaitsForChildScheduledFromJobBody()
    {
        JobSystem.Schedule(new ScopeJobs.ParentSchedulesChildJob()).Complete();

        Assert.Equal(1, ScopeJobs.ChildCount);
    }

    [Fact]
    public void ParentWaitsForGrandchildScheduledFromChildJob()
    {
        JobSystem.Schedule(new ScopeJobs.ParentSchedulesGrandchildJob()).Complete();

        Assert.Equal(1, ScopeJobs.ChildCount);
        Assert.Equal(1, ScopeJobs.GrandchildCount);
    }

    [Fact]
    public void RecursiveDynamicFanoutCompletesExpectedCount()
    {
        JobSystem.Schedule(new ScopeJobs.RecursiveFanoutJob(4)).Complete();

        Assert.Equal(5, ScopeJobs.RecursiveCount);
    }

    [Fact]
    public void ChildExceptionFaultsParentHandle()
    {
        var handle = JobSystem.Schedule(new ScopeJobs.ParentSchedulesThrowingChildJob());

        var ex = Assert.Throws<InvalidOperationException>(() => handle.Complete());
        Assert.Equal("child failed", ex.Message);
    }

    private static class ScopeJobs
    {
        internal static int ChildCount;
        internal static int GrandchildCount;
        internal static int RecursiveCount;

        internal static void Reset()
        {
            ChildCount = 0;
            GrandchildCount = 0;
            RecursiveCount = 0;
        }

        internal struct ParentSchedulesChildJob : IJob
        {
            public void Execute()
            {
                JobSystem.Schedule(new ChildJob());
            }
        }

        internal struct ParentSchedulesGrandchildJob : IJob
        {
            public void Execute()
            {
                JobSystem.Schedule(new ChildSchedulesGrandchildJob());
            }
        }

        internal struct ParentSchedulesThrowingChildJob : IJob
        {
            public void Execute()
            {
                JobSystem.Schedule(new ThrowingChildJob());
            }
        }

        internal readonly struct RecursiveFanoutJob : IJob
        {
            private readonly int _remaining;

            internal RecursiveFanoutJob(int remaining)
            {
                _remaining = remaining;
            }

            public void Execute()
            {
                Interlocked.Increment(ref RecursiveCount);
                if (_remaining > 0)
                {
                    JobSystem.Schedule(new RecursiveFanoutJob(_remaining - 1));
                }
            }
        }

        private struct ChildJob : IJob
        {
            public void Execute()
            {
                Interlocked.Increment(ref ChildCount);
            }
        }

        private struct ChildSchedulesGrandchildJob : IJob
        {
            public void Execute()
            {
                Interlocked.Increment(ref ChildCount);
                JobSystem.Schedule(new GrandchildJob());
            }
        }

        private struct GrandchildJob : IJob
        {
            public void Execute()
            {
                Interlocked.Increment(ref GrandchildCount);
            }
        }

        private struct ThrowingChildJob : IJob
        {
            public void Execute()
            {
                throw new InvalidOperationException("child failed");
            }
        }
    }
}
