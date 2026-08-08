using SomeEngine.ECS.Components;
using IComponent = global::SomeEngine.ECS.IComponent;
using SomeEngine.ECS.Registry;
using SomeEngine.Job;

namespace SomeEngine.ECS.Systems;

/// <summary>
/// Per-World, per-component value-storage capabilities for ordinary table components. Every
/// scheduled owner also takes topology-read access so entity rows cannot be relocated while the
/// component storage is borrowed.
/// </summary>
public static class ComponentJobAccess<T>
    where T : struct, IComponent
{
    public static JobResourceAccess Read(World world)
    {
        ArgumentNullException.ThrowIfNull(world);
        JobStorageTypeMetadata<T>.RequireAliasFree("Table-component");
        ComponentRegistry.MarkJobAliasFree(ComponentMetadata<T>.Id);
        return WorldStorageJobResources.Read(
            world,
            new WorldStorageResourceKey(WorldStorageKind.Table, ComponentMetadata<T>.Id));
    }

    public static JobResourceAccess Write(World world)
    {
        ArgumentNullException.ThrowIfNull(world);
        JobStorageTypeMetadata<T>.RequireAliasFree("Table-component");
        ComponentRegistry.MarkJobAliasFree(ComponentMetadata<T>.Id);
        return WorldStorageJobResources.Write(
            world,
            new WorldStorageResourceKey(WorldStorageKind.Table, ComponentMetadata<T>.Id));
    }

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

    public static JobHandle ScheduleWrite<TJob>(
        World world,
        in TJob job,
        JobHandle dependency = default)
        where TJob : struct, IJob =>
        WorldStorageJobSchedule.ScheduleTopologyRead(
            world,
            Write(world),
            in job,
            dependency);

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
}
