using SomeEngine.ECS.Components;
using SomeEngine.ECS.Registry;
using SomeEngine.Job;

namespace SomeEngine.ECS.Systems;

/// <summary>
/// Component-type-qualified resource declarations and owner-bound scheduling for sparse storage.
/// </summary>
/// <remarks>
/// Sparse callbacks also read World topology because their dense entity identities must remain
/// alive and stable for the callback lifetime. The typed schedule methods declare both resources;
/// <see cref="World.ExecuteSparseRead{T}(SparseReadExecution{T})"/> and
/// <see cref="World.ExecuteSparseWrite{T}(SparseWriteExecution{T})"/> verify them in a Job.
/// </remarks>
public static class SparseJobAccess<T>
    where T : struct, ISparseComponent
{
    /// <summary>Declares read access to this sparse component type in a World.</summary>
    public static JobResourceAccess Read(World world)
    {
        ArgumentNullException.ThrowIfNull(world);
        JobStorageTypeMetadata<T>.RequireAliasFree("Sparse-component");
        return WorldStorageJobResources.Read(world, Key);
    }

    /// <summary>Declares write access to this sparse component type in a World.</summary>
    public static JobResourceAccess Write(World world)
    {
        ArgumentNullException.ThrowIfNull(world);
        JobStorageTypeMetadata<T>.RequireAliasFree("Sparse-component");
        return WorldStorageJobResources.Write(world, Key);
    }

    /// <summary>Schedules one owner that may read this sparse component type.</summary>
    public static JobHandle ScheduleRead<TJob>(
        World world,
        in TJob job,
        JobHandle dependency = default)
        where TJob : struct, IJob
        => WorldStorageJobSchedule.ScheduleTopologyRead(
            world,
            Read(world),
            in job,
            dependency);

    /// <summary>Schedules one owner that may read this sparse component type.</summary>
    public static JobHandle ScheduleRead<TJob>(
        World world,
        in TJob job,
        JobScheduleOptions options,
        JobHandle dependency = default)
        where TJob : struct, IJob
        => WorldStorageJobSchedule.ScheduleTopologyRead(
            world,
            Read(world),
            in job,
            options,
            dependency);

    /// <summary>Schedules one owner that may write this sparse component type.</summary>
    public static JobHandle ScheduleWrite<TJob>(
        World world,
        in TJob job,
        JobHandle dependency = default)
        where TJob : struct, IJob
        => WorldStorageJobSchedule.ScheduleTopologyRead(
            world,
            Write(world),
            in job,
            dependency);

    /// <summary>Schedules one owner that may write this sparse component type.</summary>
    public static JobHandle ScheduleWrite<TJob>(
        World world,
        in TJob job,
        JobScheduleOptions options,
        JobHandle dependency = default)
        where TJob : struct, IJob
        => WorldStorageJobSchedule.ScheduleTopologyRead(
            world,
            Write(world),
            in job,
            options,
            dependency);

    private static WorldStorageResourceKey Key =>
        new(WorldStorageKind.Sparse, ComponentMetadata<T>.Id);
}
