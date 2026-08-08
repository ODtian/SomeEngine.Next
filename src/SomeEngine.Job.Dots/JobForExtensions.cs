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
        JobTraits.RequireSynchronousCallback<T, IJobFor>(
            JobForExecuteTraits<T>.IsAsyncStateMachine);
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
        JobTraits.RequireSynchronousCallback<T, IJobFor>(
            JobForExecuteTraits<T>.IsAsyncStateMachine);
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
        JobTraits.RequireSynchronousCallback<T, IJobFor>(
            JobForExecuteTraits<T>.IsAsyncStateMachine);
        return JobSystem.ScheduleParallel(new JobForParallelAdapter<T>(job), length, batchSize, accesses, dependency);
    }

    private static class JobForExecuteTraits<T>
        where T : struct, IJobFor
    {
        internal static readonly bool IsAsyncStateMachine = Create();

        private static bool Create()
        {
            T target = default;
            Action<int> callback = target.Execute;
            return JobTraits.IsAsyncCallback(callback.Method);
        }
    }
}

