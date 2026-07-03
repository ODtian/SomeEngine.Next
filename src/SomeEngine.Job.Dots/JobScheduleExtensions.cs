namespace SomeEngine.Job;

public static class JobScheduleExtensions
{
    public static JobHandle Schedule<T>(this T job, JobHandle dependency = default)
        where T : struct, IJob
    {
        return JobSystem.Schedule(job, dependency);
    }

    public static JobHandle Schedule<T>(
        this T job,
        JobResourceAccess access,
        JobHandle dependency = default)
        where T : struct, IJob
    {
        return JobSystem.Schedule(job, access, dependency);
    }

    public static JobHandle Schedule<T>(
        this T job,
        ReadOnlySpan<JobResourceAccess> accesses,
        JobHandle dependency = default)
        where T : struct, IJob
    {
        return JobSystem.Schedule(job, accesses, dependency);
    }

    public static JobHandle ScheduleParallel<T>(
        this T job,
        int length,
        int batchSize,
        JobHandle dependency = default)
        where T : struct, IJobParallelFor
    {
        return JobSystem.ScheduleParallel(job, length, batchSize, dependency);
    }

    public static JobHandle ScheduleParallel<T>(
        this T job,
        int length,
        int batchSize,
        JobResourceAccess access,
        JobHandle dependency = default)
        where T : struct, IJobParallelFor
    {
        return JobSystem.ScheduleParallel(job, length, batchSize, access, dependency);
    }

    public static JobHandle ScheduleParallel<T>(
        this T job,
        int length,
        int batchSize,
        ReadOnlySpan<JobResourceAccess> accesses,
        JobHandle dependency = default)
        where T : struct, IJobParallelFor
    {
        return JobSystem.ScheduleParallel(job, length, batchSize, accesses, dependency);
    }
}

