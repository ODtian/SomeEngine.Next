using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Hierarchy;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Registry;
using SomeEngine.Job;

namespace SomeEngine.ECS.Systems;

/// <summary>
/// Domain-qualified resource declarations for scheduled hierarchy work.
/// </summary>
/// <remarks>
/// These declarations order jobs that touch the same World and hierarchy domain. The owner-bound
/// operations on this type verify the declarations against the currently executing job. They do
/// not make a Children read fresh: a fresh reader must also depend explicitly on the maintenance
/// handle.
/// </remarks>
public static class HierarchyJobAccess<TDomain>
    where TDomain : IHierarchyDomain
{
    /// <summary>
    /// Declares a deferred writer of canonical <see cref="Parent{TDomain}"/> values.
    /// </summary>
    public static JobResourceAccess ParentWrite(World world)
    {
        return WorldStorageJobResources.Write(
            world,
            new WorldStorageResourceKey(
                WorldStorageKind.Table,
                ComponentMetadata<Parent<TDomain>>.Id));
    }

    /// <summary>
    /// Declares a read of canonical <see cref="Parent{TDomain}"/> values.
    /// </summary>
    public static JobResourceAccess ParentRead(World world)
    {
        return WorldStorageJobResources.Read(
            world,
            new WorldStorageResourceKey(
                WorldStorageKind.Table,
                ComponentMetadata<Parent<TDomain>>.Id));
    }

    /// <summary>
    /// Schedules one owner that can read canonical Parent components. The shared topology read is
    /// required because another domain's writer can relocate the same entity between archetypes.
    /// </summary>
    public static JobHandle ScheduleParentRead<TJob>(
        World world,
        in TJob job,
        JobHandle dependency = default)
        where TJob : struct, IJob
        => WorldStorageJobSchedule.ScheduleTopologyRead(
            world,
            ParentRead(world),
            in job,
            dependency);

    /// <summary>
    /// Schedules one owner that can read canonical Parent components with explicit scheduling
    /// options.
    /// </summary>
    public static JobHandle ScheduleParentRead<TJob>(
        World world,
        in TJob job,
        JobScheduleOptions options,
        JobHandle dependency = default)
        where TJob : struct, IJob
        => WorldStorageJobSchedule.ScheduleTopologyRead(
            world,
            ParentRead(world),
            in job,
            options,
            dependency);

    /// <summary>
    /// Schedules one serial owner that can perform deferred Parent writes through this type.
    /// </summary>
    public static JobHandle ScheduleParentWrite<TJob>(
        World world,
        in TJob job,
        JobHandle dependency = default)
        where TJob : struct, IJob
        => WorldStorageJobSchedule.ScheduleTopologyWrite(
            world,
            ParentWrite(world),
            in job,
            dependency);

    /// <summary>
    /// Schedules one serial owner that can perform deferred Parent writes through this type.
    /// </summary>
    public static JobHandle ScheduleParentWrite<TJob>(
        World world,
        in TJob job,
        JobScheduleOptions options,
        JobHandle dependency = default)
        where TJob : struct, IJob
        => WorldStorageJobSchedule.ScheduleTopologyWrite(
            world,
            ParentWrite(world),
            in job,
            options,
            dependency);

    /// <summary>
    /// Schedules one serial work item that visits matching chunks and lends their writable
    /// <see cref="Parent{TDomain}"/> spans to <paramref name="job"/>.
    /// </summary>
    /// <remarks>
    /// The query must declare writable Parent access and must be usable as a whole-chunk query.
    /// Existing query guards reject read-only, missing, optional-nonmatching, and row-filtered
    /// shapes before a span is lent. The complete callback is one relationship owner scope, so
    /// final forest validation either commits every chunk write or rolls all of them back.
    /// </remarks>
    public static JobHandle ScheduleParentWriteChunks<TJob>(
        World world,
        QueryHandle query,
        in TJob job,
        JobHandle dependency = default)
        where TJob : struct, IParentWriteChunkJob<TDomain>
    {
        ArgumentNullException.ThrowIfNull(world);
        RelationshipChunkQueryGuards.RequireWholeChunkWrite<Parent<TDomain>>(world, query);
        var adapter = new ParentChunkJobAdapter<TJob>(world, query, job);
        return ScheduleParentWrite(world, adapter, dependency);
    }

    /// <inheritdoc cref="ScheduleParentWriteChunks{TJob}(World, QueryHandle, in TJob, JobHandle)"/>
    public static JobHandle ScheduleParentWriteChunks<TJob>(
        World world,
        QueryHandle query,
        in TJob job,
        JobScheduleOptions options,
        JobHandle dependency = default)
        where TJob : struct, IParentWriteChunkJob<TDomain>
    {
        ArgumentNullException.ThrowIfNull(world);
        RelationshipChunkQueryGuards.RequireWholeChunkWrite<Parent<TDomain>>(world, query);
        var adapter = new ParentChunkJobAdapter<TJob>(world, query, job);
        return ScheduleParentWrite(world, adapter, options, dependency);
    }

    /// <summary>
    /// Schedules one serial work item that visits matching chunks and lends their read-only
    /// <see cref="Parent{TDomain}"/> spans to <paramref name="job"/>.
    /// </summary>
    /// <remarks>
    /// The query must declare read-only Parent access and must be usable as a whole-chunk query.
    /// Existing query/admission guards reject writable, missing, optional-nonmatching, and row-filtered
    /// shapes before a span is lent.
    /// </remarks>
    public static JobHandle ScheduleParentReadChunks<TJob>(
        World world,
        QueryHandle query,
        in TJob job,
        JobHandle dependency = default)
        where TJob : struct, IParentReadChunkJob<TDomain>
    {
        ArgumentNullException.ThrowIfNull(world);
        RelationshipChunkQueryGuards.RequireWholeChunkRead<Parent<TDomain>>(world, query);
        var adapter = new ParentReadChunkJobAdapter<TJob>(world, query, job);
        return ScheduleParentRead(world, adapter, dependency);
    }

    /// <inheritdoc cref="ScheduleParentReadChunks{TJob}(World, QueryHandle, in TJob, JobHandle)"/>
    public static JobHandle ScheduleParentReadChunks<TJob>(
        World world,
        QueryHandle query,
        in TJob job,
        JobScheduleOptions options,
        JobHandle dependency = default)
        where TJob : struct, IParentReadChunkJob<TDomain>
    {
        ArgumentNullException.ThrowIfNull(world);
        RelationshipChunkQueryGuards.RequireWholeChunkRead<Parent<TDomain>>(world, query);
        var adapter = new ParentReadChunkJobAdapter<TJob>(world, query, job);
        return ScheduleParentRead(world, adapter, options, dependency);
    }

    public static void SetParentDeferred(World world, Entity child, Entity parent)
    {
        RequireParentWrite(world);
        Hierarchy<TDomain>.SetParentDeferred(world, child, parent);
    }

    public static void SetParentDeferred(
        World world,
        Entity child,
        Entity parent,
        int insertIndex)
    {
        RequireParentWrite(world);
        Hierarchy<TDomain>.SetParentDeferred(world, child, parent, insertIndex);
    }

    public static void DetachDeferred(World world, Entity child)
    {
        RequireParentWrite(world);
        Hierarchy<TDomain>.DetachDeferred(world, child);
    }

    public static Entity GetParent(World world, Entity child)
    {
        JobSystem.RequireCurrentAccess(ParentRead(world));
        JobSystem.RequireCurrentAccess(RelationshipJobAccess.TopologyRead(world));
        return Hierarchy<TDomain>.GetParent(world, child);
    }

    public static HierarchyChildrenSnapshot<TDomain> GetChildren(World world, Entity parent)
    {
        return Hierarchy<TDomain>.GetChildren(world, parent);
    }

    internal static void Maintain(World world)
    {
        JobSystem.RequireCurrentAccess(ParentWrite(world));
        RelationshipJobAccess.RequireTopologyWrite(world, requireSingleWorkItem: true);
        Hierarchy<TDomain>.Maintain(world);
    }

    private static void RequireParentWrite(World world)
    {
        JobSystem.RequireCurrentAccess(ParentWrite(world));
        RelationshipJobAccess.RequireTopologyWrite(world, requireSingleWorkItem: true);
    }

    private static void RequireParentRead(World world)
    {
        JobSystem.RequireCurrentAccess(ParentRead(world));
        JobSystem.RequireCurrentAccess(RelationshipJobAccess.TopologyRead(world));
    }

    private readonly struct ParentChunkJobAdapter<TJob> : IJob
        where TJob : struct, IParentWriteChunkJob<TDomain>
    {
        private readonly World _world;
        private readonly QueryHandle _query;
        private readonly TJob _job;

        internal ParentChunkJobAdapter(World world, QueryHandle query, in TJob job)
        {
            _world = world;
            _query = query;
            _job = job;
        }

        public void Execute()
        {
            RequireParentWrite(_world);
            TJob state = _job;
            _world.ExecuteQuery(
                _query,
                ref state,
                static (QueryCursor cursor, ref TJob chunkJob) =>
                {
                    foreach (QueryChunkView chunk in cursor.Chunks)
                    {
                        ReadOnlySpan<Entity> entities = chunk.Entities;
                        Span<Parent<TDomain>> parents = chunk.Write<Parent<TDomain>>();
                        chunkJob.Execute(entities, parents);
                    }
                });
        }
    }

    private readonly struct ParentReadChunkJobAdapter<TJob> : IJob
        where TJob : struct, IParentReadChunkJob<TDomain>
    {
        private readonly World _world;
        private readonly QueryHandle _query;
        private readonly TJob _job;

        internal ParentReadChunkJobAdapter(World world, QueryHandle query, in TJob job)
        {
            _world = world;
            _query = query;
            _job = job;
        }

        public void Execute()
        {
            RequireParentRead(_world);
            TJob state = _job;
            _world.ExecuteQuery(
                _query,
                ref state,
                static (QueryCursor cursor, ref TJob chunkJob) =>
                {
                    foreach (QueryChunkView chunk in cursor.Chunks)
                    {
                        ReadOnlySpan<Entity> entities = chunk.Entities;
                        ReadOnlySpan<Parent<TDomain>> parents = chunk.Read<Parent<TDomain>>();
                        chunkJob.Execute(entities, parents);
                    }
                });
        }
    }
}
