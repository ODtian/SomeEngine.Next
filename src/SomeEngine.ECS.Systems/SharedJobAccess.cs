using SomeEngine.ECS.Components;
using SomeEngine.ECS.Registry;
using SomeEngine.Job;

namespace SomeEngine.ECS.Systems;

/// <summary>
/// Per-World, per-component capabilities for reading materialized shared-component values.
/// Shared membership and precomputed shared-index filters remain topology-only operations.
/// </summary>
public static class SharedJobAccess<T>
    where T : struct, ISharedComponent
{
    /// <summary>Declares read access to materialized values of this shared component type.</summary>
    public static JobResourceAccess Read(World world)
    {
        ArgumentNullException.ThrowIfNull(world);
        JobStorageTypeMetadata<T>.RequireAliasFree("Shared-component");
        return WorldStorageJobResources.Read(
            world,
            new WorldStorageResourceKey(WorldStorageKind.Shared, ComponentMetadata<T>.Id));
    }

    /// <summary>
    /// Schedules one owner that may materialize this shared component value through
    /// <see cref="World.GetShared{T}"/> or a generic dynamic shared-value query filter.
    /// </summary>
    public static JobHandle ScheduleRead<TJob>(
        World world,
        in TJob job,
        JobHandle dependency = default)
        where TJob : struct, IJob =>
        WorldStorageJobSchedule.ScheduleTopologyRead(
            world,
            Read(world),
            in job,
            dependency);

    /// <inheritdoc cref="ScheduleRead{TJob}(World, in TJob, JobHandle)"/>
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
}
