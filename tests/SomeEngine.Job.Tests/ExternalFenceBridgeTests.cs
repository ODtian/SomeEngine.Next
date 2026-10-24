namespace SomeEngine.Job.Tests;

public sealed class ExternalFenceBridgeTests
{
    private readonly ExternalFenceData _data = new();

    private const int ExpectedSignalMilliseconds = 1_000;

    public ExternalFenceBridgeTests()
    {
        JobSystem.Initialize(new JobRuntimeConfig { WorkerCount = 2 });
        _data.Reset();
    }

    [Fact]
    public void FakeFenceStartsIncompleteAndSignalsCallbacks()
    {
        var fence = new FakeExternalFence(_data.Events);
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
        var fence = new FakeExternalFence(_data.Events);
        var handle = JobSystem.CreateExternalFenceHandle(fence);

        Assert.False(handle.IsCompleted);

        fence.Signal();
        handle.Complete();

        Assert.True(handle.IsCompleted);
    }

    [Fact]
    public void DependentJobWaitsForFenceBackedHandle()
    {
        var fence = new FakeExternalFence(_data.Events);
        var fenceHandle = JobSystem.CreateExternalFenceHandle(fence);
        using var ran = _data.Events.CreateSignal();
        var dependent = JobSystem.Schedule(new ExternalFenceData.SignalingRecordJob(_data, 1, ran), fenceHandle);

        fence.Signal();
        dependent.Complete();

        Assert.Equal([1], _data.LogSnapshot());
        _data.Events.AssertBefore(fence.SignalPoint, ran);
    }

    [Fact]
    public void MultipleDependentsReleaseAfterFenceSignal()
    {
        var fence = new FakeExternalFence(_data.Events);
        var fenceHandle = JobSystem.CreateExternalFenceHandle(fence);
        using var firstRan = _data.Events.CreateSignal();
        using var secondRan = _data.Events.CreateSignal();
        var first = JobSystem.Schedule(new ExternalFenceData.SignalingRecordJob(_data, 1, firstRan), fenceHandle);
        var second = JobSystem.Schedule(new ExternalFenceData.SignalingRecordJob(_data, 2, secondRan), fenceHandle);

        fence.Signal();
        JobSystem.CombineDependencies([first, second]).Complete();

        Assert.Equal([1, 2], _data.LogSnapshot().Order().ToArray());
        _data.Events.AssertBefore(fence.SignalPoint, firstRan);
        _data.Events.AssertBefore(fence.SignalPoint, secondRan);
    }

    [Fact]
    public void AlreadySignaledFenceProducesCompletedHandle()
    {
        var fence = new FakeExternalFence(_data.Events);
        fence.Signal();

        var handle = JobSystem.CreateExternalFenceHandle(fence);

        Assert.True(handle.IsCompleted);
        handle.Complete();
    }

    [Fact]
    public void FenceHandleCombinesWithCpuHandle()
    {
        using var started = _data.Events.CreateSignal();
        using var gate = _data.Events.CreateSignal();
        var fence = new FakeExternalFence(_data.Events);
        var cpu = JobSystem.Schedule(new ExternalFenceData.BlockingRecordJob(_data, 1, started, gate));
        var fenceHandle = JobSystem.CreateExternalFenceHandle(fence);
        var combined = JobSystem.CombineDependencies([cpu, fenceHandle]);

        AssertSignaled(started);
        fence.Signal();
        Assert.False(combined.IsCompleted);

        gate.Set();
        combined.Complete();

        Assert.Equal([1], _data.LogSnapshot());
    }

    [Fact]
    public void ExternalObserverSeesIncompleteThenComplete()
    {
        using var started = _data.Events.CreateSignal();
        using var gate = _data.Events.CreateSignal();
        using var observed = _data.Events.CreateSignal();
        var handle = JobSystem.Schedule(new ExternalFenceData.BlockingRecordJob(_data, 1, started, gate));

        JobSystem.OnCompleted(handle, static (_, state) => ((JobSignal)state!).Set(), observed);

        AssertSignaled(started);

        gate.Set();
        handle.Complete();

        AssertSignaled(observed);
        _data.Events.AssertBefore(gate, observed);
    }

    [Fact]
    public void ExternalContinuationFiresOnceAndAlreadyCompleteFiresImmediately()
    {
        var handle = JobSystem.Schedule(new ExternalFenceData.RecordJob(_data, 1));
        var box = new CallbackBox();
        using var callbackObserved = _data.Events.CreateSignal();
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
        JobSystem.Initialize(new JobRuntimeConfig
        {
            WorkerCount = 2,
            MaxCompletionStates = 4,
            MaxQueuedWorkItems = 64
        });

        using var observed = _data.Events.CreateSignal();
        var handle = JobSystem.Schedule(new ExternalFenceData.RecordJob(_data, 1));
        JobSystem.OnCompleted(handle, (_, _) => throw new InvalidOperationException("observer failed"));
        JobSystem.OnCompleted(handle, static (_, state) => ((JobSignal)state!).Set(), observed);

        handle.Complete();

        AssertSignaled(observed);

        for (var i = 0; i < 16; i++)
        {
            JobSystem.Schedule(new ExternalFenceData.RecordJob(_data, i)).Complete();
        }
    }

    [Fact]
    public void ExternalContinuationRunsForFaultedHandle()
    {
        using var observed = _data.Events.CreateSignal();
        var handle = JobSystem.Schedule(new ExternalFenceData.ThrowingJob());

        JobSystem.OnCompleted(handle, static (_, state) => ((JobSignal)state!).Set(), observed);

        var ex = Assert.Throws<InvalidOperationException>(() => handle.Complete());

        AssertSignaled(observed);
    }

    [Fact]
    public void ExternalContinuationStateIsReclaimedWithCompletionState()
    {
        JobSystem.Initialize(new JobRuntimeConfig
        {
            WorkerCount = 2,
            MaxCompletionStates = 4,
            MaxQueuedWorkItems = 64
        });

        using var callbacksObserved = _data.Events.CreateSignal();
        var callbackCount = 0;
        for (var i = 0; i < 16; i++)
        {
            var handle = JobSystem.Schedule(new ExternalFenceData.RecordJob(_data, i));
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
        var system = new FakeExternalSystem(_data.Events);

        JobSystem.Schedule(new ExternalFenceData.ManagedShellJob(system, 10)).Complete();

        var fence = system.LastFence;
        Assert.False(fence.IsSignaled);

        var fenceHandle = JobSystem.CreateExternalFenceHandle(fence);
        using var ran = _data.Events.CreateSignal();
        var dependent = JobSystem.Schedule(new ExternalFenceData.SignalingRecordJob(_data, 11, ran), fenceHandle);

        fence.Signal();
        dependent.Complete();

        Assert.Equal([10], system.SubmittedSnapshot());
        Assert.Contains(11, _data.LogSnapshot());
        _data.Events.AssertBefore(system.LastFence.SignalPoint, ran);
    }

    [Fact]
    public void IndexedAndManagedShellJobsUseSameSchedulerApi()
    {
        var system = new FakeExternalSystem(_data.Events);
        var indexedFence = _data.CreateFence(42);

        var indexed = JobSystem.Schedule(new ExternalFenceData.IndexedShellJob(_data, 42, 1));
        var managed = JobSystem.Schedule(new ExternalFenceData.ManagedShellJob(system, 2));
        JobSystem.CombineDependencies([indexed, managed]).Complete();

        Assert.False(indexedFence.IsSignaled);
        Assert.Equal([1], _data.LogSnapshot());
        Assert.Equal([2], system.SubmittedSnapshot());
    }

    [Fact]
    public void ExternalFenceAccessKeepsResourceInUseUntilSignal()
    {
        var resource = JobSystem.CreateResource("external-resource");
        var fence = new FakeExternalFence(_data.Events);
        var fenceHandle = JobSystem.CreateExternalFenceHandle(fence, JobResourceAccess.Write(resource));
        using var readerRan = _data.Events.CreateSignal();
        var reader = JobSystem.Schedule(
            new ExternalFenceData.SignalingRecordJob(_data, 20, readerRan),
            JobResourceAccess.Read(resource));

        Assert.Throws<JobResourceSafetyException>(() => JobSystem.ReleaseResource(resource));

        fence.Signal();
        JobSystem.CombineDependencies([fenceHandle, reader]).Complete();

        Assert.True(readerRan.IsSet);
        JobSystem.ReleaseResource(resource);
        _data.Events.AssertBefore(fence.SignalPoint, readerRan);
    }

    [Fact]
    public void ExternalFenceWritePendingReadThenWriteOrdersWriterAfterReader()
    {
        var resource = JobSystem.CreateResource("external-write-read-write");
        var fence = new FakeExternalFence(_data.Events);
        var fenceHandle = JobSystem.CreateExternalFenceHandle(fence, JobResourceAccess.Write(resource));
        using var readerStarted = _data.Events.CreateSignal();
        using var readerGate = _data.Events.CreateSignal();
        using var writerStarted = _data.Events.CreateSignal();
        using var writerGate = _data.Events.CreateSignal();

        var reader = JobSystem.Schedule(
            new ExternalFenceData.BlockingRecordJob(_data, 41, readerStarted, readerGate),
            JobResourceAccess.Read(resource));
        var writer = JobSystem.Schedule(
            new ExternalFenceData.BlockingRecordJob(_data, 42, writerStarted, writerGate),
            JobResourceAccess.Write(resource));

        fence.Signal();
        AssertSignaled(readerStarted);

        readerGate.Set();
        AssertSignaled(writerStarted);
        writerGate.Set();
        JobSystem.CombineDependencies([fenceHandle, reader, writer]).Complete();

        Assert.Equal([41, 42], _data.LogSnapshot());
        JobSystem.ReleaseResource(resource);
        _data.Events.AssertBefore(fence.SignalPoint, readerStarted);
        _data.Events.AssertBefore(readerGate, writerStarted);
    }

    [Fact]
    public void AlreadySignaledResourceFenceReleasesAccessBeforeFactoryReturns()
    {
        var resource = JobSystem.CreateResource("already-signaled-resource");
        var fence = new FakeExternalFence(_data.Events);
        fence.Signal();

        var fenceHandle = JobSystem.CreateExternalFenceHandle(fence, JobResourceAccess.Write(resource));

        Assert.True(fenceHandle.IsCompleted);
        JobSystem.Schedule(new ExternalFenceData.NoOpJob(), JobResourceAccess.Read(resource)).Complete();
        JobSystem.ReleaseResource(resource);
    }

    [Fact]
    public void ExternalFenceSpanAccessWaitsForPriorResourceOwners()
    {
        var firstResource = JobSystem.CreateResource("prior-owner-a");
        var secondResource = JobSystem.CreateResource("prior-owner-b");
        using var firstStarted = _data.Events.CreateSignal();
        using var secondStarted = _data.Events.CreateSignal();
        using var firstGate = _data.Events.CreateSignal();
        using var secondGate = _data.Events.CreateSignal();
        var firstWriter = JobSystem.Schedule(
            new ExternalFenceData.BlockingRecordJob(_data, 51, firstStarted, firstGate),
            JobResourceAccess.Write(firstResource));
        var secondWriter = JobSystem.Schedule(
            new ExternalFenceData.BlockingRecordJob(_data, 52, secondStarted, secondGate),
            JobResourceAccess.Write(secondResource));
        var fence = new FakeExternalFence(_data.Events);

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
        var fence = new FakeExternalFence(_data.Events);
        using var created = _data.Events.CreateSignal();
        var parent = JobSystem.Schedule(
            new ExternalFenceData.ParentCreatesScopeResourceAndExternalFenceJob(_data, fence, created));

        AssertSignaled(created);
        Assert.False(parent.IsCompleted);
        Assert.Throws<JobResourceSafetyException>(() =>
            JobSystem.ReleaseResource(_data.LastScopeResource));

        fence.Signal();
        parent.Complete();

        Assert.Throws<JobResourceSafetyException>(() =>
            JobSystem.Schedule(
                new ExternalFenceData.NoOpJob(),
                JobResourceAccess.Read(_data.LastScopeResource)));
    }

    [Fact]
    public void NoExplicitFenceDeclarationMeansCpuLifetimeOnly()
    {
        var resource = JobSystem.CreateResource("cpu-only-resource");
        var system = new FakeExternalSystem(_data.Events);

        JobSystem.Schedule(
            new ExternalFenceData.ManagedShellJob(system, 30),
            JobResourceAccess.Write(resource)).Complete();

        var externalFence = system.LastFence;
        using var readerRan = _data.Events.CreateSignal();
        JobSystem.Schedule(
            new ExternalFenceData.SignalingRecordJob(_data, 31, readerRan),
            JobResourceAccess.Read(resource)).Complete();

        Assert.True(readerRan.IsSet);
        Assert.False(externalFence.IsSignaled);
        JobSystem.ReleaseResource(resource);
    }

    [Fact]
    public void CpuExternalCpuChainRunsInOrder()
    {
        using var started = _data.Events.CreateSignal();
        using var gate = _data.Events.CreateSignal();
        using var finalRan = _data.Events.CreateSignal();
        var fence = new FakeExternalFence(_data.Events);
        var cpu = JobSystem.Schedule(new ExternalFenceData.BlockingRecordJob(_data, 1, started, gate));
        JobSystem.OnCompleted(cpu, static (_, state) => ((FakeExternalFence)state!).Signal(), fence);
        var fenceHandle = JobSystem.CreateExternalFenceHandle(fence);
        var final = JobSystem.Schedule(new ExternalFenceData.SignalingRecordJob(_data, 2, finalRan), fenceHandle);

        AssertSignaled(started);

        gate.Set();
        final.Complete();
        cpu.Complete();

        Assert.Equal([1, 2], _data.LogSnapshot());
        _data.Events.AssertBefore(fence.SignalPoint, finalRan);
    }

    [Fact]
    public void ChildShellWithoutFenceHandleDoesNotDelayParentForExternalWork()
    {
        var system = new FakeExternalSystem(_data.Events);
        using var submitted = _data.Events.CreateSignal();

        JobSystem.Schedule(
            new ExternalFenceData.ParentSchedulesShellChildWithoutFence(system, submitted)).Complete();

        Assert.True(submitted.IsSet);
        Assert.False(system.LastFence.IsSignaled);
    }

    [Fact]
    public void ChildCreatedFenceHandleDelaysParentOnlyWhenExplicitlyCreated()
    {
        var fence = new FakeExternalFence(_data.Events);
        using var created = _data.Events.CreateSignal();
        var parent = JobSystem.Schedule(new ExternalFenceData.ParentSchedulesExplicitFenceChild(fence, created));

        AssertSignaled(created);
        Assert.False(parent.IsCompleted);

        fence.Signal();
        parent.Complete();
    }

    [Fact]
    public async Task ShutdownWaitsForExplicitExternalWorkAndDeliversItsObservers()
    {
        var fence = new FakeExternalFence(_data.Events);
        using var observed = _data.Events.CreateSignal();
        JobHandle handle = JobSystem.CreateExternalFenceHandle(fence);
        JobSystem.OnCompleted(handle, static (_, state) => ((JobSignal)state!).Set(), observed);
        Task closing = Task.Run(JobSystem.Shutdown);
        try
        {
            Assert.True(SpinWait.SpinUntil(() =>
            {
                try { _ = JobSystem.Default; return false; }
                catch (InvalidOperationException) { return true; }
            }, TimeSpan.FromSeconds(5)));
            Assert.False(closing.IsCompleted);
            Assert.False(observed.IsSet);
        }
        finally
        {
            fence.Signal();
            await closing.WaitAsync(TimeSpan.FromSeconds(5));
        }
        Assert.True(handle.IsCompleted);
        Assert.True(observed.IsSet);
    }

    private void AssertSignaled(JobSignal signal)
    {
        Assert.True(signal.Wait(ExpectedSignalMilliseconds));
    }

    private sealed class CallbackBox
    {
        internal int Count;
    }

    private sealed class FakeExternalSystem(ExecutionEvents events)
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
            var fence = new FakeExternalFence(events);
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

    private sealed class FakeExternalFence(ExecutionEvents events) : IJobExternalFence
    {
        internal JobSignal SignalPoint { get; } = events.CreateSignal();
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

                SignalPoint.Set();
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

    private sealed class ExternalFenceData
    {
        internal readonly ExecutionEvents Events = new();
        private readonly Lock LogLock = new();
        private readonly List<int> Log = [];
        private readonly Dictionary<int, FakeExternalFence> Fences = [];
        internal JobResource LastScopeResource;

        internal void Reset()
        {
            lock (LogLock)
            {
                Log.Clear();
                Fences.Clear();
            }

            LastScopeResource = default;
        }

        internal int[] LogSnapshot()
        {
            lock (LogLock)
            {
                return [.. Log];
            }
        }

        internal FakeExternalFence CreateFence(int id)
        {
            lock (LogLock)
            {
                var fence = new FakeExternalFence(Events);
                Fences[id] = fence;
                return fence;
            }
        }

        private void RecordSubmission(int fenceId, int value)
        {
            lock (LogLock)
            {
                if (!Fences.ContainsKey(fenceId))
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
            private readonly ExternalFenceData _data;

            private readonly int _value;

            internal RecordJob(ExternalFenceData data, int value)
            {
                _data = data;
                _value = value;
            }

            public void Execute()
            {
                lock (_data.LogLock)
                {
                    _data.Log.Add(_value);
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
            private readonly ExternalFenceData _data;

            private readonly int _value;
            private readonly JobSignal _ran;

            internal SignalingRecordJob(ExternalFenceData data, int value, JobSignal ran)
            {
                _data = data;
                _value = value;
                _ran = ran;
            }

            public void Execute()
            {
                lock (_data.LogLock)
                {
                    _data.Log.Add(_value);
                }

                _ran.Set();
            }
        }

        internal readonly struct IndexedShellJob : IJob
        {
            private readonly ExternalFenceData _data;

            private readonly int _fenceId;
            private readonly int _value;

            internal IndexedShellJob(ExternalFenceData data, int fenceId, int value)
            {
                _data = data;
                _fenceId = fenceId;
                _value = value;
            }

            public void Execute()
            {
                _data.RecordSubmission(_fenceId, _value);
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
            private readonly ExternalFenceData _data;

            private readonly FakeExternalFence _fence;
            private readonly JobSignal _created;

            internal ParentCreatesScopeResourceAndExternalFenceJob(
                ExternalFenceData data, FakeExternalFence fence,
                JobSignal created)
            {
                _data = data;
                _fence = fence;
                _created = created;
            }

            public void Execute()
            {
                var resource = JobSystem.CreateScopeResource("scope-external-resource");
                _data.LastScopeResource = resource;
                JobSystem.CreateExternalFenceHandle(_fence, JobResourceAccess.Write(resource));
                _created.Set();
            }
        }

        internal readonly struct ParentSchedulesShellChildWithoutFence : IJob
        {
            private readonly FakeExternalSystem _system;
            private readonly JobSignal _submitted;

            internal ParentSchedulesShellChildWithoutFence(
                FakeExternalSystem system,
                JobSignal submitted)
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
            private readonly JobSignal _created;

            internal ParentSchedulesExplicitFenceChild(
                FakeExternalFence fence,
                JobSignal created)
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
            private readonly JobSignal _submitted;

            internal ManagedShellJobWithSignal(
                FakeExternalSystem system,
                int value,
                JobSignal submitted)
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
            private readonly JobSignal _created;

            internal ChildCreatesFenceHandleJob(
                FakeExternalFence fence,
                JobSignal created)
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
            private readonly ExternalFenceData _data;

            private readonly int _value;
            private readonly JobSignal _started;
            private readonly JobSignal _gate;

            internal BlockingRecordJob(ExternalFenceData data, int value, JobSignal started, JobSignal gate)
            {
                _data = data;
                _value = value;
                _started = started;
                _gate = gate;
            }

            public void Execute()
            {
                _started.Set();
                _gate.Wait();
                lock (_data.LogLock)
                {
                    _data.Log.Add(_value);
                }
            }
        }
    }
}
