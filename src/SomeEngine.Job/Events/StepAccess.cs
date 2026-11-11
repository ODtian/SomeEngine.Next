namespace SomeEngine.Job;

/// <summary>A producer's result completion and the resources read by its concrete view.</summary>
public readonly struct StepAccess(JobHandle completed, JobResourceAccess[] reads)
{
    public JobHandle Completed { get; } = completed;
    public ReadOnlySpan<JobResourceAccess> Reads => reads;
}
