using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace SomeEngine.Job.Tests;

public sealed class PerformanceHardeningTests
{
    public PerformanceHardeningTests()
    {
        JobSystem.Initialize(new JobRuntimeConfig { WorkerCount = 2 });
        HardeningJobs.Reset();
    }






    [Fact]
    public void DefaultAndCustomConfigInitializeRuntime()
    {
        JobSystem.Initialize(JobRuntimeConfig.Default);
        JobSystem.Schedule(new HardeningJobs.IncrementJob()).Complete();

        JobSystem.Initialize(new JobRuntimeConfig
        {
            WorkerCount = 0,
            MaxQueuedWorkItems = 4,
            MaxCompletionStates = 4,
            MaxResourceStates = 4,
            SafetyMode = JobSafetyMode.Strict
        });

        JobSystem.Schedule(new HardeningJobs.IncrementJob()).Complete();

        Assert.Equal(2, HardeningJobs.Counter);
        Assert.Equal(JobSafetyMode.Strict, JobSystem.SafetyMode);
    }

    [Fact]
    public void InvalidConfigIsRejectedBeforeReplacingRuntime()
    {
        JobSystem.Schedule(new HardeningJobs.IncrementJob()).Complete();

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

        JobSystem.Schedule(new HardeningJobs.IncrementJob()).Complete();
        Assert.Equal(2, HardeningJobs.Counter);
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

        var queued = JobSystem.Schedule(new HardeningJobs.IncrementJob());

        var queueExhausted = Assert.Throws<InvalidOperationException>(() =>
            JobSystem.Schedule(new HardeningJobs.IncrementJob()));
        Assert.Contains("queue capacity", queueExhausted.Message);

        queued.Complete();

        _ = JobSystem.CreateResource("only-resource");
        var resourceExhausted = Assert.Throws<InvalidOperationException>(() =>
            JobSystem.CreateResource("too-many-resources"));
        Assert.Contains("Resource state capacity", resourceExhausted.Message);
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

        var queued = JobSystem.Schedule(new HardeningJobs.IncrementJob());

        var exhausted = Assert.Throws<InvalidOperationException>(() =>
            JobSystem.Schedule(new HardeningJobs.IncrementJob()));
        Assert.Contains("Completion state capacity", exhausted.Message);

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

        var dependency = JobSystem.Schedule(new HardeningJobs.IncrementJob());
        var dependent = JobSystem.Schedule(new HardeningJobs.IncrementJob(), dependency);

        dependency.Complete();

        Assert.True(dependent.IsCompleted);
        var ex = Assert.Throws<InvalidOperationException>(() => dependent.Complete());
        Assert.Contains("queue capacity", ex.Message);
        Assert.Equal(1, HardeningJobs.Counter);
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
            var handle = JobSystem.Schedule(new HardeningJobs.ThrowingJob());
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

        var faulted = JobSystem.Schedule(new HardeningJobs.ThrowingJob());

        Assert.True(SpinWait.SpinUntil(() => faulted.IsCompleted, TimeSpan.FromSeconds(2)));
        Assert.Throws<InvalidOperationException>(() =>
            JobSystem.Schedule(new HardeningJobs.IncrementJob()));

        Assert.Throws<InvalidOperationException>(() => faulted.Complete());
    }





    [Fact]
    public void IndexedRangeFrontierRetainsSingleOverlapDependency()
    {
        JobSystem.Initialize(new JobRuntimeConfig { WorkerCount = 2 });
        var resource = JobSystem.CreateResource("indexed-range-overlap");
        const int rangeCount = 256;
        using var firstStarted = new ManualResetEventSlim();
        using var firstGate = new ManualResetEventSlim();
        using var secondStarted = new ManualResetEventSlim();
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
            new HardeningJobs.BlockingIncrementJob(firstStarted, firstGate),
            firstAccesses);
        Assert.True(firstStarted.Wait(1_000));
        var second = JobSystem.Schedule(
            new HardeningJobs.SignalingIncrementJob(secondStarted),
            secondAccesses);

        try
        {
            Assert.False(secondStarted.Wait(100));
            firstGate.Set();
            Assert.True(secondStarted.Wait(1_000));
            JobSystem.CombineDependencies([first, second]).Complete();
        }
        finally
        {
            firstGate.Set();
        }

        Assert.Equal(2, HardeningJobs.Counter);
    }



    [Fact]
    public void DeferredIndexedRangeActivationRetainsSingleOverlapDependency()
    {
        JobSystem.Initialize(new JobRuntimeConfig { WorkerCount = 2 });
        var resource = JobSystem.CreateResource("deferred-indexed-range-overlap");
        const int rangeCount = 256;
        var fence = new ControlledFence();
        JobHandle dependency = JobSystem.CreateExternalFenceHandle(fence);
        using var activeStarted = new ManualResetEventSlim();
        using var activeGate = new ManualResetEventSlim();
        using var deferredStarted = new ManualResetEventSlim();
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
            new HardeningJobs.BlockingIncrementJob(activeStarted, activeGate),
            activeAccesses);
        Assert.True(activeStarted.Wait(1_000));
        var deferred = JobSystem.Schedule(
            new HardeningJobs.SignalingIncrementJob(deferredStarted),
            deferredAccesses,
            dependency);

        try
        {
            fence.Signal();
            Assert.False(deferredStarted.Wait(100));
            activeGate.Set();
            Assert.True(deferredStarted.Wait(1_000));
            JobSystem.CombineDependencies([active, deferred]).Complete();
        }
        finally
        {
            activeGate.Set();
        }

        Assert.Equal(2, HardeningJobs.Counter);
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
            new HardeningJobs.IncrementJob(),
            candidateAccesses);

        Assert.False(candidate.IsCompleted);
        for (int i = 0; i < fences.Length; i++)
            fences[i].Signal();

        candidate.Complete();
        for (int i = 0; i < owners.Length; i++)
            owners[i].Complete();

        Assert.Equal(1, HardeningJobs.Counter);
    }

    [Fact]
    public void HighPriorityJobsRunBeforeEarlierLowPriorityJobs()
    {
        JobSystem.Initialize(new JobRuntimeConfig { WorkerCount = 0 });

        var low = JobSystem.Schedule(
            new HardeningJobs.ManagedJob(HardeningJobs.ManagedLog, "low"),
            new JobScheduleOptions(JobPriority.Low));
        JobSystem.Schedule(
            new HardeningJobs.ManagedJob(HardeningJobs.ManagedLog, "high"),
            new JobScheduleOptions(JobPriority.High)).Complete();

        low.Complete();

        Assert.Equal(["high", "low"], HardeningJobs.ManagedLog);
    }

    [Fact]
    public void InvalidJobPriorityIsRejected()
    {
        JobSystem.Initialize(new JobRuntimeConfig { WorkerCount = 0 });

        var invalid = new JobScheduleOptions((JobPriority)99);

        var ex = Assert.Throws<ArgumentOutOfRangeException>(() =>
            JobSystem.Schedule(new HardeningJobs.IncrementJob(), invalid));
        Assert.Contains("priority", ex.Message);
    }








    [Fact]
    public void RefFreePressureAndLargePayloadsExecuteCorrectly()
    {
        JobSystem.Initialize(new JobRuntimeConfig { WorkerCount = 2 });
        var handles = new JobHandle[512];

        for (var i = 0; i < handles.Length; i++)
        {
            handles[i] = JobSystem.Schedule(new HardeningJobs.LargePayloadJob(i));
        }

        JobSystem.CombineDependencies(handles).Complete();

        var expected = Enumerable.Range(0, handles.Length).Sum();
        Assert.Equal(expected, HardeningJobs.LargePayloadSum);
    }

    [Fact]
    public void ContinuationAndScopeStateAreReclaimedUnderPressure()
    {
        JobSystem.Initialize(new JobRuntimeConfig { WorkerCount = 0 });

        for (var i = 0; i < 256; i++)
        {
            JobSystem.Schedule(new HardeningJobs.ParentSchedulesOneChild()).Complete();
            var first = JobSystem.Schedule(new HardeningJobs.IncrementJob());
            JobSystem.Schedule(new HardeningJobs.IncrementJob(), first).Complete();
        }

        Assert.Equal(1024, HardeningJobs.Counter);
    }


    [Fact]
    [Trait("Category", "Performance")]
    public void StressAndLivenessScenariosCompleteWithinTimeout()
    {
        RunWithTimeout(static () =>
        {
            JobSystem.Initialize(new JobRuntimeConfig { WorkerCount = 4 });
            JobSystem.Schedule(new HardeningJobs.RecursiveFanout(128)).Complete();
            Assert.Equal(129, HardeningJobs.Counter);
        });

        RunWithTimeout(static () =>
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
                        var handle = JobSystem.Schedule(new HardeningJobs.IncrementJob());
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
            Assert.Equal(threads * jobsPerThread, HardeningJobs.Counter);
        });

        RunWithTimeout(static () =>
        {
            JobSystem.Initialize(new JobRuntimeConfig { WorkerCount = 4 });
            using var gate = new ManualResetEventSlim();
            var handle = JobSystem.Schedule(new HardeningJobs.GatedIncrementJob(gate));
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

            Assert.Equal(1, HardeningJobs.Counter);
        });
    }

    private static void RunWithTimeout(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                HardeningJobs.Reset();
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

    private static void ScheduleFourReadAccesses(
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

        JobSystem.Schedule(new HardeningJobs.IncrementJob(), accesses).Complete();
    }

    private static void ScheduleFiveReadAccesses(
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

        JobSystem.Schedule(new HardeningJobs.IncrementJob(), accesses).Complete();
    }

    private static void ScheduleInferredReadAfterWrite(JobResource resource)
    {
        var writer = JobSystem.Schedule(new HardeningJobs.IncrementJob(), JobResourceAccess.Write(resource));
        var reader = JobSystem.Schedule(new HardeningJobs.IncrementJob(), JobResourceAccess.Read(resource));

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

    private static void CompleteDisjointRangeOwners(
        JobResourceAccess[] firstAccesses,
        JobResourceAccess[] secondAccesses)
    {
        var first = JobSystem.Schedule(new HardeningJobs.IncrementJob(), firstAccesses);
        var second = JobSystem.Schedule(new HardeningJobs.IncrementJob(), secondAccesses);
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

    private static class HardeningJobs
    {
        internal static readonly List<string> ManagedLog = [];
        internal static int Counter;
        internal static int ChildStartedCount;
        internal static int ExpectedStartedChildren;
        internal static int LargePayloadSum;
        internal static ManualResetEventSlim? ChildGate;
        internal static ManualResetEventSlim? EnoughChildrenStarted;

        internal static void Reset()
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
            public void Execute()
            {
                Interlocked.Increment(ref Counter);
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
            private readonly int _childCount;

            internal ParentSchedulesBlockingChildren(int childCount)
            {
                _childCount = childCount;
            }

            public void Execute()
            {
                for (var i = 0; i < _childCount; i++)
                {
                    JobSystem.Schedule(new BlockingChildJob());
                }
            }
        }

        internal struct BlockingChildJob : IJob
        {
            public void Execute()
            {
                var started = Interlocked.Increment(ref ChildStartedCount);
                if (started >= ExpectedStartedChildren)
                {
                    EnoughChildrenStarted?.Set();
                }

                ChildGate?.Wait();
                Interlocked.Increment(ref Counter);
            }
        }

        internal readonly struct LargePayloadJob : IJob
        {
            private readonly int _value;
            private readonly long _a;
            private readonly long _b;
            private readonly long _c;
            private readonly long _d;
            private readonly long _e;
            private readonly long _f;
            private readonly long _g;
            private readonly long _h;

            internal LargePayloadJob(int value)
            {
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
                Interlocked.Add(ref LargePayloadSum, _value);
            }
        }

        internal struct ParentSchedulesOneChild : IJob
        {
            public void Execute()
            {
                Interlocked.Increment(ref Counter);
                JobSystem.Schedule(new IncrementJob());
            }
        }

        internal readonly struct RecursiveFanout : IJob
        {
            private readonly int _remaining;

            internal RecursiveFanout(int remaining)
            {
                _remaining = remaining;
            }

            public void Execute()
            {
                Interlocked.Increment(ref Counter);
                if (_remaining > 0)
                {
                    JobSystem.Schedule(new RecursiveFanout(_remaining - 1));
                }
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

        internal readonly struct BlockingIncrementJob : IJob
        {
            private readonly ManualResetEventSlim _started;
            private readonly ManualResetEventSlim _gate;

            internal BlockingIncrementJob(
                ManualResetEventSlim started,
                ManualResetEventSlim gate)
            {
                _started = started;
                _gate = gate;
            }

            public void Execute()
            {
                _started.Set();
                _gate.Wait();
                Interlocked.Increment(ref Counter);
            }
        }

        internal readonly struct SignalingIncrementJob : IJob
        {
            private readonly ManualResetEventSlim _started;

            internal SignalingIncrementJob(ManualResetEventSlim started)
            {
                _started = started;
            }

            public void Execute()
            {
                _started.Set();
                Interlocked.Increment(ref Counter);
            }
        }
    }



    private readonly record struct BenchmarkResult(string Name, long ElapsedTicks, int WorkItems);

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
