namespace SomeEngine.Job;

public sealed class JobRuntimeConfig
{
    private const string WorkerCountMessage = "Worker count must be non-negative.";
    private const string QueuedWorkItemCapacityMessage = "Queued work item capacity must be positive.";
    private const string CompletionStateCapacityMessage = "Completion state capacity must be positive.";
    private const string ResourceStateCapacityMessage = "Resource state capacity must be positive.";
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
    }
}






