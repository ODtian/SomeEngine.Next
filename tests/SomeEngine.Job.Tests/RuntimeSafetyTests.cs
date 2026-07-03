namespace SomeEngine.Job.Tests;

public sealed class RuntimeSafetyTests
{
    public RuntimeSafetyTests()
    {
        JobSystem.ResetForTesting(workerCount: 2);
        SafetyJobs.Reset();
    }

    [Fact]
    public void ConcurrentSchedulingFromMultipleThreadsIsSafe()
    {
        const int threadCount = 4;
        const int jobsPerThread = 100;
        var handles = new List<JobHandle>(threadCount * jobsPerThread);
        var handlesLock = new Lock();
        var threads = new Thread[threadCount];

        for (var i = 0; i < threads.Length; i++)
        {
            threads[i] = new Thread(() =>
            {
                for (var j = 0; j < jobsPerThread; j++)
                {
                    var handle = JobSystem.Schedule(new SafetyJobs.IncrementJob());
                    lock (handlesLock)
                    {
                        handles.Add(handle);
                    }
                }
            });
            threads[i].Start();
        }

        foreach (var thread in threads)
        {
            thread.Join();
        }

        JobSystem.CombineDependencies(handles.ToArray()).Complete();

        Assert.Equal(threadCount * jobsPerThread, SafetyJobs.Counter);
    }

    [Fact]
    public void ConcurrentCompleteOnSameHandleIsSafe()
    {
        using var gate = new ManualResetEventSlim();
        var handle = JobSystem.Schedule(new SafetyJobs.GatedIncrementJob(gate));
        var errors = new List<Exception>();
        var errorsLock = new Lock();
        var threads = new Thread[4];

        for (var i = 0; i < threads.Length; i++)
        {
            threads[i] = new Thread(() =>
            {
                try
                {
                    handle.Complete();
                }
                catch (Exception ex)
                {
                    lock (errorsLock)
                    {
                        errors.Add(ex);
                    }
                }
            });
            threads[i].Start();
        }

        gate.Set();

        foreach (var thread in threads)
        {
            thread.Join();
        }

        Assert.Empty(errors);
        Assert.Equal(1, SafetyJobs.Counter);
    }

    [Fact]
    public void RootJobExceptionIsObservableFromComplete()
    {
        var handle = JobSystem.Schedule(new SafetyJobs.ThrowingRootJob());

        var ex = Assert.Throws<InvalidOperationException>(() => handle.Complete());

        Assert.Equal("root failed", ex.Message);
    }

    [Fact]
    public void ChildJobExceptionIsObservableFromParentComplete()
    {
        var handle = JobSystem.Schedule(new SafetyJobs.ParentSchedulesThrowingChildJob());

        var ex = Assert.Throws<InvalidOperationException>(() => handle.Complete());

        Assert.Equal("child failed", ex.Message);
    }

    [Fact]
    [Trait("Category", "Performance")]
    public void RefFreeWarmPathAllocationSmokeTestIsBounded()
    {
        JobSystem.ResetForTesting(workerCount: 0);

        for (var i = 0; i < 32; i++)
        {
            JobSystem.Schedule(new SafetyJobs.IncrementJob()).Complete();
        }

        var before = GC.GetAllocatedBytesForCurrentThread();

        JobSystem.Schedule(new SafetyJobs.IncrementJob()).Complete();

        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.InRange(allocated, 0, 2_048);
    }

    [Fact]
    public void OldRuntimeHandleIsCompletedAndCannotCompleteNewRuntimeState()
    {
        JobSystem.ResetForTesting(workerCount: 0);
        var old = JobSystem.Schedule(new SafetyJobs.IncrementJob());
        old.Complete();
        SafetyJobs.Counter = 0;

        JobSystem.ResetForTesting(workerCount: 0);
        var current = JobSystem.Schedule(new SafetyJobs.IncrementJob());

        Assert.Equal(old.Index, current.Index);
        Assert.Equal(old.Version, current.Version);
        Assert.NotEqual(old.Generation, current.Generation);
        Assert.True(old.IsCompleted);

        old.Complete();

        Assert.False(current.IsCompleted);
        Assert.Equal(0, SafetyJobs.Counter);

        current.Complete();
        Assert.Equal(1, SafetyJobs.Counter);
    }

    [Fact]
    public void InitializeCreatesNewRuntimeGenerationForHandles()
    {
        JobSystem.ResetForTesting(workerCount: 0);
        var old = JobSystem.Schedule(new SafetyJobs.IncrementJob());
        old.Complete();
        SafetyJobs.Counter = 0;

        JobSystem.Initialize(new JobRuntimeConfig { WorkerCount = 0 });
        var current = JobSystem.Schedule(new SafetyJobs.IncrementJob());

        Assert.Equal(old.Index, current.Index);
        Assert.Equal(old.Version, current.Version);
        Assert.NotEqual(old.Generation, current.Generation);

        old.Complete();

        Assert.False(current.IsCompleted);
        Assert.Equal(0, SafetyJobs.Counter);

        current.Complete();
        Assert.Equal(1, SafetyJobs.Counter);
    }

    [Fact]
    public void ShutdownCreatesNewRuntimeGenerationForHandles()
    {
        JobSystem.ResetForTesting(workerCount: 0);
        var old = JobSystem.Schedule(new SafetyJobs.IncrementJob());
        old.Complete();
        SafetyJobs.Counter = 0;

        JobSystem.Shutdown();
        var current = JobSystem.Schedule(new SafetyJobs.IncrementJob());

        Assert.Equal(old.Index, current.Index);
        Assert.Equal(old.Version, current.Version);
        Assert.NotEqual(old.Generation, current.Generation);

        old.Complete();

        Assert.False(current.IsCompleted);
        Assert.Equal(0, SafetyJobs.Counter);

        current.Complete();
        Assert.Equal(1, SafetyJobs.Counter);
    }

    [Fact]
    public void OldRuntimeResourceIsRejectedAndDoesNotAliasReusedResource()
    {
        JobSystem.ResetForTesting(workerCount: 0);
        var old = JobSystem.CreateResource("old-runtime-resource");

        JobSystem.ResetForTesting(workerCount: 0);
        JobSystem.SafetyMode = JobSafetyMode.Strict;
        var current = JobSystem.CreateResource("new-runtime-resource");

        Assert.Equal(old.Id, current.Id);
        Assert.Equal(old.Version, current.Version);
        Assert.NotEqual(old.Generation, current.Generation);

        var ex = Assert.Throws<JobResourceSafetyException>(() =>
            JobSystem.Schedule(new SafetyJobs.IncrementJob(), JobResourceAccess.Read(old)));

        Assert.Null(ex.ResourceName);
        Assert.Equal(old.Id, ex.ResourceId);
        Assert.Throws<JobResourceSafetyException>(() => JobSystem.ReleaseResource(old));

        JobSystem.Schedule(new SafetyJobs.IncrementJob(), JobResourceAccess.Read(current)).Complete();
        Assert.Equal(1, SafetyJobs.Counter);
        JobSystem.ReleaseResource(current);
    }

    [Fact]
    public void OldRuntimeTokenIsRejectedAndDoesNotAliasReusedToken()
    {
        JobSystem.ResetForTesting(workerCount: 0);
        var old = JobSystem.CreateResourceToken("old-runtime-token");

        JobSystem.ResetForTesting(workerCount: 0);
        JobSystem.SafetyMode = JobSafetyMode.Strict;
        var current = JobSystem.CreateResourceToken("new-runtime-token");

        Assert.Equal(old.Id, current.Id);
        Assert.Equal(old.Version, current.Version);
        Assert.NotEqual(old.Generation, current.Generation);

        var ex = Assert.Throws<JobResourceSafetyException>(() =>
            JobSystem.Schedule(new SafetyJobs.IncrementJob(), JobResourceAccess.Write(old)));

        Assert.Null(ex.ResourceName);
        Assert.Equal(old.Id, ex.ResourceId);
        Assert.Throws<JobResourceSafetyException>(() => JobSystem.ReleaseResourceToken(old));

        JobSystem.Schedule(new SafetyJobs.IncrementJob(), JobResourceAccess.Write(current)).Complete();
        Assert.Equal(1, SafetyJobs.Counter);
        JobSystem.ReleaseResourceToken(current);
    }

    [Fact]
    public void FastModeIgnoresOldRuntimeResourceAndTokenAccesses()
    {
        JobSystem.ResetForTesting(workerCount: 0);
        var oldResource = JobSystem.CreateResource("fast-old-resource");
        var oldToken = JobSystem.CreateResourceToken("fast-old-token");

        JobSystem.ResetForTesting(workerCount: 0);
        JobSystem.SafetyMode = JobSafetyMode.Fast;
        var current = JobSystem.CreateResource("fast-current-resource");

        JobSystem.ReleaseResource(oldResource);
        JobSystem.ReleaseResourceToken(oldToken);
        JobSystem.Schedule(new SafetyJobs.IncrementJob(), JobResourceAccess.Read(oldResource)).Complete();
        JobSystem.Schedule(new SafetyJobs.IncrementJob(), JobResourceAccess.Write(oldToken)).Complete();
        JobSystem.Schedule(new SafetyJobs.IncrementJob(), JobResourceAccess.Read(current)).Complete();

        Assert.Equal(3, SafetyJobs.Counter);
        JobSystem.ReleaseResource(current);
    }

    [Fact]
    public void LiveOldRuntimeJobCannotScheduleChildIntoNewRuntime()
    {
        using var started = new ManualResetEventSlim();
        using var gate = new ManualResetEventSlim();
        using var observed = new ManualResetEventSlim();
        JobSystem.ResetForTesting(workerCount: 1);

        var old = JobSystem.Schedule(new SafetyJobs.ScheduleAfterRuntimeSwapJob(started, gate, observed));

        Assert.True(started.Wait(1_000));

        var resetThread = new Thread(() => JobSystem.ResetForTesting(workerCount: 2));
        resetThread.Start();
        Assert.True(WaitUntilPublishedToNewRuntime(old));

        gate.Set();

        Assert.True(resetThread.Join(TimeSpan.FromSeconds(2)));
        Assert.True(observed.Wait(1_000));
        Assert.IsType<InvalidOperationException>(SafetyJobs.CapturedException);
        Assert.Equal(0, SafetyJobs.Counter);
    }

    [Fact]
    public void LiveOldRuntimeJobCannotCreateScopeResourceInNewRuntime()
    {
        using var started = new ManualResetEventSlim();
        using var gate = new ManualResetEventSlim();
        using var observed = new ManualResetEventSlim();
        JobSystem.ResetForTesting(workerCount: 1);

        var old = JobSystem.Schedule(new SafetyJobs.CreateScopeResourceAfterRuntimeSwapJob(started, gate, observed));

        Assert.True(started.Wait(1_000));

        var resetThread = new Thread(() => JobSystem.ResetForTesting(workerCount: 2));
        resetThread.Start();
        Assert.True(WaitUntilPublishedToNewRuntime(old));

        gate.Set();

        Assert.True(resetThread.Join(TimeSpan.FromSeconds(2)));
        Assert.True(observed.Wait(1_000));
        Assert.IsType<InvalidOperationException>(SafetyJobs.CapturedException);
        Assert.Equal(0, SafetyJobs.Counter);
    }

    private static bool WaitUntilPublishedToNewRuntime(JobHandle old)
    {
        var deadline = DateTime.UtcNow.AddSeconds(2);
        var spin = new SpinWait();
        while (DateTime.UtcNow < deadline)
        {
            if (old.IsCompleted)
            {
                return true;
            }

            spin.SpinOnce();
        }

        return false;
    }

    private static class SafetyJobs
    {
        internal static int Counter;
        internal static Exception? CapturedException;

        internal static void Reset()
        {
            Counter = 0;
            CapturedException = null;
        }

        internal struct IncrementJob : IJob
        {
            public void Execute()
            {
                Interlocked.Increment(ref Counter);
            }
        }

        internal readonly struct GatedIncrementJob : IJob
        {
            private readonly ManualResetEventSlim _gate;

            internal GatedIncrementJob(ManualResetEventSlim gate)
            {
                _gate = gate;
            }

            public void Execute()
            {
                _gate.Wait();
                Interlocked.Increment(ref Counter);
            }
        }

        internal struct ThrowingRootJob : IJob
        {
            public void Execute()
            {
                throw new InvalidOperationException("root failed");
            }
        }

        internal struct ParentSchedulesThrowingChildJob : IJob
        {
            public void Execute()
            {
                JobSystem.Schedule(new ThrowingChildJob());
            }
        }

        internal readonly struct ScheduleAfterRuntimeSwapJob : IJob
        {
            private readonly ManualResetEventSlim _started;
            private readonly ManualResetEventSlim _gate;
            private readonly ManualResetEventSlim _observed;

            internal ScheduleAfterRuntimeSwapJob(
                ManualResetEventSlim started,
                ManualResetEventSlim gate,
                ManualResetEventSlim observed)
            {
                _started = started;
                _gate = gate;
                _observed = observed;
            }

            public void Execute()
            {
                _started.Set();
                _gate.Wait();
                try
                {
                    JobSystem.Schedule(new IncrementJob());
                }
                catch (Exception ex)
                {
                    CapturedException = ex;
                    _observed.Set();
                }
            }
        }

        internal readonly struct CreateScopeResourceAfterRuntimeSwapJob : IJob
        {
            private readonly ManualResetEventSlim _started;
            private readonly ManualResetEventSlim _gate;
            private readonly ManualResetEventSlim _observed;

            internal CreateScopeResourceAfterRuntimeSwapJob(
                ManualResetEventSlim started,
                ManualResetEventSlim gate,
                ManualResetEventSlim observed)
            {
                _started = started;
                _gate = gate;
                _observed = observed;
            }

            public void Execute()
            {
                _started.Set();
                _gate.Wait();
                try
                {
                    _ = JobSystem.CreateScopeResource("stale-scope-resource");
                }
                catch (Exception ex)
                {
                    CapturedException = ex;
                    _observed.Set();
                }
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
