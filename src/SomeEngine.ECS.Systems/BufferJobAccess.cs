using System.Runtime.CompilerServices;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Registry;
using SomeEngine.Job;

namespace SomeEngine.ECS.Systems;

/// <summary>
/// Element-type-qualified resource declarations and owner-bound scheduling for dynamic buffers.
/// </summary>
/// <remarks>
/// A buffer job is also a World topology reader because the entity row containing the header and
/// inline storage must not move while the callback borrows it. The typed schedule methods declare
/// both resources automatically; <see cref="World.ExecuteBufferRead{T}"/> and
/// <see cref="World.ExecuteBufferWrite{T}"/> verify them when the job executes.
/// </remarks>
public static class BufferJobAccess<T>
    where T : struct, IBufferElement
{
    /// <summary>Declares read access to all buffers of this element type in a World.</summary>
    public static JobResourceAccess Read(World world)
    {
        ArgumentNullException.ThrowIfNull(world);
        JobStorageTypeMetadata<T>.RequireAliasFree("Buffer-element");
        return WorldStorageJobResources.Read(
            world,
            new WorldStorageResourceKey(WorldStorageKind.Buffer, BufferComponents.Header<T>()));
    }

    /// <summary>Declares write access to all buffers of this element type in a World.</summary>
    public static JobResourceAccess Write(World world)
    {
        ArgumentNullException.ThrowIfNull(world);
        JobStorageTypeMetadata<T>.RequireAliasFree("Buffer-element");
        return WorldStorageJobResources.Write(
            world,
            new WorldStorageResourceKey(WorldStorageKind.Buffer, BufferComponents.Header<T>()));
    }

    /// <summary>
    /// Schedules one owner that can call <see cref="World.ExecuteBufferRead{T}"/> for this World
    /// and element type.
    /// </summary>
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

    /// <summary>
    /// Schedules one owner that can call <see cref="World.ExecuteBufferWrite{T}"/> for this World
    /// and element type.
    /// </summary>
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

    /// <inheritdoc cref="ScheduleWrite{TJob}(World, in TJob, JobHandle)"/>
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

internal static class WorldStorageJobResources
{
    internal static WorldJobs RequireJobs(World world)
    {
        ArgumentNullException.ThrowIfNull(world);
        return world.Execution as WorldJobs
            ?? throw new InvalidOperationException(
                "This World is synchronous. Supply WorldJobs when creating it to use scheduled ECS access.");
    }

    internal static JobResourceAccess Read(World world, WorldStorageResourceKey storage) =>
        JobResourceAccess.Read(RequireJobs(world).Resource(storage));

    internal static JobResourceAccess Write(World world, WorldStorageResourceKey storage) =>
        JobResourceAccess.Write(RequireJobs(world).Resource(storage));

    internal static JobResourceAccess Read(World world, WorldStorageResourceKey storage, long start, long length) =>
        JobResourceAccess.Read(RequireJobs(world).Resource(storage), start, length);

    internal static JobResourceAccess Write(World world, WorldStorageResourceKey storage, long start, long length) =>
        JobResourceAccess.Write(RequireJobs(world).Resource(storage), start, length);

    internal static bool TryDescribe(World world, JobResourceAccess access, out WorldStorageResourceKey storage) =>
        RequireJobs(world).TryDescribe(access, out storage);
}

internal readonly record struct WorldStorageResourceKey(WorldStorageKind Kind, int ComponentId);
