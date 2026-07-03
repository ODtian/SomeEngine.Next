namespace SomeEngine.Job;

public readonly struct JobScheduleOptions
{
    public static JobScheduleOptions Default => default;

    public JobScheduleOptions(JobPriority priority)
    {
        Priority = priority;
    }

    public JobPriority Priority { get; init; }
}



