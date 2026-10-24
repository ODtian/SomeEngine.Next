namespace SomeEngine.Job.Tests;

public sealed class JobScopeTests
{
    private readonly ScopeData _data = new();

    public JobScopeTests()
    {
        JobSystem.Initialize(new JobRuntimeConfig { WorkerCount = 2 });
        _data.Reset();
    }

    [Fact]
    public void ParentWaitsForChildScheduledFromJobBody()
    {
        JobSystem.Schedule(new ScopeData.ParentSchedulesChildJob(_data)).Complete();

        Assert.Equal(1, _data.ChildCount);
    }

    [Fact]
    public void ParentWaitsForGrandchildScheduledFromChildJob()
    {
        JobSystem.Schedule(new ScopeData.ParentSchedulesGrandchildJob(_data)).Complete();

        Assert.Equal(1, _data.ChildCount);
        Assert.Equal(1, _data.GrandchildCount);
    }

    [Fact]
    public void RecursiveDynamicFanoutCompletesExpectedCount()
    {
        JobSystem.Schedule(new ScopeData.RecursiveFanoutJob(_data, 4)).Complete();

        Assert.Equal(5, _data.RecursiveCount);
    }

    [Fact]
    public void ChildExceptionFaultsParentHandle()
    {
        var handle = JobSystem.Schedule(new ScopeData.ParentSchedulesThrowingChildJob());

        var ex = Assert.Throws<InvalidOperationException>(() => handle.Complete());
    }

    private sealed class ScopeData
    {
        internal int ChildCount;
        internal int GrandchildCount;
        internal int RecursiveCount;

        internal void Reset()
        {
            ChildCount = 0;
            GrandchildCount = 0;
            RecursiveCount = 0;
        }

        internal struct ParentSchedulesChildJob : IJob
        {
            private readonly ScopeData _data;

            internal ParentSchedulesChildJob(ScopeData data) => _data = data;

            public void Execute()
            {
                JobSystem.Schedule(new ChildJob(_data));
            }
        }

        internal struct ParentSchedulesGrandchildJob : IJob
        {
            private readonly ScopeData _data;

            internal ParentSchedulesGrandchildJob(ScopeData data) => _data = data;

            public void Execute()
            {
                JobSystem.Schedule(new ChildSchedulesGrandchildJob(_data));
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
            private readonly ScopeData _data;

            private readonly int _remaining;

            internal RecursiveFanoutJob(ScopeData data, int remaining)
            {
                _data = data;
                _remaining = remaining;
            }

            public void Execute()
            {
                Interlocked.Increment(ref _data.RecursiveCount);
                if (_remaining > 0)
                {
                    JobSystem.Schedule(new RecursiveFanoutJob(_data, _remaining - 1));
                }
            }
        }

        private struct ChildJob : IJob
        {
            private readonly ScopeData _data;

            internal ChildJob(ScopeData data) => _data = data;

            public void Execute()
            {
                Interlocked.Increment(ref _data.ChildCount);
            }
        }

        private struct ChildSchedulesGrandchildJob : IJob
        {
            private readonly ScopeData _data;

            internal ChildSchedulesGrandchildJob(ScopeData data) => _data = data;

            public void Execute()
            {
                Interlocked.Increment(ref _data.ChildCount);
                JobSystem.Schedule(new GrandchildJob(_data));
            }
        }

        private struct GrandchildJob : IJob
        {
            private readonly ScopeData _data;

            internal GrandchildJob(ScopeData data) => _data = data;

            public void Execute()
            {
                Interlocked.Increment(ref _data.GrandchildCount);
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
