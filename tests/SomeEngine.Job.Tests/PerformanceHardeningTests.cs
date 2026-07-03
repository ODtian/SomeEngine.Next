using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace SomeEngine.Job.Tests;

public sealed class PerformanceHardeningTests
{
    public PerformanceHardeningTests()
    {
        JobSystem.ResetForTesting(new JobRuntimeConfig { WorkerCount = 2 });
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

        JobSystem.Schedule(new HardeningJobs.IncrementJob()).Complete();
        Assert.Equal(2, HardeningJobs.Counter);
    }

    [Fact]
    public void QueueAndResourceCapacityExhaustionAreExplicit()
    {
        JobSystem.ResetForTesting(new JobRuntimeConfig
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
        JobSystem.ResetForTesting(new JobRuntimeConfig
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
        JobSystem.ResetForTesting(new JobRuntimeConfig
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
        JobSystem.ResetForTesting(new JobRuntimeConfig
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
        JobSystem.ResetForTesting(new JobRuntimeConfig
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
    public void RuntimeCountersTrackJobsLanesFaultsAndHighWater()
    {
        JobSystem.ResetForTesting(new JobRuntimeConfig { WorkerCount = 0 });

        JobSystem.Schedule(new HardeningJobs.IncrementJob()).Complete();
        JobSystem.Schedule(new HardeningJobs.ManagedJob(HardeningJobs.ManagedLog, "managed")).Complete();
        var faulted = JobSystem.Schedule(new HardeningJobs.ThrowingJob());
        Assert.Throws<InvalidOperationException>(() => faulted.Complete());

        var stats = JobSystem.GetStats();
        Assert.Equal(3, stats.ScheduledJobs);
        Assert.Equal(3, stats.ExecutedWorkItems);
        Assert.Equal(3, stats.CompletedHandles);
        Assert.Equal(1, stats.FaultedWorkItems);
        Assert.Equal(2, stats.RefFreeJobs);
        Assert.Equal(1, stats.RefContainingJobs);
        Assert.True(stats.QueueHighWater >= 1);
        Assert.True(stats.CompletionStateHighWater >= 1);
    }

    [Fact]
    public void ResourceConflictChecksExposeRangedScans()
    {
        JobSystem.ResetForTesting(new JobRuntimeConfig { WorkerCount = 0 });
        var resource = JobSystem.CreateResource("ranged-resource");

        var first = JobSystem.Schedule(
            new HardeningJobs.IncrementJob(),
            JobResourceAccess.Read(resource, start: 0, length: 4));
        var second = JobSystem.Schedule(
            new HardeningJobs.IncrementJob(),
            JobResourceAccess.Write(resource, start: 8, length: 4));

        first.Complete();
        second.Complete();

        var stats = JobSystem.GetStats();
        Assert.Equal(2, HardeningJobs.Counter);
        Assert.Equal(2, stats.ResourceConflictChecks);
        Assert.Equal(1, stats.ResourceConflictCheckSteps);
    }

    [Fact]
    public void HighPriorityJobsRunBeforeEarlierLowPriorityJobs()
    {
        JobSystem.ResetForTesting(new JobRuntimeConfig { WorkerCount = 0 });

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
        JobSystem.ResetForTesting(new JobRuntimeConfig { WorkerCount = 0 });

        var invalid = new JobScheduleOptions((JobPriority)99);

        var ex = Assert.Throws<ArgumentOutOfRangeException>(() =>
            JobSystem.Schedule(new HardeningJobs.IncrementJob(), invalid));
        Assert.Contains("priority", ex.Message);
    }

    [Fact]
    [Trait("Category", "Performance")]
    public void WorkerChildJobsUseLocalQueueAndCanBeStolen()
    {
        JobSystem.ResetForTesting(new JobRuntimeConfig { WorkerCount = 4 });
        using var gate = new ManualResetEventSlim();
        using var enoughChildrenStarted = new ManualResetEventSlim();
        HardeningJobs.ChildGate = gate;
        HardeningJobs.EnoughChildrenStarted = enoughChildrenStarted;
        HardeningJobs.ExpectedStartedChildren = 2;

        var parent = JobSystem.Schedule(new HardeningJobs.ParentSchedulesBlockingChildren(16));

        Assert.True(enoughChildrenStarted.Wait(TimeSpan.FromSeconds(2)));
        gate.Set();
        parent.Complete();

        var stats = JobSystem.GetStats();
        Assert.Equal(16, HardeningJobs.Counter);
        Assert.True(stats.LocalQueuedWorkItems >= 16);
        Assert.True(stats.StolenWorkItems > 0);
    }

    [Fact]
    [Trait("Category", "Performance")]
    public void WarmedRefFreeSchedulePathHasBoundedAllocations()
    {
        JobSystem.ResetForTesting(new JobRuntimeConfig { WorkerCount = 0 });

        for (var i = 0; i < 128; i++)
        {
            JobSystem.Schedule(new HardeningJobs.IncrementJob()).Complete();
        }

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 32; i++)
        {
            JobSystem.Schedule(new HardeningJobs.IncrementJob()).Complete();
        }

        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.InRange(allocated, 0, 1_024);
    }

    [Fact]
    [Trait("Category", "Performance")]
    public void DependencyHeavyWarmPathHasBoundedAllocations()
    {
        JobSystem.ResetForTesting(new JobRuntimeConfig { WorkerCount = 0 });

        for (var i = 0; i < 64; i++)
        {
            var first = JobSystem.Schedule(new HardeningJobs.IncrementJob());
            JobSystem.Schedule(new HardeningJobs.IncrementJob(), first).Complete();
        }

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 16; i++)
        {
            var first = JobSystem.Schedule(new HardeningJobs.IncrementJob());
            JobSystem.Schedule(new HardeningJobs.IncrementJob(), first).Complete();
        }

        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.InRange(allocated, 0, 2_048);
    }

    [Fact]
    [Trait("Category", "Performance")]
    public void SmallMultiAccessDeclarationPathHasBoundedAllocations()
    {
        JobSystem.ResetForTesting(new JobRuntimeConfig { WorkerCount = 0 });
        var a = JobSystem.CreateResource("a");
        var b = JobSystem.CreateResource("b");
        var c = JobSystem.CreateResource("c");
        var d = JobSystem.CreateResource("d");

        for (var i = 0; i < 64; i++)
        {
            ScheduleFourReadAccesses(a, b, c, d);
        }

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 32; i++)
        {
            ScheduleFourReadAccesses(a, b, c, d);
        }

        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.InRange(allocated, 0, 1_024);
    }

    [Fact]
    [Trait("Category", "Performance")]
    public void OverflowMultiAccessDeclarationPathReusesPooledStorage()
    {
        JobSystem.ResetForTesting(new JobRuntimeConfig { WorkerCount = 0 });
        var a = JobSystem.CreateResource("a");
        var b = JobSystem.CreateResource("b");
        var c = JobSystem.CreateResource("c");
        var d = JobSystem.CreateResource("d");
        var e = JobSystem.CreateResource("e");

        for (var i = 0; i < 64; i++)
        {
            ScheduleFiveReadAccesses(a, b, c, d, e);
        }

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 32; i++)
        {
            ScheduleFiveReadAccesses(a, b, c, d, e);
        }

        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.InRange(allocated, 0, 1_024);
    }

    [Fact]
    [Trait("Category", "Performance")]
    public void InferredResourceDependencyPathHasBoundedAllocations()
    {
        JobSystem.ResetForTesting(new JobRuntimeConfig { WorkerCount = 0 });
        var resource = JobSystem.CreateResource("dependency-resource");

        for (var i = 0; i < 64; i++)
        {
            ScheduleInferredReadAfterWrite(resource);
        }

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 32; i++)
        {
            ScheduleInferredReadAfterWrite(resource);
        }

        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.InRange(allocated, 0, 2_048);
        Assert.Equal(192, HardeningJobs.Counter);
    }

    [Fact]
    public void RefFreePressureAndLargePayloadsExecuteCorrectly()
    {
        JobSystem.ResetForTesting(new JobRuntimeConfig { WorkerCount = 2 });
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
        JobSystem.ResetForTesting(new JobRuntimeConfig { WorkerCount = 0 });

        for (var i = 0; i < 256; i++)
        {
            JobSystem.Schedule(new HardeningJobs.ParentSchedulesOneChild()).Complete();
            var first = JobSystem.Schedule(new HardeningJobs.IncrementJob());
            JobSystem.Schedule(new HardeningJobs.IncrementJob(), first).Complete();
        }

        var stats = JobSystem.GetStats();
        Assert.Equal(1024, HardeningJobs.Counter);
        Assert.True(stats.CompletionStateHighWater < 32);
    }

    [Fact]
    [Trait("Category", "Performance")]
    public void BenchmarkHarnessScenariosCompileAndRun()
    {
        JobSystem.ResetForTesting(new JobRuntimeConfig { WorkerCount = 0 });

        var results = SomeEngineJobBenchmarkHarness.RunAll(iterations: 16);

        Assert.Equal(
            ["simple-schedule", "dependency-chain", "child-scope", "parallel-for"],
            results.Select(result => result.Name).ToArray());
        Assert.All(results, result => Assert.True(result.ElapsedTicks >= 0));
        Assert.Equal([16, 32, 32, 512], results.Select(result => result.WorkItems).ToArray());
    }

    [Fact]
    [Trait("Category", "Performance")]
    public void StressAndLivenessScenariosCompleteWithinTimeout()
    {
        RunWithTimeout(static () =>
        {
            JobSystem.ResetForTesting(new JobRuntimeConfig { WorkerCount = 4 });
            JobSystem.Schedule(new HardeningJobs.RecursiveFanout(128)).Complete();
            Assert.Equal(129, HardeningJobs.Counter);
        });

        RunWithTimeout(static () =>
        {
            JobSystem.ResetForTesting(new JobRuntimeConfig { WorkerCount = 4 });
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
            JobSystem.ResetForTesting(new JobRuntimeConfig { WorkerCount = 4 });
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
    }

    private static class SomeEngineJobBenchmarkHarness
    {
        internal static BenchmarkResult[] RunAll(int iterations)
        {
            return
            [
                Measure("simple-schedule", iterations, static () =>
                {
                    JobSystem.Schedule(new HardeningJobs.IncrementJob()).Complete();
                    return 1;
                }),
                Measure("dependency-chain", iterations, static () =>
                {
                    var dependency = JobSystem.Schedule(new HardeningJobs.IncrementJob());
                    JobSystem.Schedule(new HardeningJobs.IncrementJob(), dependency).Complete();
                    return 2;
                }),
                Measure("child-scope", iterations, static () =>
                {
                    JobSystem.Schedule(new HardeningJobs.ParentSchedulesOneChild()).Complete();
                    return 2;
                }),
                Measure("parallel-for", iterations, static () =>
                {
                    var values = new int[32];
                    JobSystem.ScheduleParallel(new ParallelMarkJob(values), values.Length, 4).Complete();
                    return values.Sum();
                })
            ];
        }

        private static BenchmarkResult Measure(string name, int iterations, Func<int> action)
        {
            var workItems = 0;
            var stopwatch = Stopwatch.StartNew();
            for (var i = 0; i < iterations; i++)
            {
                workItems += action();
            }

            stopwatch.Stop();
            return new BenchmarkResult(name, stopwatch.ElapsedTicks, workItems);
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
