namespace SomeEngine.Job.Tests;

public sealed class ExternalFenceBridgeTests
{
    private readonly ExternalFenceData _data = new();

    private const int ExpectedSignalMilliseconds = 1_000;

    public ExternalFenceBridgeTests()
    {
        JobSystem.Initialize(new JobConfiguration { WorkerCount = 2 });
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
        var handle = JobSystem.Current.CreateExternalFenceHandle(fence);

        Assert.False(handle.IsCompleted);

        fence.Signal();
        handle.Complete();

        Assert.True(handle.IsCompleted);
    }

    [Fact]
    public void DependentJobWaitsForFenceBackedHandle()
    {
        var fence = new FakeExternalFence(_data.Events);
        var fenceHandle = JobSystem.Current.CreateExternalFenceHandle(fence);
        using var ran = _data.Events.CreateSignal();
        var dependent = JobSystem.Current.Schedule(new ExternalFenceData.SignalingRecordJob(_data, 1, ran), dependency: fenceHandle);

        fence.Signal();
        dependent.Complete();

        Assert.Equal([1], _data.LogSnapshot());
        _data.Events.AssertBefore(fence.SignalPoint, ran);
    }

    [Fact]
    public void MultipleDependentsReleaseAfterFenceSignal()
    {
        var fence = new FakeExternalFence(_data.Events);
        var fenceHandle = JobSystem.Current.CreateExternalFenceHandle(fence);
        using var firstRan = _data.Events.CreateSignal();
        using var secondRan = _data.Events.CreateSignal();
        var first = JobSystem.Current.Schedule(new ExternalFenceData.SignalingRecordJob(_data, 1, firstRan), dependency: fenceHandle);
        var second = JobSystem.Current.Schedule(new ExternalFenceData.SignalingRecordJob(_data, 2, secondRan), dependency: fenceHandle);

        fence.Signal();
        JobSystem.Current.CombineDependencies([first, second]).Complete();

        Assert.Equal([1, 2], _data.LogSnapshot().Order().ToArray());
        _data.Events.AssertBefore(fence.SignalPoint, firstRan);
        _data.Events.AssertBefore(fence.SignalPoint, secondRan);
    }

    [Fact]
    public void AlreadySignaledFenceProducesCompletedHandle()
    {
        var fence = new FakeExternalFence(_data.Events);
        fence.Signal();

        var handle = JobSystem.Current.CreateExternalFenceHandle(fence);

        Assert.True(handle.IsCompleted);
        handle.Complete();
    }

    [Fact]
    public void FenceHandleCombinesWithCpuHandle()
    {
        using var started = _data.Events.CreateSignal();
        using var gate = _data.Events.CreateSignal();
        var fence = new FakeExternalFence(_data.Events);
        var cpu = JobSystem.Current.Schedule(new ExternalFenceData.BlockingRecordJob(_data, 1, started, gate));
        var fenceHandle = JobSystem.Current.CreateExternalFenceHandle(fence);
        var combined = JobSystem.Current.CombineDependencies([cpu, fenceHandle]);

        AssertSignaled(started);
        fence.Signal();
        Assert.False(combined.IsCompleted);

        gate.Set();
        combined.Complete();

        Assert.Equal([1], _data.LogSnapshot());
    }

    [Fact]
    public void ShellJobCpuHandleCompletesBeforeExternalFence()
    {
        var system = new FakeExternalSystem(_data.Events);

        JobSystem.Current.Schedule(new ExternalFenceData.ManagedShellJob(system, 10)).Complete();

        var fence = system.LastFence;
        Assert.False(fence.IsSignaled);

        var fenceHandle = JobSystem.Current.CreateExternalFenceHandle(fence);
        using var ran = _data.Events.CreateSignal();
        var dependent = JobSystem.Current.Schedule(new ExternalFenceData.SignalingRecordJob(_data, 11, ran), dependency: fenceHandle);

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

        var indexed = JobSystem.Current.Schedule(new ExternalFenceData.IndexedShellJob(_data, 42, 1));
        var managed = JobSystem.Current.Schedule(new ExternalFenceData.ManagedShellJob(system, 2));
        JobSystem.Current.CombineDependencies([indexed, managed]).Complete();

        Assert.False(indexedFence.IsSignaled);
        Assert.Equal([1], _data.LogSnapshot());
        Assert.Equal([2], system.SubmittedSnapshot());
    }

    [Fact]
    public void ExternalFenceAccessKeepsResourceInUseUntilSignal()
    {
        var resource = JobSystem.Current.CreateResource("external-resource");
        var fence = new FakeExternalFence(_data.Events);
        var fenceHandle = JobSystem.Current.CreateExternalFenceHandle(fence, [JobResourceAccess.Write(resource)]);
        using var readerRan = _data.Events.CreateSignal();
        var reader = JobSystem.Current.Schedule(
            new ExternalFenceData.SignalingRecordJob(_data, 20, readerRan),
            [JobResourceAccess.Read(resource)]);

        Assert.Throws<JobResourceSafetyException>(() => JobSystem.Current.ReleaseResource(resource));

        fence.Signal();
        JobSystem.Current.CombineDependencies([fenceHandle, reader]).Complete();

        Assert.True(readerRan.IsSet);
        JobSystem.Current.ReleaseResource(resource);
        _data.Events.AssertBefore(fence.SignalPoint, readerRan);
    }

    [Fact]
    public void ExternalFenceWritePendingReadThenWriteOrdersWriterAfterReader()
    {
        var resource = JobSystem.Current.CreateResource("external-write-read-write");
        var fence = new FakeExternalFence(_data.Events);
        var fenceHandle = JobSystem.Current.CreateExternalFenceHandle(fence, [JobResourceAccess.Write(resource)]);
        using var readerStarted = _data.Events.CreateSignal();
        using var readerGate = _data.Events.CreateSignal();
        using var writerStarted = _data.Events.CreateSignal();
        using var writerGate = _data.Events.CreateSignal();

        var reader = JobSystem.Current.Schedule(
            new ExternalFenceData.BlockingRecordJob(_data, 41, readerStarted, readerGate),
            [JobResourceAccess.Read(resource)]);
        var writer = JobSystem.Current.Schedule(
            new ExternalFenceData.BlockingRecordJob(_data, 42, writerStarted, writerGate),
            [JobResourceAccess.Write(resource)]);

        fence.Signal();
        AssertSignaled(readerStarted);

        readerGate.Set();
        AssertSignaled(writerStarted);
        writerGate.Set();
        JobSystem.Current.CombineDependencies([fenceHandle, reader, writer]).Complete();

        Assert.Equal([41, 42], _data.LogSnapshot());
        JobSystem.Current.ReleaseResource(resource);
        _data.Events.AssertBefore(fence.SignalPoint, readerStarted);
        _data.Events.AssertBefore(readerGate, writerStarted);
    }

    [Fact]
    public void AlreadySignaledResourceFenceReleasesAccessBeforeFactoryReturns()
    {
        var resource = JobSystem.Current.CreateResource("already-signaled-resource");
        var fence = new FakeExternalFence(_data.Events);
        fence.Signal();

        var fenceHandle = JobSystem.Current.CreateExternalFenceHandle(fence, [JobResourceAccess.Write(resource)]);

        Assert.True(fenceHandle.IsCompleted);
        JobSystem.Current.Schedule(new ExternalFenceData.NoOpJob(), [JobResourceAccess.Read(resource)]).Complete();
        JobSystem.Current.ReleaseResource(resource);
    }

    [Fact]
    public void ExternalFenceSpanAccessWaitsForPriorResourceOwners()
    {
        var firstResource = JobSystem.Current.CreateResource("prior-owner-a");
        var secondResource = JobSystem.Current.CreateResource("prior-owner-b");
        using var firstStarted = _data.Events.CreateSignal();
        using var secondStarted = _data.Events.CreateSignal();
        using var firstGate = _data.Events.CreateSignal();
        using var secondGate = _data.Events.CreateSignal();
        var firstWriter = JobSystem.Current.Schedule(
            new ExternalFenceData.BlockingRecordJob(_data, 51, firstStarted, firstGate),
            [JobResourceAccess.Write(firstResource)]);
        var secondWriter = JobSystem.Current.Schedule(
            new ExternalFenceData.BlockingRecordJob(_data, 52, secondStarted, secondGate),
            [JobResourceAccess.Write(secondResource)]);
        var fence = new FakeExternalFence(_data.Events);

        AssertSignaled(firstStarted);
        AssertSignaled(secondStarted);

        ReadOnlySpan<JobResourceAccess> accesses =
        [
            JobResourceAccess.Read(firstResource),
            JobResourceAccess.Read(secondResource)
        ];
        var fenceHandle = JobSystem.Current.CreateExternalFenceHandle(fence, accesses);

        fence.Signal();
        Assert.False(fenceHandle.IsCompleted);

        firstGate.Set();
        secondGate.Set();
        JobSystem.Current.CombineDependencies([firstWriter, secondWriter, fenceHandle]).Complete();

        Assert.True(fenceHandle.IsCompleted);
        JobSystem.Current.ReleaseResource(firstResource);
        JobSystem.Current.ReleaseResource(secondResource);
    }

    [Fact]
    public void NoExplicitFenceDeclarationMeansCpuLifetimeOnly()
    {
        var resource = JobSystem.Current.CreateResource("cpu-only-resource");
        var system = new FakeExternalSystem(_data.Events);

        JobSystem.Current.Schedule(
            new ExternalFenceData.ManagedShellJob(system, 30),
            [JobResourceAccess.Write(resource)]).Complete();

        var externalFence = system.LastFence;
        using var readerRan = _data.Events.CreateSignal();
        JobSystem.Current.Schedule(
            new ExternalFenceData.SignalingRecordJob(_data, 31, readerRan),
            [JobResourceAccess.Read(resource)]).Complete();

        Assert.True(readerRan.IsSet);
        Assert.False(externalFence.IsSignaled);
        JobSystem.Current.ReleaseResource(resource);
    }

    [Fact]
    public void ChildShellWithoutFenceHandleDoesNotDelayParentForExternalWork()
    {
        var system = new FakeExternalSystem(_data.Events);
        using var submitted = _data.Events.CreateSignal();

        JobSystem.Current.Schedule(
            new ExternalFenceData.ParentSchedulesShellChildWithoutFence(system, submitted)).Complete();

        Assert.True(submitted.IsSet);
        Assert.False(system.LastFence.IsSignaled);
    }

    [Fact]
    public void ChildCreatedFenceHandleDelaysParentOnlyWhenExplicitlyCreated()
    {
        var fence = new FakeExternalFence(_data.Events);
        using var created = _data.Events.CreateSignal();
        var parent = JobSystem.Current.Schedule(new ExternalFenceData.ParentSchedulesExplicitFenceChild(fence, created));

        AssertSignaled(created);
        Assert.False(parent.IsCompleted);

        fence.Signal();
        parent.Complete();
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
                JobSystem.Current.Schedule(new ManagedShellJobWithSignal(_system, 40, _submitted));
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
                JobSystem.Current.Schedule(new ChildCreatesFenceHandleJob(_fence, _created));
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
                JobSystem.Current.CreateExternalFenceHandle(_fence);
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
