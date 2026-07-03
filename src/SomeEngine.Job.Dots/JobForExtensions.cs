namespace SomeEngine.Job;

public static class JobForExtensions
{
    public static JobHandle Schedule<T>(
        this T job,
        int length,
        int batchSize,
        JobHandle dependency = default)
        where T : struct, IJobFor
    {
        return JobSystem.ScheduleParallel(new JobForParallelAdapter<T>(job), length, batchSize, dependency);
    }

    public static JobHandle Schedule<T>(
        this T job,
        int length,
        int batchSize,
        JobResourceAccess access,
        JobHandle dependency = default)
        where T : struct, IJobFor
    {
        return JobSystem.ScheduleParallel(new JobForParallelAdapter<T>(job), length, batchSize, access, dependency);
    }

    public static JobHandle Schedule<T>(
        this T job,
        int length,
        int batchSize,
        ReadOnlySpan<JobResourceAccess> accesses,
        JobHandle dependency = default)
        where T : struct, IJobFor
    {
        return JobSystem.ScheduleParallel(new JobForParallelAdapter<T>(job), length, batchSize, accesses, dependency);
    }
}

