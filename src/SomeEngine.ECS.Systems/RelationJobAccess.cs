using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Relations;
using SomeEngine.ECS.Registry;
using SomeEngine.Job;

namespace SomeEngine.ECS.Systems;

/// <summary>
/// Payload-qualified resource declarations for scheduled relation topology work.
/// </summary>
/// <remarks>
/// These declarations order jobs that touch the same World and relation payload type. The
/// owner-bound operations on this type verify the declarations against the currently executing
/// job. They do not make an adjacency read fresh: a fresh reader must also depend explicitly on
/// the maintenance handle whose publication it needs to observe.
/// </remarks>
public static class RelationJobAccess<T>
    where T : struct, IComponent
{
    /// <summary>
    /// Declares a deferred writer of the canonical endpoints for relation payload <typeparamref name="T"/>.
    /// </summary>
    public static JobResourceAccess EndpointsWrite(World world)
    {
        return WorldStorageJobResources.Write(
            world,
            new WorldStorageResourceKey(WorldStorageKind.Table, EndpointComponentId));
    }

    /// <summary>
    /// Declares a read of canonical relation endpoints for payload <typeparamref name="T"/>.
    /// </summary>
    public static JobResourceAccess EndpointsRead(World world)
    {
        return WorldStorageJobResources.Read(
            world,
            new WorldStorageResourceKey(WorldStorageKind.Table, EndpointComponentId));
    }

    /// <summary>
    /// Schedules one owner that can read canonical endpoint components. The shared topology read
    /// prevents a hierarchy or another relation writer from relocating the edge row concurrently.
    /// </summary>
    public static JobHandle ScheduleEndpointsRead<TJob>(
        World world,
        in TJob job,
        JobHandle dependency = default)
        where TJob : struct, IJob
        => WorldStorageJobSchedule.ScheduleTopologyRead(
            world,
            EndpointsRead(world),
            in job,
            dependency);

    /// <summary>
    /// Schedules one owner that can read canonical endpoint components with explicit scheduling
    /// options.
    /// </summary>
    public static JobHandle ScheduleEndpointsRead<TJob>(
        World world,
        in TJob job,
        JobScheduleOptions options,
        JobHandle dependency = default)
        where TJob : struct, IJob
        => WorldStorageJobSchedule.ScheduleTopologyRead(
            world,
            EndpointsRead(world),
            in job,
            options,
            dependency);

    /// <summary>
    /// Schedules one serial owner that can perform deferred endpoint writes through this type.
    /// </summary>
    public static JobHandle ScheduleEndpointsWrite<TJob>(
        World world,
        in TJob job,
        JobHandle dependency = default)
        where TJob : struct, IJob
        => WorldStorageJobSchedule.ScheduleTopologyWrite(
            world,
            EndpointsWrite(world),
            in job,
            dependency);

    /// <summary>
    /// Schedules one serial owner that can perform deferred endpoint writes through this type.
    /// </summary>
    public static JobHandle ScheduleEndpointsWrite<TJob>(
        World world,
        in TJob job,
        JobScheduleOptions options,
        JobHandle dependency = default)
        where TJob : struct, IJob
        => WorldStorageJobSchedule.ScheduleTopologyWrite(
            world,
            EndpointsWrite(world),
            in job,
            options,
            dependency);

    /// <summary>
    /// Schedules one serial work item that lends writable directed endpoint chunks to
    /// <paramref name="job"/>.
    /// </summary>
    /// <remarks>
    /// The query must declare writable <see cref="DirectedRelationEndpoints{T}"/> access and
    /// must be usable as a whole-chunk query. Existing query guards reject incompatible shapes.
    /// All chunks share one owner scope, so relation invariants are validated against one final
    /// image and failure rolls every endpoint write back.
    /// </remarks>
    public static JobHandle ScheduleDirectedEndpointWriteChunks<TJob>(
        World world,
        QueryHandle query,
        in TJob job,
        JobHandle dependency = default)
        where TJob : struct, IDirectedRelationEndpointsWriteChunkJob<T>
    {
        ArgumentNullException.ThrowIfNull(world);
        RelationshipChunkQueryGuards.RequireWholeChunkWrite<DirectedRelationEndpoints<T>>(world, query);
        var adapter = new DirectedEndpointChunkJobAdapter<TJob>(world, query, job);
        return ScheduleEndpointsWrite(world, adapter, dependency);
    }

    /// <inheritdoc cref="ScheduleDirectedEndpointWriteChunks{TJob}(World, QueryHandle, in TJob, JobHandle)"/>
    public static JobHandle ScheduleDirectedEndpointWriteChunks<TJob>(
        World world,
        QueryHandle query,
        in TJob job,
        JobScheduleOptions options,
        JobHandle dependency = default)
        where TJob : struct, IDirectedRelationEndpointsWriteChunkJob<T>
    {
        ArgumentNullException.ThrowIfNull(world);
        RelationshipChunkQueryGuards.RequireWholeChunkWrite<DirectedRelationEndpoints<T>>(world, query);
        var adapter = new DirectedEndpointChunkJobAdapter<TJob>(world, query, job);
        return ScheduleEndpointsWrite(world, adapter, options, dependency);
    }

    /// <summary>
    /// Schedules one serial work item that lends writable undirected endpoint chunks to
    /// <paramref name="job"/>.
    /// </summary>
    /// <remarks>
    /// The query must declare writable <see cref="UndirectedRelationEndpoints{T}"/> access and
    /// must be usable as a whole-chunk query. Existing query guards reject incompatible shapes.
    /// All chunks share one owner scope, so relation invariants are validated against one final
    /// image and failure rolls every endpoint write back.
    /// </remarks>
    public static JobHandle ScheduleUndirectedEndpointWriteChunks<TJob>(
        World world,
        QueryHandle query,
        in TJob job,
        JobHandle dependency = default)
        where TJob : struct, IUndirectedRelationEndpointsWriteChunkJob<T>
    {
        ArgumentNullException.ThrowIfNull(world);
        RelationshipChunkQueryGuards.RequireWholeChunkWrite<UndirectedRelationEndpoints<T>>(world, query);
        var adapter = new UndirectedEndpointChunkJobAdapter<TJob>(world, query, job);
        return ScheduleEndpointsWrite(world, adapter, dependency);
    }

    /// <inheritdoc cref="ScheduleUndirectedEndpointWriteChunks{TJob}(World, QueryHandle, in TJob, JobHandle)"/>
    public static JobHandle ScheduleUndirectedEndpointWriteChunks<TJob>(
        World world,
        QueryHandle query,
        in TJob job,
        JobScheduleOptions options,
        JobHandle dependency = default)
        where TJob : struct, IUndirectedRelationEndpointsWriteChunkJob<T>
    {
        ArgumentNullException.ThrowIfNull(world);
        RelationshipChunkQueryGuards.RequireWholeChunkWrite<UndirectedRelationEndpoints<T>>(world, query);
        var adapter = new UndirectedEndpointChunkJobAdapter<TJob>(world, query, job);
        return ScheduleEndpointsWrite(world, adapter, options, dependency);
    }

    /// <summary>
    /// Schedules one serial work item that lends read-only directed endpoint chunks to
    /// <paramref name="job"/>.
    /// </summary>
    /// <remarks>The query must declare read-only directed endpoint access.</remarks>
    public static JobHandle ScheduleDirectedEndpointReadChunks<TJob>(
        World world,
        QueryHandle query,
        in TJob job,
        JobHandle dependency = default)
        where TJob : struct, IDirectedRelationEndpointsReadChunkJob<T>
    {
        ArgumentNullException.ThrowIfNull(world);
        RelationshipChunkQueryGuards.RequireWholeChunkRead<DirectedRelationEndpoints<T>>(world, query);
        var adapter = new DirectedEndpointReadChunkJobAdapter<TJob>(world, query, job);
        return ScheduleEndpointsRead(world, adapter, dependency);
    }

    /// <inheritdoc cref="ScheduleDirectedEndpointReadChunks{TJob}(World, QueryHandle, in TJob, JobHandle)"/>
    public static JobHandle ScheduleDirectedEndpointReadChunks<TJob>(
        World world,
        QueryHandle query,
        in TJob job,
        JobScheduleOptions options,
        JobHandle dependency = default)
        where TJob : struct, IDirectedRelationEndpointsReadChunkJob<T>
    {
        ArgumentNullException.ThrowIfNull(world);
        RelationshipChunkQueryGuards.RequireWholeChunkRead<DirectedRelationEndpoints<T>>(world, query);
        var adapter = new DirectedEndpointReadChunkJobAdapter<TJob>(world, query, job);
        return ScheduleEndpointsRead(world, adapter, options, dependency);
    }

    /// <summary>
    /// Schedules one serial work item that lends read-only undirected endpoint chunks to
    /// <paramref name="job"/>.
    /// </summary>
    /// <remarks>The query must declare read-only undirected endpoint access.</remarks>
    public static JobHandle ScheduleUndirectedEndpointReadChunks<TJob>(
        World world,
        QueryHandle query,
        in TJob job,
        JobHandle dependency = default)
        where TJob : struct, IUndirectedRelationEndpointsReadChunkJob<T>
    {
        ArgumentNullException.ThrowIfNull(world);
        RelationshipChunkQueryGuards.RequireWholeChunkRead<UndirectedRelationEndpoints<T>>(world, query);
        var adapter = new UndirectedEndpointReadChunkJobAdapter<TJob>(world, query, job);
        return ScheduleEndpointsRead(world, adapter, dependency);
    }

    /// <inheritdoc cref="ScheduleUndirectedEndpointReadChunks{TJob}(World, QueryHandle, in TJob, JobHandle)"/>
    public static JobHandle ScheduleUndirectedEndpointReadChunks<TJob>(
        World world,
        QueryHandle query,
        in TJob job,
        JobScheduleOptions options,
        JobHandle dependency = default)
        where TJob : struct, IUndirectedRelationEndpointsReadChunkJob<T>
    {
        ArgumentNullException.ThrowIfNull(world);
        RelationshipChunkQueryGuards.RequireWholeChunkRead<UndirectedRelationEndpoints<T>>(world, query);
        var adapter = new UndirectedEndpointReadChunkJobAdapter<TJob>(world, query, job);
        return ScheduleEndpointsRead(world, adapter, options, dependency);
    }

    public static void RetargetDeferred(
        World world,
        RelationEdge<T> edge,
        Entity first,
        Entity second)
    {
        RequireEndpointsWrite(world);
        world.RetargetRelationDeferred(edge, first, second);
    }

    public static void RetargetDeferred(
        World world,
        RelationEdge<T> edge,
        Entity source,
        Entity target,
        DirectedRelationPlacement placement)
    {
        RequireEndpointsWrite(world);
        world.RetargetRelationDeferred(edge, source, target, placement);
    }

    public static void RetargetDeferred(
        World world,
        RelationEdge<T> edge,
        Entity endpointA,
        Entity endpointB,
        UndirectedRelationPlacement placement)
    {
        RequireEndpointsWrite(world);
        world.RetargetRelationDeferred(edge, endpointA, endpointB, placement);
    }

    public static DirectedRelationEndpoints<T> GetDirectedEndpoints(
        World world,
        RelationEdge<T> edge)
    {
        JobSystem.RequireCurrentAccess(EndpointsRead(world));
        JobSystem.RequireCurrentAccess(RelationshipJobAccess.TopologyRead(world));
        return world.GetDirectedRelationEndpoints(edge);
    }

    public static UndirectedRelationEndpoints<T> GetUndirectedEndpoints(
        World world,
        RelationEdge<T> edge)
    {
        JobSystem.RequireCurrentAccess(EndpointsRead(world));
        JobSystem.RequireCurrentAccess(RelationshipJobAccess.TopologyRead(world));
        return world.GetUndirectedRelationEndpoints(edge);
    }

    public static RelationAdjacencySnapshot<T> GetOutgoing(World world, Entity source)
    {
        return world.GetOutgoingRelations<T>(source);
    }

    public static RelationAdjacencySnapshot<T> GetIncoming(World world, Entity target)
    {
        return world.GetIncomingRelations<T>(target);
    }

    public static RelationAdjacencySnapshot<T> GetIncident(World world, Entity endpoint)
    {
        return world.GetIncidentRelations<T>(endpoint);
    }

    internal static void Maintain(World world)
    {
        JobSystem.RequireCurrentAccess(EndpointsRead(world));
        RelationshipJobAccess.RequireTopologyWrite(world, requireSingleWorkItem: true);
        world.MaintainRelations<T>();
    }

    private static void RequireEndpointsWrite(World world)
    {
        JobSystem.RequireCurrentAccess(EndpointsWrite(world));
        RelationshipJobAccess.RequireTopologyWrite(world, requireSingleWorkItem: true);
    }

    private static void RequireEndpointsRead(World world)
    {
        JobSystem.RequireCurrentAccess(EndpointsRead(world));
        JobSystem.RequireCurrentAccess(RelationshipJobAccess.TopologyRead(world));
    }

    private static int EndpointComponentId =>
        RelationSchema.For<T>().Direction == RelationDirection.Directed
            ? ComponentMetadata<DirectedRelationEndpoints<T>>.Id
            : ComponentMetadata<UndirectedRelationEndpoints<T>>.Id;

    private readonly struct DirectedEndpointChunkJobAdapter<TJob> : IJob
        where TJob : struct, IDirectedRelationEndpointsWriteChunkJob<T>
    {
        private readonly World _world;
        private readonly QueryHandle _query;
        private readonly TJob _job;

        internal DirectedEndpointChunkJobAdapter(World world, QueryHandle query, in TJob job)
        {
            _world = world;
            _query = query;
            _job = job;
        }

        public void Execute()
        {
            RequireEndpointsWrite(_world);
            TJob state = _job;
            _world.ExecuteQuery(
                _query,
                ref state,
                static (QueryCursor cursor, ref TJob chunkJob) =>
                {
                    foreach (QueryChunkView chunk in cursor.Chunks)
                    {
                        ReadOnlySpan<Entity> entities = chunk.Entities;
                        Span<DirectedRelationEndpoints<T>> endpoints =
                            chunk.Write<DirectedRelationEndpoints<T>>();
                        chunkJob.Execute(entities, endpoints);
                    }
                });
        }
    }

    private readonly struct UndirectedEndpointChunkJobAdapter<TJob> : IJob
        where TJob : struct, IUndirectedRelationEndpointsWriteChunkJob<T>
    {
        private readonly World _world;
        private readonly QueryHandle _query;
        private readonly TJob _job;

        internal UndirectedEndpointChunkJobAdapter(World world, QueryHandle query, in TJob job)
        {
            _world = world;
            _query = query;
            _job = job;
        }

        public void Execute()
        {
            RequireEndpointsWrite(_world);
            TJob state = _job;
            _world.ExecuteQuery(
                _query,
                ref state,
                static (QueryCursor cursor, ref TJob chunkJob) =>
                {
                    foreach (QueryChunkView chunk in cursor.Chunks)
                    {
                        ReadOnlySpan<Entity> entities = chunk.Entities;
                        Span<UndirectedRelationEndpoints<T>> endpoints =
                            chunk.Write<UndirectedRelationEndpoints<T>>();
                        chunkJob.Execute(entities, endpoints);
                    }
                });
        }
    }

    private readonly struct DirectedEndpointReadChunkJobAdapter<TJob> : IJob
        where TJob : struct, IDirectedRelationEndpointsReadChunkJob<T>
    {
        private readonly World _world;
        private readonly QueryHandle _query;
        private readonly TJob _job;

        internal DirectedEndpointReadChunkJobAdapter(World world, QueryHandle query, in TJob job)
        {
            _world = world;
            _query = query;
            _job = job;
        }

        public void Execute()
        {
            RequireEndpointsRead(_world);
            TJob state = _job;
            _world.ExecuteQuery(
                _query,
                ref state,
                static (QueryCursor cursor, ref TJob chunkJob) =>
                {
                    foreach (QueryChunkView chunk in cursor.Chunks)
                    {
                        ReadOnlySpan<Entity> entities = chunk.Entities;
                        ReadOnlySpan<DirectedRelationEndpoints<T>> endpoints =
                            chunk.Read<DirectedRelationEndpoints<T>>();
                        chunkJob.Execute(entities, endpoints);
                    }
                });
        }
    }

    private readonly struct UndirectedEndpointReadChunkJobAdapter<TJob> : IJob
        where TJob : struct, IUndirectedRelationEndpointsReadChunkJob<T>
    {
        private readonly World _world;
        private readonly QueryHandle _query;
        private readonly TJob _job;

        internal UndirectedEndpointReadChunkJobAdapter(World world, QueryHandle query, in TJob job)
        {
            _world = world;
            _query = query;
            _job = job;
        }

        public void Execute()
        {
            RequireEndpointsRead(_world);
            TJob state = _job;
            _world.ExecuteQuery(
                _query,
                ref state,
                static (QueryCursor cursor, ref TJob chunkJob) =>
                {
                    foreach (QueryChunkView chunk in cursor.Chunks)
                    {
                        ReadOnlySpan<Entity> entities = chunk.Entities;
                        ReadOnlySpan<UndirectedRelationEndpoints<T>> endpoints =
                            chunk.Read<UndirectedRelationEndpoints<T>>();
                        chunkJob.Execute(entities, endpoints);
                    }
                });
        }
    }
}
