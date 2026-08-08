using SomeEngine.Job;

namespace SomeEngine.ECS.Systems;

/// <summary>
/// Centralizes the two-resource admission shape shared by typed World-storage jobs. Public
/// storage adapters stay distinct so their component constraints and runtime invariants remain
/// visible, while the scheduler-facing resource pair has one implementation.
/// </summary>
internal static class WorldStorageJobSchedule
{
    internal static JobHandle ScheduleTopologyRead<TJob>(
        World world,
        JobResourceAccess storage,
        in TJob job,
        JobHandle dependency = default)
        where TJob : struct, IJob
    {
        ArgumentNullException.ThrowIfNull(world);
        return Schedule(
            storage,
            RelationshipJobAccess.TopologyRead(world),
            in job,
            dependency);
    }

    internal static JobHandle ScheduleTopologyRead<TJob>(
        World world,
        JobResourceAccess storage,
        in TJob job,
        JobScheduleOptions options,
        JobHandle dependency = default)
        where TJob : struct, IJob
    {
        ArgumentNullException.ThrowIfNull(world);
        return Schedule(
            storage,
            RelationshipJobAccess.TopologyRead(world),
            in job,
            options,
            dependency);
    }

    internal static JobHandle ScheduleTopologyWrite<TJob>(
        World world,
        JobResourceAccess storage,
        in TJob job,
        JobHandle dependency = default)
        where TJob : struct, IJob
    {
        ArgumentNullException.ThrowIfNull(world);
        return Schedule(
            storage,
            RelationshipJobAccess.TopologyWrite(world),
            in job,
            dependency);
    }

    internal static JobHandle ScheduleTopologyWrite<TJob>(
        World world,
        JobResourceAccess storage,
        in TJob job,
        JobScheduleOptions options,
        JobHandle dependency = default)
        where TJob : struct, IJob
    {
        ArgumentNullException.ThrowIfNull(world);
        return Schedule(
            storage,
            RelationshipJobAccess.TopologyWrite(world),
            in job,
            options,
            dependency);
    }

    private static JobHandle Schedule<TJob>(
        JobResourceAccess storage,
        JobResourceAccess topology,
        in TJob job,
        JobHandle dependency)
        where TJob : struct, IJob
    {
        Span<JobResourceAccess> accesses = stackalloc JobResourceAccess[2];
        accesses[0] = storage;
        accesses[1] = topology;
        return JobSystem.Schedule(job, accesses, dependency);
    }

    private static JobHandle Schedule<TJob>(
        JobResourceAccess storage,
        JobResourceAccess topology,
        in TJob job,
        JobScheduleOptions options,
        JobHandle dependency)
        where TJob : struct, IJob
    {
        Span<JobResourceAccess> accesses = stackalloc JobResourceAccess[2];
        accesses[0] = storage;
        accesses[1] = topology;
        return JobSystem.Schedule(job, accesses, options, dependency);
    }
}
