namespace SomeEngine.Job.Tests;

public sealed class ExternalFenceBridgeTests
{
    private const int ExpectedSignalMilliseconds = 1_000;
    private const int UnexpectedSignalMilliseconds = 50;

    public ExternalFenceBridgeTests()
    {
        JobSystem.ResetForTesting(workerCount: 2);
        ExternalFenceJobs.Reset();
    }

    [Fact]
    public void FakeFenceStartsIncompleteAndSignalsCallbacks()
    {
        var fence = new FakeExternalFence();
        var box = new CallbackBox();
        var callbackCount = 0;

        Assert.False(fence.IsSignaled);

        fence.OnSignaled(static state => Interlocked.Increment(ref ((CallbackBox)state!).Count), box);
        fence.OnSignaled(_ => callbackCount++, null);
        fence.Signal();
        fence.Signal();

        Assert.True(fence.IsSignaled);
        Assert.Equal(1, box.Count);
        Assert.Equal(1, callbackCount);
    }

    [Fact]
    public void ExternalFenceHandleCompletesAfterSignal()
    {
        var fence = new FakeExternalFence();
        var handle = JobSystem.CreateExternalFenceHandle(fence);

        Assert.False(handle.IsCompleted);

        fence.Signal();
        handle.Complete();

        Assert.True(handle.IsCompleted);
    }

    [Fact]
    public void DependentJobWaitsForFenceBackedHandle()
    {
        var fence = new FakeExternalFence();
        var fenceHandle = JobSystem.CreateExternalFenceHandle(fence);
        using var ran = new ManualResetEventSlim();
        var dependent = JobSystem.Schedule(new ExternalFenceJobs.SignalingRecordJob(1, ran), fenceHandle);

        AssertNotSignaled(ran);

        fence.Signal();
        dependent.Complete();

        Assert.Equal([1], ExternalFenceJobs.LogSnapshot());
    }

    [Fact]
    public void MultipleDependentsReleaseAfterFenceSignal()
    {
        var fence = new FakeExternalFence();
        var fenceHandle = JobSystem.CreateExternalFenceHandle(fence);
        using var firstRan = new ManualResetEventSlim();
        using var secondRan = new ManualResetEventSlim();
        var first = JobSystem.Schedule(new ExternalFenceJobs.SignalingRecordJob(1, firstRan), fenceHandle);
        var second = JobSystem.Schedule(new ExternalFenceJobs.SignalingRecordJob(2, secondRan), fenceHandle);

        AssertNotSignaled(firstRan);
        AssertNotSignaled(secondRan);

        fence.Signal();
        JobSystem.CombineDependencies([first, second]).Complete();

        Assert.Equal([1, 2], ExternalFenceJobs.LogSnapshot().Order().ToArray());
    }

    [Fact]
    public void AlreadySignaledFenceProducesCompletedHandle()
    {
        var fence = new FakeExternalFence();
        fence.Signal();

        var handle = JobSystem.CreateExternalFenceHandle(fence);

        Assert.True(handle.IsCompleted);
        handle.Complete();
    }

    [Fact]
    public void FenceHandleCombinesWithCpuHandle()
    {
        using var started = new ManualResetEventSlim();
        using var gate = new ManualResetEventSlim();
        var fence = new FakeExternalFence();
        var cpu = JobSystem.Schedule(new ExternalFenceJobs.BlockingRecordJob(1, started, gate));
        var fenceHandle = JobSystem.CreateExternalFenceHandle(fence);
        var combined = JobSystem.CombineDependencies([cpu, fenceHandle]);

        AssertSignaled(started);
        fence.Signal();
        Assert.False(combined.IsCompleted);

        gate.Set();
        combined.Complete();

        Assert.Equal([1], ExternalFenceJobs.LogSnapshot());
    }

    [Fact]
    public void ExternalObserverSeesIncompleteThenComplete()
    {
        using var started = new ManualResetEventSlim();
        using var gate = new ManualResetEventSlim();
        using var observed = new ManualResetEventSlim();
        var handle = JobSystem.Schedule(new ExternalFenceJobs.BlockingRecordJob(1, started, gate));

        JobSystem.OnCompleted(handle, static (_, state) => ((ManualResetEventSlim)state!).Set(), observed);

        AssertSignaled(started);
        AssertNotSignaled(observed);

        gate.Set();
        handle.Complete();

        AssertSignaled(observed);
    }

    [Fact]
    public void ExternalContinuationFiresOnceAndAlreadyCompleteFiresImmediately()
    {
        var handle = JobSystem.Schedule(new ExternalFenceJobs.RecordJob(1));
        var box = new CallbackBox();
        using var callbackObserved = new ManualResetEventSlim();
        var callbackCount = 0;

        JobSystem.OnCompleted(handle, static (_, state) => Interlocked.Increment(ref ((CallbackBox)state!).Count), box);
        JobSystem.OnCompleted(handle, (_, _) =>
        {
            callbackCount++;
            callbackObserved.Set();
        });
        handle.Complete();
        handle.Complete();
        AssertSignaled(callbackObserved);

        var afterCompleteCount = 0;
        JobSystem.OnCompleted(handle, (_, _) => afterCompleteCount++);

        Assert.Equal(1, box.Count);
        Assert.Equal(1, callbackCount);
        Assert.Equal(1, afterCompleteCount);
    }

    [Fact]
    public void ThrowingExternalContinuationDoesNotBlockCleanupOrOtherCallbacks()
    {
        JobSystem.ResetForTesting(new JobRuntimeConfig
        {
            WorkerCount = 2,
            MaxCompletionStates = 4,
            MaxQueuedWorkItems = 64
        });

        using var observed = new ManualResetEventSlim();
        var handle = JobSystem.Schedule(new ExternalFenceJobs.RecordJob(1));
        JobSystem.OnCompleted(handle, (_, _) => throw new InvalidOperationException("observer failed"));
        JobSystem.OnCompleted(handle, static (_, state) => ((ManualResetEventSlim)state!).Set(), observed);

        handle.Complete();

        AssertSignaled(observed);

        for (var i = 0; i < 16; i++)
        {
            JobSystem.Schedule(new ExternalFenceJobs.RecordJob(i)).Complete();
        }
    }

    [Fact]
    public void ExternalContinuationRunsForFaultedHandle()
    {
        using var observed = new ManualResetEventSlim();
        var handle = JobSystem.Schedule(new ExternalFenceJobs.ThrowingJob());

        JobSystem.OnCompleted(handle, static (_, state) => ((ManualResetEventSlim)state!).Set(), observed);

        var ex = Assert.Throws<InvalidOperationException>(() => handle.Complete());

        Assert.Equal("external observer fault", ex.Message);
        AssertSignaled(observed);
    }

    [Fact]
    public void ExternalContinuationStateIsReclaimedWithCompletionState()
    {
        JobSystem.ResetForTesting(new JobRuntimeConfig
        {
            WorkerCount = 2,
            MaxCompletionStates = 4,
            MaxQueuedWorkItems = 64
        });

        using var callbacksObserved = new ManualResetEventSlim();
        var callbackCount = 0;
        for (var i = 0; i < 16; i++)
        {
            var handle = JobSystem.Schedule(new ExternalFenceJobs.RecordJob(i));
            JobSystem.OnCompleted(handle, (_, _) =>
            {
                if (Interlocked.Increment(ref callbackCount) == 16)
                {
                    callbacksObserved.Set();
                }
            });
            handle.Complete();
        }

        AssertSignaled(callbacksObserved);
        Assert.Equal(16, Volatile.Read(ref callbackCount));
    }

    [Fact]
    public void ShellJobCpuHandleCompletesBeforeExternalFence()
    {
        var system = new FakeExternalSystem();

        JobSystem.Schedule(new ExternalFenceJobs.ManagedShellJob(system, 10)).Complete();

        var fence = system.LastFence;
        Assert.False(fence.IsSignaled);

        var fenceHandle = JobSystem.CreateExternalFenceHandle(fence);
        using var ran = new ManualResetEventSlim();
        var dependent = JobSystem.Schedule(new ExternalFenceJobs.SignalingRecordJob(11, ran), fenceHandle);
        AssertNotSignaled(ran);

        fence.Signal();
        dependent.Complete();

        Assert.Equal([10], system.SubmittedSnapshot());
        Assert.Contains(11, ExternalFenceJobs.LogSnapshot());
    }

    [Fact]
    public void RefFreeAndManagedShellJobsUseSameSchedulerApi()
    {
        var system = new FakeExternalSystem();
        var staticFence = ExternalFenceJobs.PrepareStaticFence(42);

        var refFree = JobSystem.Schedule(new ExternalFenceJobs.RefFreeShellJob(42, 1));
        var managed = JobSystem.Schedule(new ExternalFenceJobs.ManagedShellJob(system, 2));
        JobSystem.CombineDependencies([refFree, managed]).Complete();

        Assert.Equal(JobPayloadLane.RefFree, JobSystem.GetPayloadLane<ExternalFenceJobs.RefFreeShellJob>());
        Assert.Equal(JobPayloadLane.RefContaining, JobSystem.GetPayloadLane<ExternalFenceJobs.ManagedShellJob>());
        Assert.False(staticFence.IsSignaled);
        Assert.Equal([1], ExternalFenceJobs.LogSnapshot());
        Assert.Equal([2], system.SubmittedSnapshot());
    }

    [Fact]
    public void ExternalFenceAccessKeepsResourceInUseUntilSignal()
    {
        var resource = JobSystem.CreateResource("external-resource");
        var fence = new FakeExternalFence();
        var fenceHandle = JobSystem.CreateExternalFenceHandle(fence, JobResourceAccess.Write(resource));
        using var readerRan = new ManualResetEventSlim();
        var reader = JobSystem.Schedule(
            new ExternalFenceJobs.SignalingRecordJob(20, readerRan),
            JobResourceAccess.Read(resource));

        Assert.Throws<JobResourceSafetyException>(() => JobSystem.ReleaseResource(resource));
        AssertNotSignaled(readerRan);

        fence.Signal();
        JobSystem.CombineDependencies([fenceHandle, reader]).Complete();

        Assert.True(readerRan.IsSet);
        JobSystem.ReleaseResource(resource);
    }

    [Fact]
    public void ExternalFenceWritePendingReadThenWriteOrdersWriterAfterReader()
    {
        var resource = JobSystem.CreateResource("external-write-read-write");
        var fence = new FakeExternalFence();
        var fenceHandle = JobSystem.CreateExternalFenceHandle(fence, JobResourceAccess.Write(resource));
        using var readerStarted = new ManualResetEventSlim();
        using var readerGate = new ManualResetEventSlim();
        using var writerStarted = new ManualResetEventSlim();
        using var writerGate = new ManualResetEventSlim();

        var reader = JobSystem.Schedule(
            new ExternalFenceJobs.BlockingRecordJob(41, readerStarted, readerGate),
            JobResourceAccess.Read(resource));
        var writer = JobSystem.Schedule(
            new ExternalFenceJobs.BlockingRecordJob(42, writerStarted, writerGate),
            JobResourceAccess.Write(resource));

        AssertNotSignaled(readerStarted);
        AssertNotSignaled(writerStarted);

        fence.Signal();
        AssertSignaled(readerStarted);
        AssertNotSignaled(writerStarted);

        readerGate.Set();
        AssertSignaled(writerStarted);
        writerGate.Set();
        JobSystem.CombineDependencies([fenceHandle, reader, writer]).Complete();

        Assert.Equal([41, 42], ExternalFenceJobs.LogSnapshot());
        JobSystem.ReleaseResource(resource);
    }

    [Fact]
    public void AlreadySignaledResourceFenceReleasesAccessBeforeFactoryReturns()
    {
        var resource = JobSystem.CreateResource("already-signaled-resource");
        var fence = new FakeExternalFence();
        fence.Signal();

        var fenceHandle = JobSystem.CreateExternalFenceHandle(fence, JobResourceAccess.Write(resource));

        Assert.True(fenceHandle.IsCompleted);
        JobSystem.Schedule(new ExternalFenceJobs.NoOpJob(), JobResourceAccess.Read(resource)).Complete();
        JobSystem.ReleaseResource(resource);
    }

    [Fact]
    public void ExternalFenceSpanAccessWaitsForPriorResourceOwners()
    {
        var firstResource = JobSystem.CreateResource("prior-owner-a");
        var secondResource = JobSystem.CreateResource("prior-owner-b");
        using var firstStarted = new ManualResetEventSlim();
        using var secondStarted = new ManualResetEventSlim();
        using var firstGate = new ManualResetEventSlim();
        using var secondGate = new ManualResetEventSlim();
        var firstWriter = JobSystem.Schedule(
            new ExternalFenceJobs.BlockingRecordJob(51, firstStarted, firstGate),
            JobResourceAccess.Write(firstResource));
        var secondWriter = JobSystem.Schedule(
            new ExternalFenceJobs.BlockingRecordJob(52, secondStarted, secondGate),
            JobResourceAccess.Write(secondResource));
        var fence = new FakeExternalFence();

        AssertSignaled(firstStarted);
        AssertSignaled(secondStarted);

        ReadOnlySpan<JobResourceAccess> accesses =
        [
            JobResourceAccess.Read(firstResource),
            JobResourceAccess.Read(secondResource)
        ];
        var fenceHandle = JobSystem.CreateExternalFenceHandle(fence, accesses);

        fence.Signal();
        Assert.False(fenceHandle.IsCompleted);

        firstGate.Set();
        secondGate.Set();
        JobSystem.CombineDependencies([firstWriter, secondWriter, fenceHandle]).Complete();

        Assert.True(fenceHandle.IsCompleted);
        JobSystem.ReleaseResource(firstResource);
        JobSystem.ReleaseResource(secondResource);
    }

    [Fact]
    public void ScopeOwnedResourceCanExtendToExplicitExternalFence()
    {
        var fence = new FakeExternalFence();
        using var created = new ManualResetEventSlim();
        var parent = JobSystem.Schedule(
            new ExternalFenceJobs.ParentCreatesScopeResourceAndExternalFenceJob(fence, created));

        AssertSignaled(created);
        Assert.False(parent.IsCompleted);
        Assert.Throws<JobResourceSafetyException>(() =>
            JobSystem.ReleaseResource(ExternalFenceJobs.LastScopeResource));

        fence.Signal();
        parent.Complete();

        Assert.Throws<JobResourceSafetyException>(() =>
            JobSystem.Schedule(
                new ExternalFenceJobs.NoOpJob(),
                JobResourceAccess.Read(ExternalFenceJobs.LastScopeResource)));
    }

    [Fact]
    public void NoExplicitFenceDeclarationMeansCpuLifetimeOnly()
    {
        var resource = JobSystem.CreateResource("cpu-only-resource");
        var system = new FakeExternalSystem();

        JobSystem.Schedule(
            new ExternalFenceJobs.ManagedShellJob(system, 30),
            JobResourceAccess.Write(resource)).Complete();

        var externalFence = system.LastFence;
        using var readerRan = new ManualResetEventSlim();
        JobSystem.Schedule(
            new ExternalFenceJobs.SignalingRecordJob(31, readerRan),
            JobResourceAccess.Read(resource)).Complete();

        Assert.True(readerRan.IsSet);
        Assert.False(externalFence.IsSignaled);
        JobSystem.ReleaseResource(resource);
    }

    [Fact]
    public void CpuExternalCpuChainRunsInOrder()
    {
        using var started = new ManualResetEventSlim();
        using var gate = new ManualResetEventSlim();
        using var finalRan = new ManualResetEventSlim();
        var fence = new FakeExternalFence();
        var cpu = JobSystem.Schedule(new ExternalFenceJobs.BlockingRecordJob(1, started, gate));
        JobSystem.OnCompleted(cpu, static (_, state) => ((FakeExternalFence)state!).Signal(), fence);
        var fenceHandle = JobSystem.CreateExternalFenceHandle(fence);
        var final = JobSystem.Schedule(new ExternalFenceJobs.SignalingRecordJob(2, finalRan), fenceHandle);

        AssertSignaled(started);
        AssertNotSignaled(finalRan);

        gate.Set();
        final.Complete();
        cpu.Complete();

        Assert.Equal([1, 2], ExternalFenceJobs.LogSnapshot());
    }

    [Fact]
    public void ChildShellWithoutFenceHandleDoesNotDelayParentForExternalWork()
    {
        var system = new FakeExternalSystem();
        using var submitted = new ManualResetEventSlim();

        JobSystem.Schedule(
            new ExternalFenceJobs.ParentSchedulesShellChildWithoutFence(system, submitted)).Complete();

        Assert.True(submitted.IsSet);
        Assert.False(system.LastFence.IsSignaled);
    }

    [Fact]
    public void ChildCreatedFenceHandleDelaysParentOnlyWhenExplicitlyCreated()
    {
        var fence = new FakeExternalFence();
        using var created = new ManualResetEventSlim();
        var parent = JobSystem.Schedule(new ExternalFenceJobs.ParentSchedulesExplicitFenceChild(fence, created));

        AssertSignaled(created);
        Assert.False(parent.IsCompleted);

        fence.Signal();
        parent.Complete();
    }

    [Fact]
    public void FenceSignalAfterSchedulerShutdownStillRunsExternalObserver()
    {
        var fence = new FakeExternalFence();
        using var observed = new ManualResetEventSlim();
        var handle = JobSystem.CreateExternalFenceHandle(fence);
        JobSystem.OnCompleted(handle, static (_, state) => ((ManualResetEventSlim)state!).Set(), observed);

        JobSystem.ShutdownForTesting();
        AssertNotSignaled(observed);

        fence.Signal();

        AssertSignaled(observed);
    }

    private static void AssertSignaled(ManualResetEventSlim signal)
    {
        Assert.True(signal.Wait(ExpectedSignalMilliseconds));
    }

    private static void AssertNotSignaled(ManualResetEventSlim signal)
    {
        Assert.False(signal.Wait(UnexpectedSignalMilliseconds));
    }

    private sealed class CallbackBox
    {
        internal int Count;
    }

    private sealed class FakeExternalSystem
    {
        private readonly Lock _sync = new();
        private readonly List<int> _submitted = [];
        private FakeExternalFence? _lastFence;

        internal FakeExternalFence LastFence
        {
            get
            {
                lock (_sync)
                {
                    return _lastFence
                        ?? throw new InvalidOperationException("External work has not been submitted.");
                }
            }
        }

        internal FakeExternalFence Submit(int value)
        {
            var fence = new FakeExternalFence();
            lock (_sync)
            {
                _submitted.Add(value);
                _lastFence = fence;
            }

            return fence;
        }

        internal int[] SubmittedSnapshot()
        {
            lock (_sync)
            {
                return [.. _submitted];
            }
        }
    }

    private sealed class FakeExternalFence : IJobExternalFence
    {
        private readonly Lock _sync = new();
        private readonly List<(Action<object?> Continuation, object? State)> _callbacks = [];
        private bool _signaled;

        public bool IsSignaled
        {
            get
            {
                lock (_sync)
                {
                    return _signaled;
                }
            }
        }

        public void OnSignaled(Action<object?> continuation, object? state)
        {
            ArgumentNullException.ThrowIfNull(continuation);

            lock (_sync)
            {
                if (!_signaled)
                {
                    _callbacks.Add((continuation, state));
                    return;
                }
            }

            continuation(state);
        }

        internal void Signal()
        {
            (Action<object?> Continuation, object? State)[] callbacks;
            lock (_sync)
            {
                if (_signaled)
                {
                    return;
                }

                _signaled = true;
                callbacks = _callbacks.ToArray();
                _callbacks.Clear();
            }

            foreach (var callback in callbacks)
            {
                callback.Continuation(callback.State);
            }
        }
    }

    private static class ExternalFenceJobs
    {
        private static readonly Lock LogLock = new();
        private static readonly List<int> Log = [];
        private static readonly Dictionary<int, FakeExternalFence> StaticFences = [];
        internal static JobResource LastScopeResource;

        internal static void Reset()
        {
            lock (LogLock)
            {
                Log.Clear();
                StaticFences.Clear();
            }

            LastScopeResource = default;
        }

        internal static int[] LogSnapshot()
        {
            lock (LogLock)
            {
                return [.. Log];
            }
        }

        internal static FakeExternalFence PrepareStaticFence(int id)
        {
            lock (LogLock)
            {
                var fence = new FakeExternalFence();
                StaticFences[id] = fence;
                return fence;
            }
        }

        private static void SubmitStaticExternalWork(int fenceId, int value)
        {
            lock (LogLock)
            {
                if (!StaticFences.ContainsKey(fenceId))
                {
                    throw new InvalidOperationException($"Static external fence '{fenceId}' was not prepared.");
                }

                Log.Add(value);
            }
        }

        internal struct NoOpJob : IJob
        {
            public void Execute()
            {
            }
        }

        internal readonly struct RecordJob : IJob
        {
            private readonly int _value;

            internal RecordJob(int value)
            {
                _value = value;
            }

            public void Execute()
            {
                lock (LogLock)
                {
                    Log.Add(_value);
                }
            }
        }

        internal readonly struct ThrowingJob : IJob
        {
            public void Execute()
            {
                throw new InvalidOperationException("external observer fault");
            }
        }

        internal readonly struct SignalingRecordJob : IJob
        {
            private readonly int _value;
            private readonly ManualResetEventSlim _ran;

            internal SignalingRecordJob(int value, ManualResetEventSlim ran)
            {
                _value = value;
                _ran = ran;
            }

            public void Execute()
            {
                lock (LogLock)
                {
                    Log.Add(_value);
                }

                _ran.Set();
            }
        }

        internal readonly struct RefFreeShellJob : IJob
        {
            private readonly int _fenceId;
            private readonly int _value;

            internal RefFreeShellJob(int fenceId, int value)
            {
                _fenceId = fenceId;
                _value = value;
            }

            public void Execute()
            {
                SubmitStaticExternalWork(_fenceId, _value);
            }
        }

        internal readonly struct ManagedShellJob : IJob
        {
            private readonly FakeExternalSystem _system;
            private readonly int _value;

            internal ManagedShellJob(FakeExternalSystem system, int value)
            {
                _system = system;
                _value = value;
            }

            public void Execute()
            {
                _system.Submit(_value);
            }
        }

        internal readonly struct ParentCreatesScopeResourceAndExternalFenceJob : IJob
        {
            private readonly FakeExternalFence _fence;
            private readonly ManualResetEventSlim _created;

            internal ParentCreatesScopeResourceAndExternalFenceJob(
                FakeExternalFence fence,
                ManualResetEventSlim created)
            {
                _fence = fence;
                _created = created;
            }

            public void Execute()
            {
                var resource = JobSystem.CreateScopeResource("scope-external-resource");
                LastScopeResource = resource;
                JobSystem.CreateExternalFenceHandle(_fence, JobResourceAccess.Write(resource));
                _created.Set();
            }
        }

        internal readonly struct ParentSchedulesShellChildWithoutFence : IJob
        {
            private readonly FakeExternalSystem _system;
            private readonly ManualResetEventSlim _submitted;

            internal ParentSchedulesShellChildWithoutFence(
                FakeExternalSystem system,
                ManualResetEventSlim submitted)
            {
                _system = system;
                _submitted = submitted;
            }

            public void Execute()
            {
                JobSystem.Schedule(new ManagedShellJobWithSignal(_system, 40, _submitted));
            }
        }

        internal readonly struct ParentSchedulesExplicitFenceChild : IJob
        {
            private readonly FakeExternalFence _fence;
            private readonly ManualResetEventSlim _created;

            internal ParentSchedulesExplicitFenceChild(
                FakeExternalFence fence,
                ManualResetEventSlim created)
            {
                _fence = fence;
                _created = created;
            }

            public void Execute()
            {
                JobSystem.Schedule(new ChildCreatesFenceHandleJob(_fence, _created));
            }
        }

        private readonly struct ManagedShellJobWithSignal : IJob
        {
            private readonly FakeExternalSystem _system;
            private readonly int _value;
            private readonly ManualResetEventSlim _submitted;

            internal ManagedShellJobWithSignal(
                FakeExternalSystem system,
                int value,
                ManualResetEventSlim submitted)
            {
                _system = system;
                _value = value;
                _submitted = submitted;
            }

            public void Execute()
            {
                _system.Submit(_value);
                _submitted.Set();
            }
        }

        private readonly struct ChildCreatesFenceHandleJob : IJob
        {
            private readonly FakeExternalFence _fence;
            private readonly ManualResetEventSlim _created;

            internal ChildCreatesFenceHandleJob(
                FakeExternalFence fence,
                ManualResetEventSlim created)
            {
                _fence = fence;
                _created = created;
            }

            public void Execute()
            {
                JobSystem.CreateExternalFenceHandle(_fence);
                _created.Set();
            }
        }

        internal readonly struct BlockingRecordJob : IJob
        {
            private readonly int _value;
            private readonly ManualResetEventSlim _started;
            private readonly ManualResetEventSlim _gate;

            internal BlockingRecordJob(int value, ManualResetEventSlim started, ManualResetEventSlim gate)
            {
                _value = value;
                _started = started;
                _gate = gate;
            }

            public void Execute()
            {
                _started.Set();
                _gate.Wait();
                lock (LogLock)
                {
                    Log.Add(_value);
                }
            }
        }
    }
}
