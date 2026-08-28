namespace SomeEngine.Job;

public readonly struct JobScheduleOptions
{
    /// <summary>
    /// Pass as the batch-size argument to opt into bounded per-job automatic batch sizing.
    /// Positive batch sizes always remain authoritative.
    /// </summary>
    public const int AutomaticBatchSize = -1;

    public static JobScheduleOptions Default => default;

    public JobScheduleOptions(JobPriority priority)
    {
        Priority = priority;
    }

    public JobPriority Priority { get; init; }
}



