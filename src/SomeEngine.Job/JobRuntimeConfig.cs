namespace SomeEngine.Job;

public sealed class JobRuntimeConfig
{
    private const string WorkerCountMessage = "Worker count must be non-negative.";
    private const string QueuedWorkItemCapacityMessage = "Queued work item capacity must be positive.";
    private const string CompletionStateCapacityMessage = "Completion state capacity must be positive.";
    private const string ResourceStateCapacityMessage = "Resource state capacity must be positive.";
    private const string WorkerSpinCountMessage = "Worker spin count must be non-negative.";
    private const string BusyWorkerSpinCountMessage = "Busy worker spin count must not be smaller than worker spin count.";
    private const string AutoBatchTargetMessage = "Automatic batch target must be positive.";
    private const string AutoBatchTileLimitMessage = "Automatic batch tile limit must be positive.";
    public const int DefaultMaxQueuedWorkItems = 65_536;

    public const int DefaultMaxCompletionStates = 65_536;

    public const int DefaultMaxResourceStates = 16_384;

    public int WorkerCount { get; init; } = DefaultWorkerCount;

    public int MaxQueuedWorkItems { get; init; } = DefaultMaxQueuedWorkItems;

    public int MaxCompletionStates { get; init; } = DefaultMaxCompletionStates;

    public int MaxResourceStates { get; init; } = DefaultMaxResourceStates;

    public JobSafetyMode SafetyMode { get; init; } = JobSafetyMode.Checked;

    public ManagedPayloadPolicy ManagedPayloadPolicy { get; init; } = ManagedPayloadPolicy.Allow;

    public bool EnableCounters { get; init; } = true;

    /// <summary>Minimum bounded spin iterations before an idle worker parks.</summary>
    public int WorkerSpinCount { get; init; } = 128;

    /// <summary>Spin budget restored after a worker executes work; it decays back to the minimum.</summary>
    public int BusyWorkerSpinCount { get; init; } = 2_048;

    /// <summary>Target callback time used by per-job automatic parallel batch sizing.</summary>
    public int AutoBatchTargetMicroseconds { get; init; } = 100;

    /// <summary>Maximum automatic tile density; the scheduler always keeps at least four tiles per worker.</summary>
    public int AutoBatchMaxTilesPerWorker { get; init; } = 16;

    public static JobRuntimeConfig Default => new();

    internal static int DefaultWorkerCount => Math.Max(1, Environment.ProcessorCount - 1);

    internal void Validate()
    {
        if (WorkerCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(WorkerCount), WorkerCountMessage);
        }

        if (MaxQueuedWorkItems <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaxQueuedWorkItems),
                QueuedWorkItemCapacityMessage);
        }

        if (MaxCompletionStates <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaxCompletionStates),
                CompletionStateCapacityMessage);
        }

        if (MaxResourceStates <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaxResourceStates),
                ResourceStateCapacityMessage);
        }

        if (!Enum.IsDefined(SafetyMode))
        {
            throw new ArgumentOutOfRangeException(nameof(SafetyMode));
        }

        if (!Enum.IsDefined(ManagedPayloadPolicy))
        {
            throw new ArgumentOutOfRangeException(nameof(ManagedPayloadPolicy));
        }

        if (WorkerSpinCount < 0)
            throw new ArgumentOutOfRangeException(nameof(WorkerSpinCount), WorkerSpinCountMessage);

        if (BusyWorkerSpinCount < WorkerSpinCount)
            throw new ArgumentOutOfRangeException(nameof(BusyWorkerSpinCount), BusyWorkerSpinCountMessage);

        if (AutoBatchTargetMicroseconds <= 0)
            throw new ArgumentOutOfRangeException(nameof(AutoBatchTargetMicroseconds), AutoBatchTargetMessage);

        if (AutoBatchMaxTilesPerWorker <= 0)
            throw new ArgumentOutOfRangeException(nameof(AutoBatchMaxTilesPerWorker), AutoBatchTileLimitMessage);
    }
}






