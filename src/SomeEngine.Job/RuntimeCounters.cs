namespace SomeEngine.Job;

internal sealed class RuntimeCounters
{
    private readonly bool _enabled;
    private long _scheduledJobs;
    private long _executedWorkItems;
    private long _completedHandles;
    private long _faultedWorkItems;
    private long _waitedCompletes;
    private long _queuedWorkItems;
    private long _localQueuedWorkItems;
    private long _stolenWorkItems;
    private long _refFreeJobs;
    private long _refContainingJobs;
    private long _managedPayloadWarnings;
    private long _resourceConflictChecks;
    private long _resourceConflictCheckSteps;
    private int _queueHighWater;
    private int _completionStateHighWater;
    private int _resourceStateHighWater;

    internal RuntimeCounters(bool enabled)
    {
        _enabled = enabled;
    }

    internal void Scheduled(JobPayloadLane lane)
    {
        if (!_enabled)
        {
            return;
        }

        Interlocked.Increment(ref _scheduledJobs);
        if (lane == JobPayloadLane.RefFree)
        {
            Interlocked.Increment(ref _refFreeJobs);
        }
        else
        {
            Interlocked.Increment(ref _refContainingJobs);
        }
    }

    internal void Executed()
    {
        Increment(ref _executedWorkItems);
    }

    internal void Completed()
    {
        Increment(ref _completedHandles);
    }

    internal void Faulted()
    {
        Increment(ref _faultedWorkItems);
    }

    internal void Waited()
    {
        Increment(ref _waitedCompletes);
    }

    internal void Queued(int count, bool local)
    {
        if (!_enabled)
        {
            return;
        }

        Interlocked.Add(ref _queuedWorkItems, count);
        if (local)
        {
            Interlocked.Add(ref _localQueuedWorkItems, count);
        }
    }

    internal void Stolen()
    {
        Increment(ref _stolenWorkItems);
    }

    internal void ManagedPayloadWarning()
    {
        Increment(ref _managedPayloadWarnings);
    }

    internal void ResourceConflictCheck(int steps)
    {
        if (!_enabled)
        {
            return;
        }

        Interlocked.Increment(ref _resourceConflictChecks);
        Interlocked.Add(ref _resourceConflictCheckSteps, steps);
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
        return new JobRuntimeStats(
            Interlocked.Read(ref _scheduledJobs),
            Interlocked.Read(ref _executedWorkItems),
            Interlocked.Read(ref _completedHandles),
            Interlocked.Read(ref _faultedWorkItems),
            Interlocked.Read(ref _waitedCompletes),
            Interlocked.Read(ref _queuedWorkItems),
            Interlocked.Read(ref _localQueuedWorkItems),
            Interlocked.Read(ref _stolenWorkItems),
            Interlocked.Read(ref _refFreeJobs),
            Interlocked.Read(ref _refContainingJobs),
            Interlocked.Read(ref _managedPayloadWarnings),
            Interlocked.Read(ref _resourceConflictChecks),
            Interlocked.Read(ref _resourceConflictCheckSteps),
            Volatile.Read(ref _queueHighWater),
            Volatile.Read(ref _completionStateHighWater),
            Volatile.Read(ref _resourceStateHighWater));
    }

    private void Increment(ref long value)
    {
        if (_enabled)
        {
            Interlocked.Increment(ref value);
        }
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
}



