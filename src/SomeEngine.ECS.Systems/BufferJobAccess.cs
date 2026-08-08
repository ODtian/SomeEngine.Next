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
    private static readonly ConditionalWeakTable<World, WorldResources> s_resources = new();

    /// <summary>
    /// Installs synchronous World admission before a deferred launcher is visible to callers.
    /// Binding alone acquires no resource and therefore does not block semantic predecessors.
    /// </summary>
    internal static void Bind(World world)
    {
        ArgumentNullException.ThrowIfNull(world);
        world.BindJobAdmission(WorldJobAdmission.Instance);
    }

    internal static JobResourceAccess Read(World world, WorldStorageResourceKey storage) =>
        JobResourceAccess.Read(Key(world, storage));

    internal static JobResourceAccess Write(World world, WorldStorageResourceKey storage) =>
        JobResourceAccess.Write(Key(world, storage));

    internal static JobResourceAccess Read(
        World world,
        WorldStorageResourceKey storage,
        long start,
        long length) =>
        JobResourceAccess.Read(Key(world, storage), start, length);

    internal static JobResourceAccess Write(
        World world,
        WorldStorageResourceKey storage,
        long start,
        long length) =>
        JobResourceAccess.Write(Key(world, storage), start, length);

    internal static bool TryDescribe(
        World world,
        JobResourceAccess access,
        out WorldStorageResourceKey storage)
    {
        ArgumentNullException.ThrowIfNull(world);
        if (s_resources.TryGetValue(world, out WorldResources? resources))
            return resources.TryDescribe(access, out storage);

        storage = default;
        return false;
    }

    private static JobResourceKey Key(World world, WorldStorageResourceKey storage)
    {
        ArgumentNullException.ThrowIfNull(world);
        Bind(world);
        return s_resources
            .GetValue(world, static owner => new WorldResources(owner.JobSubmissionObserver))
            .Key(storage);
    }

    private sealed class WorldResources
    {
        private readonly IJobSubmissionObserver _submissionObserver;
        private readonly Lock _gate = new();
        private readonly Dictionary<WorldStorageResourceKey, JobResourceKey> _keys = new();

        internal WorldResources(IJobSubmissionObserver submissionObserver)
        {
            _submissionObserver = submissionObserver;
        }

        internal JobResourceKey Key(WorldStorageResourceKey storage)
        {
            lock (_gate)
            {
                if (_keys.TryGetValue(storage, out JobResourceKey? key))
                    return key;

                key = new JobResourceKey(_submissionObserver);
                _keys.Add(storage, key);
                return key;
            }
        }

        internal bool TryDescribe(
            JobResourceAccess access,
            out WorldStorageResourceKey storage)
        {
            lock (_gate)
            {
                foreach (var pair in _keys)
                {
                    JobResourceAccess known = JobResourceAccess.Read(pair.Value);
                    if (known.Kind == access.Kind &&
                        known.Id == access.Id &&
                        known.Version == access.Version &&
                        known.Generation == access.Generation)
                    {
                        storage = pair.Key;
                        return true;
                    }
                }
            }

            storage = default;
            return false;
        }
    }
}

internal readonly record struct WorldStorageResourceKey(
    WorldStorageKind Kind,
    int ComponentId);
