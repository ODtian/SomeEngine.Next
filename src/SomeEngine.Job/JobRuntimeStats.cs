namespace SomeEngine.Job;

public readonly struct JobRuntimeStats
{
    internal JobRuntimeStats(
        long scheduledJobs,
        long executedWorkItems,
        long completedHandles,
        long faultedWorkItems,
        long waitedCompletes,
        long queuedWorkItems,
        long localQueuedWorkItems,
        long stolenWorkItems,
        long refFreeJobs,
        long refContainingJobs,
        long managedPayloadWarnings,
        long resourceConflictChecks,
        long resourceConflictCheckSteps,
        int queueHighWater,
        int completionStateHighWater,
        int resourceStateHighWater)
    {
        ScheduledJobs = scheduledJobs;
        ExecutedWorkItems = executedWorkItems;
        CompletedHandles = completedHandles;
        FaultedWorkItems = faultedWorkItems;
        WaitedCompletes = waitedCompletes;
        QueuedWorkItems = queuedWorkItems;
        LocalQueuedWorkItems = localQueuedWorkItems;
        StolenWorkItems = stolenWorkItems;
        RefFreeJobs = refFreeJobs;
        RefContainingJobs = refContainingJobs;
        ManagedPayloadWarnings = managedPayloadWarnings;
        ResourceConflictChecks = resourceConflictChecks;
        ResourceConflictCheckSteps = resourceConflictCheckSteps;
        QueueHighWater = queueHighWater;
        CompletionStateHighWater = completionStateHighWater;
        ResourceStateHighWater = resourceStateHighWater;
    }

    public long ScheduledJobs { get; }

    public long ExecutedWorkItems { get; }

    public long CompletedHandles { get; }

    public long FaultedWorkItems { get; }

    public long WaitedCompletes { get; }

    public long QueuedWorkItems { get; }

    public long LocalQueuedWorkItems { get; }

    public long StolenWorkItems { get; }

    public long RefFreeJobs { get; }

    public long RefContainingJobs { get; }

    public long ManagedPayloadWarnings { get; }

    public long ResourceConflictChecks { get; }

    public long ResourceConflictCheckSteps { get; }

    public int QueueHighWater { get; }

    public int CompletionStateHighWater { get; }

    public int ResourceStateHighWater { get; }
}



