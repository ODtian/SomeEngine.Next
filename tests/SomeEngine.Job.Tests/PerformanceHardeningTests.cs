using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace SomeEngine.Job.Tests;

public sealed class PerformanceHardeningTests
{
    private readonly HardeningData _data = new();

    public PerformanceHardeningTests()
    {
        JobSystem.Initialize(new JobRuntimeConfig { WorkerCount = 2 });
        _data.Reset();
    }

    [Fact]
    public void DefaultAndCustomConfigInitializeRuntime()
    {
        JobSystem.Initialize(JobRuntimeConfig.Default);
        JobSystem.Schedule(new HardeningData.IncrementJob(_data)).Complete();

        JobSystem.Initialize(new JobRuntimeConfig
        {
            WorkerCount = 0,
            MaxQueuedWorkItems = 4,
            MaxCompletionStates = 4,
            MaxResourceStates = 4,
            SafetyMode = JobSafetyMode.Strict
        });

        JobSystem.Schedule(new HardeningData.IncrementJob(_data)).Complete();

        Assert.Equal(2, _data.Counter);
        Assert.Equal(JobSafetyMode.Strict, JobSystem.SafetyMode);
    }

    [Fact]
    public void InvalidConfigIsRejectedBeforeReplacingRuntime()
    {
        JobSystem.Schedule(new HardeningData.IncrementJob(_data)).Complete();

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            JobSystem.Initialize(new JobRuntimeConfig { WorkerCount = -1 }));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            JobSystem.Initialize(new JobRuntimeConfig { WorkerSpinCount = -1 }));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            JobSystem.Initialize(new JobRuntimeConfig
            {
                WorkerSpinCount = 128,
                BusyWorkerSpinCount = 64
            }));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            JobSystem.Initialize(new JobRuntimeConfig { AutoBatchTargetMicroseconds = 0 }));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            JobSystem.Initialize(new JobRuntimeConfig { AutoBatchMaxTilesPerWorker = 0 }));

        JobSystem.Schedule(new HardeningData.IncrementJob(_data)).Complete();
        Assert.Equal(2, _data.Counter);
    }

    [Fact]
    public void QueueAndResourceCapacityExhaustionAreExplicit()
    {
        JobSystem.Initialize(new JobRuntimeConfig
        {
            WorkerCount = 0,
            MaxQueuedWorkItems = 1,
            MaxCompletionStates = 8,
            MaxResourceStates = 1
        });

        var queued = JobSystem.Schedule(new HardeningData.IncrementJob(_data));

        var queueExhausted = Assert.Throws<InvalidOperationException>(() =>
            JobSystem.Schedule(new HardeningData.IncrementJob(_data)));

        queued.Complete();

        _ = JobSystem.CreateResource("only-resource");
        var resourceExhausted = Assert.Throws<InvalidOperationException>(() =>
            JobSystem.CreateResource("too-many-resources"));
    }

    [Fact]
    public void CompletionStateCapacityExhaustionIsExplicit()
    {
        JobSystem.Initialize(new JobRuntimeConfig
        {
            WorkerCount = 0,
            MaxQueuedWorkItems = 8,
            MaxCompletionStates = 1,
            MaxResourceStates = 4
        });

        var queued = JobSystem.Schedule(new HardeningData.IncrementJob(_data));

        var exhausted = Assert.Throws<InvalidOperationException>(() =>
            JobSystem.Schedule(new HardeningData.IncrementJob(_data)));

        queued.Complete();
    }

    [Fact]
    public void DependencyQueueCapacityFailureFaultsDependentHandleInsteadOfHanging()
    {
        JobSystem.Initialize(new JobRuntimeConfig
        {
            WorkerCount = 0,
            MaxQueuedWorkItems = 1,
            MaxCompletionStates = 4,
            MaxResourceStates = 4
        });

        var dependency = JobSystem.Schedule(new HardeningData.IncrementJob(_data));
        var dependent = JobSystem.Schedule(new HardeningData.IncrementJob(_data), dependency);

        dependency.Complete();

        Assert.True(dependent.IsCompleted);
        var ex = Assert.Throws<InvalidOperationException>(() => dependent.Complete());

        Assert.Equal(1, _data.Counter);
    }

    [Fact]
    public void ObservedFaultedHandlesDoNotExhaustCompletionStatePool()
    {
        JobSystem.Initialize(new JobRuntimeConfig
        {
            WorkerCount = 0,
            MaxQueuedWorkItems = 4,
            MaxCompletionStates = 1,
            MaxResourceStates = 4
        });

        for (var i = 0; i < 8; i++)
        {
            var handle = JobSystem.Schedule(new HardeningData.ThrowingJob());
            Assert.Throws<InvalidOperationException>(() => handle.Complete());
        }
    }

    [Fact]
    public void UnobservedFaultedHandleRetainsFaultUnderCompletionStatePressure()
    {
        JobSystem.Initialize(new JobRuntimeConfig
        {
            WorkerCount = 1,
            MaxQueuedWorkItems = 4,
            MaxCompletionStates = 1,
            MaxResourceStates = 4
        });

        var faulted = JobSystem.Schedule(new HardeningData.ThrowingJob());

        Assert.True(SpinWait.SpinUntil(() => faulted.IsCompleted, TimeSpan.FromSeconds(2)));
        Assert.Throws<InvalidOperationException>(() =>
            JobSystem.Schedule(new HardeningData.IncrementJob(_data)));

        Assert.Throws<InvalidOperationException>(() => faulted.Complete());
    }

    [Fact]
    public void IndexedRangeFrontierRetainsSingleOverlapDependency()
    {
        JobSystem.Initialize(new JobRuntimeConfig { WorkerCount = 2 });
        var resource = JobSystem.CreateResource("indexed-range-overlap");
        const int rangeCount = 256;
        using var firstStarted = _data.Events.CreateSignal();
        using var firstGate = _data.Events.CreateSignal();
        using var secondStarted = _data.Events.CreateSignal();
        JobResourceAccess[] firstAccesses = CreateInterleavedReadAccesses(
            resource,
            rangeCount,
            offset: 0);
        JobResourceAccess[] secondAccesses = CreateInterleavedWriteAccesses(
            resource,
            rangeCount,
            offset: 2);
        secondAccesses[rangeCount / 2] = JobResourceAccess.Write(
            resource,
            start: (rangeCount / 2) * 4L,
            length: 1);

        var first = JobSystem.Schedule(
            new HardeningData.BlockingIncrementJob(_data, firstStarted, firstGate),
            firstAccesses);
        Assert.True(firstStarted.Wait(1_000));
        var second = JobSystem.Schedule(
            new HardeningData.SignalingIncrementJob(_data, secondStarted),
            secondAccesses);

        try
        {
            firstGate.Set();
            Assert.True(secondStarted.Wait(1_000));
            JobSystem.CombineDependencies([first, second]).Complete();
        }
        finally
        {
            firstGate.Set();
        }

        Assert.Equal(2, _data.Counter);
        _data.Events.AssertBefore(firstGate, secondStarted);
    }

    [Fact]
    public void DeferredIndexedRangeActivationRetainsSingleOverlapDependency()
    {
        JobSystem.Initialize(new JobRuntimeConfig { WorkerCount = 2 });
        var resource = JobSystem.CreateResource("deferred-indexed-range-overlap");
        const int rangeCount = 256;
        var fence = new ControlledFence();
        JobHandle dependency = JobSystem.CreateExternalFenceHandle(fence);
        using var activeStarted = _data.Events.CreateSignal();
        using var activeGate = _data.Events.CreateSignal();
        using var deferredStarted = _data.Events.CreateSignal();
        JobResourceAccess[] activeAccesses = CreateInterleavedReadAccesses(
            resource,
            rangeCount,
            offset: 0);
        JobResourceAccess[] deferredAccesses = CreateInterleavedWriteAccesses(
            resource,
            rangeCount,
            offset: 2);
        deferredAccesses[rangeCount / 2] = JobResourceAccess.Write(
            resource,
            start: (rangeCount / 2) * 4L,
            length: 1);

        var active = JobSystem.Schedule(
            new HardeningData.BlockingIncrementJob(_data, activeStarted, activeGate),
            activeAccesses);
        Assert.True(activeStarted.Wait(1_000));
        var deferred = JobSystem.Schedule(
            new HardeningData.SignalingIncrementJob(_data, deferredStarted),
            deferredAccesses,
            dependency);

        try
        {
            fence.Signal();

            activeGate.Set();
            Assert.True(deferredStarted.Wait(1_000));
            JobSystem.CombineDependencies([active, deferred]).Complete();
        }
        finally
        {
            activeGate.Set();
        }

        Assert.Equal(2, _data.Counter);
        _data.Events.AssertBefore(activeGate, deferredStarted);
    }

    [Fact]
    public void RangeDependencyDedupeBeyondInlineCapacityPreservesEveryOwner()
    {
        JobSystem.Initialize(new JobRuntimeConfig { WorkerCount = 0 });
        var resource = JobSystem.CreateResource("range-dependency-dedupe");
        const int ownerCount = 8;
        var fences = new ControlledFence[ownerCount];
        var owners = new JobHandle[ownerCount];
        for (int i = 0; i < ownerCount; i++)
        {
            fences[i] = new ControlledFence();
            owners[i] = JobSystem.CreateExternalFenceHandle(
                fences[i],
                JobResourceAccess.Write(resource, start: 0, length: 16));
        }

        const int duplicateQueryCount = 64;
        var candidateAccesses = new JobResourceAccess[duplicateQueryCount];
        Array.Fill(
            candidateAccesses,
            JobResourceAccess.Read(resource, start: 0, length: 16));
        var candidate = JobSystem.Schedule(
            new HardeningData.IncrementJob(_data),
            candidateAccesses);

        Assert.False(candidate.IsCompleted);
        for (int i = 0; i < fences.Length; i++)
            fences[i].Signal();

        candidate.Complete();
        for (int i = 0; i < owners.Length; i++)
            owners[i].Complete();

        Assert.Equal(1, _data.Counter);
    }

    [Fact]
    public void HighPriorityJobsRunBeforeEarlierLowPriorityJobs()
    {
        JobSystem.Initialize(new JobRuntimeConfig { WorkerCount = 0 });

        var low = JobSystem.Schedule(
            new HardeningData.ManagedJob(_data.ManagedLog, "low"),
            new JobScheduleOptions(JobPriority.Low));
        JobSystem.Schedule(
            new HardeningData.ManagedJob(_data.ManagedLog, "high"),
            new JobScheduleOptions(JobPriority.High)).Complete();

        low.Complete();

        Assert.Equal(["high", "low"], _data.ManagedLog);
    }

    [Fact]
    public void InvalidJobPriorityIsRejected()
    {
        JobSystem.Initialize(new JobRuntimeConfig { WorkerCount = 0 });

        var invalid = new JobScheduleOptions((JobPriority)99);

        var ex = Assert.Throws<ArgumentOutOfRangeException>(() =>
            JobSystem.Schedule(new HardeningData.IncrementJob(_data), invalid));
        Assert.Equal("options", ex.ParamName);
    }

    [Fact]
    public void ManyJobsAndLargePayloadsExecuteCorrectly()
    {
        JobSystem.Initialize(new JobRuntimeConfig { WorkerCount = 2 });
        var handles = new JobHandle[512];

        for (var i = 0; i < handles.Length; i++)
        {
            handles[i] = JobSystem.Schedule(new HardeningData.LargePayloadJob(_data, i));
        }

        JobSystem.CombineDependencies(handles).Complete();

        var expected = Enumerable.Range(0, handles.Length).Sum();
        Assert.Equal(expected, _data.LargePayloadSum);
    }

    [Fact]
    public void ContinuationAndScopeStateAreReclaimedUnderPressure()
    {
        JobSystem.Initialize(new JobRuntimeConfig { WorkerCount = 0 });

        for (var i = 0; i < 256; i++)
        {
            JobSystem.Schedule(new HardeningData.ParentSchedulesOneChild(_data)).Complete();
            var first = JobSystem.Schedule(new HardeningData.IncrementJob(_data));
            JobSystem.Schedule(new HardeningData.IncrementJob(_data), first).Complete();
        }

        Assert.Equal(1024, _data.Counter);
    }

    [Fact]
    [Trait("Category", "Performance")]
    public void StressAndLivenessScenariosCompleteWithinTimeout()
    {
        RunWithTimeout(() =>
        {
            JobSystem.Initialize(new JobRuntimeConfig { WorkerCount = 4 });
            JobSystem.Schedule(new HardeningData.RecursiveFanout(_data, 128)).Complete();
            Assert.Equal(129, _data.Counter);
        });

        RunWithTimeout(() =>
        {
            JobSystem.Initialize(new JobRuntimeConfig { WorkerCount = 4 });
            const int threads = 4;
            const int jobsPerThread = 128;
            var handles = new List<JobHandle>(threads * jobsPerThread);
            var handlesLock = new Lock();
            var producers = new Thread[threads];

            for (var i = 0; i < producers.Length; i++)
            {
                producers[i] = new Thread(() =>
                {
                    for (var j = 0; j < jobsPerThread; j++)
                    {
                        var handle = JobSystem.Schedule(new HardeningData.IncrementJob(_data));
                        lock (handlesLock)
                        {
                            handles.Add(handle);
                        }
                    }
                });
                producers[i].Start();
            }

            foreach (var producer in producers)
            {
                producer.Join();
            }

            JobSystem.CombineDependencies(handles.ToArray()).Complete();
            Assert.Equal(threads * jobsPerThread, _data.Counter);
        });

        RunWithTimeout(() =>
        {
            JobSystem.Initialize(new JobRuntimeConfig { WorkerCount = 4 });
            using var gate = _data.Events.CreateSignal();
            var handle = JobSystem.Schedule(new HardeningData.GatedIncrementJob(_data, gate));
            var completers = new Thread[4];

            for (var i = 0; i < completers.Length; i++)
            {
                completers[i] = new Thread(() => handle.Complete());
                completers[i].Start();
            }

            gate.Set();
            foreach (var completer in completers)
            {
                completer.Join();
            }

            Assert.Equal(1, _data.Counter);
        });
    }

    private void RunWithTimeout(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                _data.Reset();
                action();
            }
            catch (Exception ex)
            {
                error = ex;
            }
        });

        thread.IsBackground = true;
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(5)), "Stress scenario did not complete within the timeout.");
        if (error is not null)
        {
            throw error;
        }
    }

    private void ScheduleFourReadAccesses(
        JobResource a,
        JobResource b,
        JobResource c,
        JobResource d)
    {
        ReadOnlySpan<JobResourceAccess> accesses = stackalloc JobResourceAccess[]
        {
            JobResourceAccess.Read(a),
            JobResourceAccess.Read(b),
            JobResourceAccess.Read(c),
            JobResourceAccess.Read(d)
        };

        JobSystem.Schedule(new HardeningData.IncrementJob(_data), accesses).Complete();
    }

    private void ScheduleFiveReadAccesses(
        JobResource a,
        JobResource b,
        JobResource c,
        JobResource d,
        JobResource e)
    {
        ReadOnlySpan<JobResourceAccess> accesses = stackalloc JobResourceAccess[]
        {
            JobResourceAccess.Read(a),
            JobResourceAccess.Read(b),
            JobResourceAccess.Read(c),
            JobResourceAccess.Read(d),
            JobResourceAccess.Read(e)
        };

        JobSystem.Schedule(new HardeningData.IncrementJob(_data), accesses).Complete();
    }

    private void ScheduleInferredReadAfterWrite(JobResource resource)
    {
        var writer = JobSystem.Schedule(new HardeningData.IncrementJob(_data), JobResourceAccess.Write(resource));
        var reader = JobSystem.Schedule(new HardeningData.IncrementJob(_data), JobResourceAccess.Read(resource));

        writer.Complete();
        reader.Complete();
    }

    private static JobResourceAccess[] CreateInterleavedWriteAccesses(
        JobResource resource,
        int count,
        int offset)
    {
        var accesses = new JobResourceAccess[count];
        for (int i = 0; i < accesses.Length; i++)
        {
            accesses[i] = JobResourceAccess.Write(
                resource,
                start: i * 4L + offset,
                length: 1);
        }

        return accesses;
    }

    private static JobResourceAccess[] CreateInterleavedReadAccesses(
        JobResource resource,
        int count,
        int offset)
    {
        var accesses = new JobResourceAccess[count];
        for (int i = 0; i < accesses.Length; i++)
        {
            accesses[i] = JobResourceAccess.Read(
                resource,
                start: i * 4L + offset,
                length: 1);
        }

        return accesses;
    }

    private void CompleteDisjointRangeOwners(
        JobResourceAccess[] firstAccesses,
        JobResourceAccess[] secondAccesses)
    {
        var first = JobSystem.Schedule(new HardeningData.IncrementJob(_data), firstAccesses);
        var second = JobSystem.Schedule(new HardeningData.IncrementJob(_data), secondAccesses);
        second.Complete();
        first.Complete();
    }

    private sealed class ControlledFence : IJobExternalFence
    {
        private Action<object?>? _continuation;
        private object? _state;

        public bool IsSignaled { get; private set; }

        public void OnSignaled(Action<object?> continuation, object? state)
        {
            ArgumentNullException.ThrowIfNull(continuation);
            if (IsSignaled)
            {
                continuation(state);
                return;
            }

            _continuation = continuation;
            _state = state;
        }

        internal void Signal()
        {
            if (IsSignaled)
                return;

            IsSignaled = true;
            Action<object?>? continuation = _continuation;
            object? state = _state;
            _continuation = null;
            _state = null;
            continuation?.Invoke(state);
        }
    }

    private sealed class HardeningData
    {
        internal readonly ExecutionEvents Events = new();

        internal readonly List<string> ManagedLog = [];
        internal int Counter;
        internal int ChildStartedCount;
        internal int ExpectedStartedChildren;
        internal int LargePayloadSum;
        internal JobSignal? ChildGate;
        internal JobSignal? EnoughChildrenStarted;

        internal void Reset()
        {
            ManagedLog.Clear();
            Counter = 0;
            ChildStartedCount = 0;
            ExpectedStartedChildren = 0;
            LargePayloadSum = 0;
            ChildGate = null;
            EnoughChildrenStarted = null;
        }

        internal struct IncrementJob : IJob
        {
            private readonly HardeningData _data;

            internal IncrementJob(HardeningData data) => _data = data;

            public void Execute()
            {
                Interlocked.Increment(ref _data.Counter);
            }
        }

        internal readonly struct ManagedJob : IJob
        {
            private readonly List<string> _target;
            private readonly string _value;

            internal ManagedJob(List<string> target, string value)
            {
                _target = target;
                _value = value;
            }

            public void Execute()
            {
                _target.Add(_value);
            }
        }

        internal struct ThrowingJob : IJob
        {
            public void Execute()
            {
                throw new InvalidOperationException("fault");
            }
        }

        internal readonly struct ParentSchedulesBlockingChildren : IJob
        {
            private readonly HardeningData _data;

            private readonly int _childCount;

            internal ParentSchedulesBlockingChildren(HardeningData data, int childCount)
            {
                _data = data;
                _childCount = childCount;
            }

            public void Execute()
            {
                for (var i = 0; i < _childCount; i++)
                {
                    JobSystem.Schedule(new BlockingChildJob(_data));
                }
            }
        }

        internal struct BlockingChildJob : IJob
        {
            private readonly HardeningData _data;

            internal BlockingChildJob(HardeningData data) => _data = data;

            public void Execute()
            {
                var started = Interlocked.Increment(ref _data.ChildStartedCount);
                if (started >= _data.ExpectedStartedChildren)
                {
                    _data.EnoughChildrenStarted?.Set();
                }

                _data.ChildGate?.Wait();
                Interlocked.Increment(ref _data.Counter);
            }
        }

        internal readonly struct LargePayloadJob : IJob
        {
            private readonly HardeningData _data;

            private readonly int _value;
            private readonly long _a;
            private readonly long _b;
            private readonly long _c;
            private readonly long _d;
            private readonly long _e;
            private readonly long _f;
            private readonly long _g;
            private readonly long _h;

            internal LargePayloadJob(HardeningData data, int value)
            {
                _data = data;
                _value = value;
                _a = value;
                _b = value;
                _c = value;
                _d = value;
                _e = value;
                _f = value;
                _g = value;
                _h = value;
            }

            public void Execute()
            {
                _ = _a + _b + _c + _d + _e + _f + _g + _h;
                Interlocked.Add(ref _data.LargePayloadSum, _value);
            }
        }

        internal struct ParentSchedulesOneChild : IJob
        {
            private readonly HardeningData _data;

            internal ParentSchedulesOneChild(HardeningData data) => _data = data;

            public void Execute()
            {
                Interlocked.Increment(ref _data.Counter);
                JobSystem.Schedule(new IncrementJob(_data));
            }
        }

        internal readonly struct RecursiveFanout : IJob
        {
            private readonly HardeningData _data;

            private readonly int _remaining;

            internal RecursiveFanout(HardeningData data, int remaining)
            {
                _data = data;
                _remaining = remaining;
            }

            public void Execute()
            {
                Interlocked.Increment(ref _data.Counter);
                if (_remaining > 0)
                {
                    JobSystem.Schedule(new RecursiveFanout(_data, _remaining - 1));
                }
            }
        }

        internal readonly struct GatedIncrementJob : IJob
        {
            private readonly HardeningData _data;

            private readonly JobSignal _gate;

            internal GatedIncrementJob(HardeningData data, JobSignal gate)
            {
                _data = data;
                _gate = gate;
            }

            public void Execute()
            {
                _gate.Wait();
                Interlocked.Increment(ref _data.Counter);
            }
        }

        internal readonly struct BlockingIncrementJob : IJob
        {
            private readonly HardeningData _data;

            private readonly JobSignal _started;
            private readonly JobSignal _gate;

            internal BlockingIncrementJob(
                HardeningData data, JobSignal started,
                JobSignal gate)
            {
                _data = data;
                _started = started;
                _gate = gate;
            }

            public void Execute()
            {
                _started.Set();
                _gate.Wait();
                Interlocked.Increment(ref _data.Counter);
            }
        }

        internal readonly struct SignalingIncrementJob : IJob
        {
            private readonly HardeningData _data;

            private readonly JobSignal _started;

            internal SignalingIncrementJob(HardeningData data, JobSignal started)
            {
                _data = data;
                _started = started;
            }

            public void Execute()
            {
                _started.Set();
                Interlocked.Increment(ref _data.Counter);
            }
        }
    }

    private readonly struct ParallelMarkJob : IJobParallelFor
    {
        private readonly int[] _values;

        internal ParallelMarkJob(int[] values)
        {
            _values = values;
        }

        public void Execute(int index)
        {
            Interlocked.Increment(ref _values[index]);
        }
    }
}
