using System.Diagnostics;

namespace SomeEngine.Job;

internal sealed class RuntimeCounters
{
    private readonly bool _enabled;
    private readonly Lock _shardLock = new();
    private readonly List<CounterShard> _shards = [];
    private int _queueHighWater;
    private int _completionStateHighWater;
    private int _resourceStateHighWater;
    private long _readyLatencyMaxTicks;

    [ThreadStatic]
    private static CounterShardRegistration? s_threadShard;

    internal RuntimeCounters(bool enabled)
    {
        _enabled = enabled;
    }

    internal bool Enabled => _enabled;

    internal void Scheduled(JobPayloadLane lane)
    {
        CounterShard? shard = GetShard();
        if (shard is null)
            return;

        shard.ScheduledJobs++;
        if (lane == JobPayloadLane.RefFree)
            shard.RefFreeJobs++;
        else
            shard.RefContainingJobs++;
    }

    internal void Executed(int count = 1)
    {
        if (count <= 0)
            return;
        CounterShard? shard = GetShard();
        if (shard is not null)
            shard.ExecutedWorkItems += count;
    }

    internal void Completed()
    {
        CounterShard? shard = GetShard();
        if (shard is not null)
            shard.CompletedHandles++;
    }

    internal void Faulted(int count = 1)
    {
        if (count <= 0)
            return;
        CounterShard? shard = GetShard();
        if (shard is not null)
            shard.FaultedWorkItems += count;
    }

    internal void Waited()
    {
        CounterShard? shard = GetShard();
        if (shard is not null)
            shard.WaitedCompletes++;
    }

    internal void Queued(int count, bool local)
    {
        CounterShard? shard = GetShard();
        if (shard is null)
            return;

        shard.QueuedWorkItems += count;
        if (local)
            shard.LocalQueuedWorkItems += count;
    }

    internal void Stolen()
    {
        CounterShard? shard = GetShard();
        if (shard is not null)
            shard.StolenWorkItems++;
    }

    internal void ManagedPayloadWarning()
    {
        CounterShard? shard = GetShard();
        if (shard is not null)
            shard.ManagedPayloadWarnings++;
    }

    internal void ResourceConflictCheck(int steps)
    {
        CounterShard? shard = GetShard();
        if (shard is null)
            return;

        shard.ResourceConflictChecks++;
        shard.ResourceConflictCheckSteps += steps;
    }

    internal void ReadyTicketsPublished(int count)
    {
        if (count <= 0)
            return;
        CounterShard? shard = GetShard();
        if (shard is not null)
            shard.ReadyTicketsPublished += count;
    }

    internal void ReadyTicketExecuted(long latencyTicks)
    {
        CounterShard? shard = GetShard();
        if (shard is null)
            return;

        shard.ReadyTicketsExecuted++;
        if (latencyTicks > 0)
        {
            shard.ReadyLatencySamples++;
            shard.ReadyLatencyTicks += latencyTicks;
            SetHighWater(ref _readyLatencyMaxTicks, latencyTicks);
        }
    }

    internal void WorkerSpun()
    {
        CounterShard? shard = GetShard();
        if (shard is not null)
            shard.WorkerSpinWaits++;
    }

    internal void WorkerParked()
    {
        CounterShard? shard = GetShard();
        if (shard is not null)
            shard.WorkerParks++;
    }

    internal void WorkerWoke()
    {
        CounterShard? shard = GetShard();
        if (shard is not null)
            shard.WorkerWakeups++;
    }

    internal void QueueHighWater(int value)
    {
        SetHighWater(ref _queueHighWater, value);
    }

    internal void CompletionStateHighWater(int value)
    {
        SetHighWater(ref _completionStateHighWater, value);
    }

    internal void ResourceStateHighWater(int value)
    {
        SetHighWater(ref _resourceStateHighWater, value);
    }

    internal JobRuntimeStats Snapshot()
    {
        long scheduledJobs = 0;
        long executedWorkItems = 0;
        long completedHandles = 0;
        long faultedWorkItems = 0;
        long waitedCompletes = 0;
        long queuedWorkItems = 0;
        long localQueuedWorkItems = 0;
        long stolenWorkItems = 0;
        long refFreeJobs = 0;
        long refContainingJobs = 0;
        long managedPayloadWarnings = 0;
        long resourceConflictChecks = 0;
        long resourceConflictCheckSteps = 0;
        long readyTicketsPublished = 0;
        long readyTicketsExecuted = 0;
        long workerSpinWaits = 0;
        long workerParks = 0;
        long workerWakeups = 0;
        long readyLatencyTicks = 0;
        long readyLatencySamples = 0;

        lock (_shardLock)
        {
            foreach (CounterShard shard in _shards)
            {
                scheduledJobs += Volatile.Read(ref shard.ScheduledJobs);
                executedWorkItems += Volatile.Read(ref shard.ExecutedWorkItems);
                completedHandles += Volatile.Read(ref shard.CompletedHandles);
                faultedWorkItems += Volatile.Read(ref shard.FaultedWorkItems);
                waitedCompletes += Volatile.Read(ref shard.WaitedCompletes);
                queuedWorkItems += Volatile.Read(ref shard.QueuedWorkItems);
                localQueuedWorkItems += Volatile.Read(ref shard.LocalQueuedWorkItems);
                stolenWorkItems += Volatile.Read(ref shard.StolenWorkItems);
                refFreeJobs += Volatile.Read(ref shard.RefFreeJobs);
                refContainingJobs += Volatile.Read(ref shard.RefContainingJobs);
                managedPayloadWarnings += Volatile.Read(ref shard.ManagedPayloadWarnings);
                resourceConflictChecks += Volatile.Read(ref shard.ResourceConflictChecks);
                resourceConflictCheckSteps += Volatile.Read(ref shard.ResourceConflictCheckSteps);
                readyTicketsPublished += Volatile.Read(ref shard.ReadyTicketsPublished);
                readyTicketsExecuted += Volatile.Read(ref shard.ReadyTicketsExecuted);
                workerSpinWaits += Volatile.Read(ref shard.WorkerSpinWaits);
                workerParks += Volatile.Read(ref shard.WorkerParks);
                workerWakeups += Volatile.Read(ref shard.WorkerWakeups);
                readyLatencyTicks += Volatile.Read(ref shard.ReadyLatencyTicks);
                readyLatencySamples += Volatile.Read(ref shard.ReadyLatencySamples);
            }
        }

        long readyLatencyAverageNanoseconds = readyLatencySamples == 0
            ? 0
            : TicksToNanoseconds(readyLatencyTicks / readyLatencySamples);
        long readyLatencyMaxNanoseconds =
            TicksToNanoseconds(Volatile.Read(ref _readyLatencyMaxTicks));

        return new JobRuntimeStats(
            scheduledJobs,
            executedWorkItems,
            completedHandles,
            faultedWorkItems,
            waitedCompletes,
            queuedWorkItems,
            localQueuedWorkItems,
            stolenWorkItems,
            refFreeJobs,
            refContainingJobs,
            managedPayloadWarnings,
            resourceConflictChecks,
            resourceConflictCheckSteps,
            readyTicketsPublished,
            readyTicketsExecuted,
            workerSpinWaits,
            workerParks,
            workerWakeups,
            readyLatencyAverageNanoseconds,
            readyLatencyMaxNanoseconds,
            Volatile.Read(ref _queueHighWater),
            Volatile.Read(ref _completionStateHighWater),
            Volatile.Read(ref _resourceStateHighWater));
    }

    private CounterShard? GetShard()
    {
        if (!_enabled)
            return null;

        CounterShardRegistration? registration = s_threadShard;
        if (registration is not null && ReferenceEquals(registration.Owner, this))
            return registration.Shard;

        var shard = new CounterShard();
        lock (_shardLock)
        {
            _shards.Add(shard);
        }

        s_threadShard = new CounterShardRegistration(this, shard);
        return shard;
    }

    private void SetHighWater(ref int target, int value)
    {
        if (!_enabled)
        {
            return;
        }

        while (true)
        {
            int current = Volatile.Read(ref target);
            if (value <= current)
            {
                return;
            }

            if (Interlocked.CompareExchange(ref target, value, current) == current)
            {
                return;
            }
        }
    }

    private void SetHighWater(ref long target, long value)
    {
        if (!_enabled)
            return;

        while (true)
        {
            long current = Volatile.Read(ref target);
            if (value <= current)
                return;
            if (Interlocked.CompareExchange(ref target, value, current) == current)
                return;
        }
    }

    private static long TicksToNanoseconds(long ticks)
    {
        if (ticks <= 0)
            return 0;
        return (long)(ticks * (1_000_000_000d / Stopwatch.Frequency));
    }

    private sealed class CounterShardRegistration
    {
        internal CounterShardRegistration(RuntimeCounters owner, CounterShard shard)
        {
            Owner = owner;
            Shard = shard;
        }

        internal RuntimeCounters Owner { get; }

        internal CounterShard Shard { get; }
    }

    private sealed class CounterShard
    {
        internal long ScheduledJobs;
        internal long ExecutedWorkItems;
        internal long CompletedHandles;
        internal long FaultedWorkItems;
        internal long WaitedCompletes;
        internal long QueuedWorkItems;
        internal long LocalQueuedWorkItems;
        internal long StolenWorkItems;
        internal long RefFreeJobs;
        internal long RefContainingJobs;
        internal long ManagedPayloadWarnings;
        internal long ResourceConflictChecks;
        internal long ResourceConflictCheckSteps;
        internal long ReadyTicketsPublished;
        internal long ReadyTicketsExecuted;
        internal long WorkerSpinWaits;
        internal long WorkerParks;
        internal long WorkerWakeups;
        internal long ReadyLatencyTicks;
        internal long ReadyLatencySamples;
    }
}



