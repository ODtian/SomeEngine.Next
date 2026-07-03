using System.Runtime.CompilerServices;

namespace SomeEngine.Job.Tests;

public sealed class PayloadLaneTests
{
    public PayloadLaneTests()
    {
        JobSystem.ResetForTesting();
    }

    [Fact]
    public void RefFreeJobIsClassifiedIntoFastLane()
    {
        Assert.Equal(JobPayloadLane.RefFree, JobSystem.GetPayloadLane<RefFreePayloadJob>());
    }

    [Fact]
    public void RefContainingJobIsClassifiedIntoManagedLane()
    {
        Assert.Equal(JobPayloadLane.RefContaining, JobSystem.GetPayloadLane<RefContainingPayloadJob>());
    }

    [Fact]
    public void ManagedReferencePayloadExecutes()
    {
        var sink = new List<string>();

        JobSystem.Schedule(new RefContainingPayloadJob(sink, "ran")).Complete();

        Assert.Equal(["ran"], sink);
    }

    [Fact]
    public void ManagedPayloadPolicyWarnsOrRejectsRefContainingJobs()
    {
        var sink = new List<string>();

        JobSystem.ManagedPayloadPolicy = ManagedPayloadPolicy.Warn;
        JobSystem.Schedule(new RefContainingPayloadJob(sink, "warned")).Complete();

        Assert.Equal(["warned"], sink);
        Assert.Equal(1, JobSystem.GetStats().ManagedPayloadWarnings);

        JobSystem.ManagedPayloadPolicy = ManagedPayloadPolicy.Reject;
        var ex = Assert.Throws<InvalidOperationException>(() =>
            JobSystem.Schedule(new RefContainingPayloadJob(sink, "rejected")));

        Assert.Contains("managed payload policy", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(["warned"], sink);
    }

    [Fact]
    public void RefContainingSchedulerPathClearsPayloadSlot()
    {
        JobSystem.ResetForTesting(workerCount: 0);

        var weak = CreateScheduledPayloadWeakReference();

        ForceFullCollection();

        Assert.False(weak.IsAlive);
    }

    [Fact]
    public void RefContainingParallelSchedulerPathClearsPayloadSlots()
    {
        JobSystem.ResetForTesting(workerCount: 0);

        var weak = CreateScheduledParallelPayloadWeakReference();

        ForceFullCollection();

        Assert.False(weak.IsAlive);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference CreateScheduledPayloadWeakReference()
    {
        var target = new object();
        var weak = new WeakReference(target);

        JobSystem.Schedule(new RefContainingLifetimeJob(target)).Complete();
        target = null;

        return weak;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference CreateScheduledParallelPayloadWeakReference()
    {
        var target = new object();
        var weak = new WeakReference(target);

        JobSystem.ScheduleParallel(new RefContainingParallelLifetimeJob(target), 4, 1).Complete();
        target = null;

        return weak;
    }

    private static void ForceFullCollection()
    {
        for (var i = 0; i < 3; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }
    }

    private struct RefFreePayloadJob : IJob
    {
        public int Value;

        public void Execute()
        {
            Value++;
        }
    }

    private readonly struct RefContainingPayloadJob : IJob
    {
        private readonly List<string> _sink;
        private readonly string _value;

        internal RefContainingPayloadJob(List<string> sink, string value)
        {
            _sink = sink;
            _value = value;
        }

        public void Execute()
        {
            _sink.Add(_value);
        }
    }

    private readonly struct RefContainingLifetimeJob : IJob
    {
        private readonly object? _target;

        internal RefContainingLifetimeJob(object target)
        {
            _target = target;
        }

        public void Execute()
        {
            GC.KeepAlive(_target);
        }
    }

    private readonly struct RefContainingParallelLifetimeJob : IJobParallelFor
    {
        private readonly object? _target;

        internal RefContainingParallelLifetimeJob(object target)
        {
            _target = target;
        }

        public void Execute(int index)
        {
            GC.KeepAlive(_target);
        }
    }
}
