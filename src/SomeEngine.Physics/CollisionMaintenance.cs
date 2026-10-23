using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Relations;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;

namespace SomeEngine.Physics;

public static class CollisionMaintenanceQueries
{
    public static CollisionMaintenance<TGeometry, TBoundsReader, TFilter, TGroupResults>
        CreateMaintenance<TGeometry, TBoundsReader, TFilter, TGroupResults>(
            this RelationQuery<SpatialCandidates<TGeometry, TBoundsReader, TFilter, TGroupResults>, (Entity, Entity)> query,
            Scheduler scheduler)
        where TGeometry : struct
        where TBoundsReader : struct, ICollisionBoundsReader<TGeometry>
        where TFilter : struct, ISpatialFilter<TGroupResults>
        where TGroupResults : unmanaged => new(query.Enumerable, scheduler);

    public static CollisionMaintenance<TGeometry, TBoundsReader, SpatialAll, GroupMatch>
        CreateMaintenance<TGeometry, TBoundsReader>(
            this RelationQuery<CollisionCandidates<TGeometry, TBoundsReader>, (Entity, Entity)> query,
            Scheduler scheduler)
        where TGeometry : struct
        where TBoundsReader : struct, ICollisionBoundsReader<TGeometry> =>
        new(new SpatialCandidates<TGeometry, TBoundsReader, SpatialAll, GroupMatch>(query.Enumerable, default), scheduler);
}

public readonly struct SpatialAll : ISpatialFilter<GroupMatch>
{
    public QueryDefinition AddReads(QueryDefinition definition) => definition;
    public void RebuildSummaries(QueryCursor access) { }
    public void RefitSummary(int node, QueryCursor access) { }
    public GroupMatch MatchGroups(int firstNode, int secondNode, in GroupMatch inherited, out GroupMatch results) =>
        results = GroupMatch.All;
    public bool MatchesLeaves(QueryCursor access, int firstNode, int secondNode, in GroupMatch results) => true;
}

/// <summary>Maintains one collision tree and its concrete filter summaries from World changes.</summary>
public sealed class CollisionMaintenance<TGeometry, TBoundsReader, TFilter, TGroupResults> : IDisposable
    where TGeometry : struct
    where TBoundsReader : struct, ICollisionBoundsReader<TGeometry>
    where TFilter : struct, ISpatialFilter<TGroupResults>
    where TGroupResults : unmanaged
{
    private readonly EntityTableChanges _tableChanges;
    private readonly Scheduler _scheduler;
    private readonly QueryHandle _members;
    private readonly QueryHandle _access;
    private readonly JobResourceToken _indexResource;
    private readonly JobResourceAccess[] _updateAccesses;

    public CollisionMaintenance(SpatialCandidates<TGeometry, TBoundsReader, TFilter, TGroupResults> candidates,
        Scheduler scheduler)
    {
        _scheduler = scheduler;
        var maintained = new SpatialCandidates<TGeometry, TBoundsReader, TFilter, TGroupResults>(
            candidates.Source.WithTree(), candidates.Filter);
        Query = new(maintained);
        World world = maintained.World;
        _tableChanges = world.ObserveEntityTables();
        QueryDefinition members = maintained.AddReads(world.GetQueryDefinition(maintained.GetQuery(0)), 0);
        QueryDefinition access = maintained.AddReads(QueryDefinition.Empty.WithAccessFrom(members), 0);
        _members = world.Query(members);
        _access = world.Query(access);
        _indexResource = scheduler.CreateResourceToken("Collision tree and summaries");
        _updateAccesses = new JobResourceAccess[QueryJobAccess.Count(access) + 1];
        QueryJobAccess.CopyAccesses(world, access, _updateAccesses);
        _updateAccesses[^1] = JobResourceAccess.Write(_indexResource);
    }

    public RelationQuery<SpatialCandidates<TGeometry, TBoundsReader, TFilter, TGroupResults>, (Entity, Entity)> Query { get; }
    public CollisionTree Tree => Query.Enumerable.Tree;
    public uint LastSystemVersion { get; private set; }
    public int AccessCount => _updateAccesses.Length;
    public JobResourceAccess ReadAccess => JobResourceAccess.Read(_indexResource);

    /// <summary>Copies the component/topology reads and this maintained index's read access.</summary>
    public int CopyReadAccesses(Span<JobResourceAccess> destination)
    {
        _updateAccesses.AsSpan(0, _updateAccesses.Length - 1).CopyTo(destination);
        destination[_updateAccesses.Length - 1] = ReadAccess;
        return _updateAccesses.Length;
    }

    public JobHandle Schedule(JobHandle dependency = default)
    {
        var job = new MaintenanceJob(this);
        return _scheduler.Schedule(job, _updateAccesses, dependency);
    }

    /// <summary>Schedules a reader of this frame's components and maintained index.</summary>
    public JobHandle ScheduleRead<TJob>(in TJob job, ReadOnlySpan<JobResourceAccess> additionalAccesses,
        JobHandle dependency = default)
        where TJob : struct, IJob
    {
        Span<JobResourceAccess> accesses = stackalloc JobResourceAccess[AccessCount + additionalAccesses.Length];
        CopyReadAccesses(accesses);
        additionalAccesses.CopyTo(accesses[AccessCount..]);
        return _scheduler.Schedule(job, accesses, dependency);
    }

    private void Update()
    {
        EntityTableChangeBatch changes = _tableChanges.Read();
        var execution = new AccessExecution(this, changes);
        Query.Enumerable.World.ExecuteQuery(_access, LastSystemVersion, ref execution);
        _tableChanges.Accept(changes);
        LastSystemVersion = execution.CurrentSystemVersion;
    }

    private void Reconcile(Entity entity, QueryCursor members, QueryCursor access)
    {
        if (members.TryGetRow(entity, out _) && access.TryGetRow(entity, out var row) && row.Has<TGeometry>())
        {
            CollisionBounds bounds = Query.Enumerable.Source.ReadBounds(row);
            if (Tree.TryGetLeaf(entity, out int leaf))
                Tree.SetBounds(leaf, in bounds);
            else
                leaf = Tree.Insert(entity, in bounds);
            Refit(leaf, access);
        }
        else if (Tree.TryGetLeaf(entity, out _))
        {
            Refit(Tree.Remove(entity), access);
        }
    }

    private void Refit(int node, QueryCursor access)
    {
        TFilter filter = Query.Enumerable.Filter;
        while (node >= 0)
        {
            Tree.Refit(node);
            filter.RefitSummary(node, access);
            node = Tree.Nodes[node].Parent;
        }
    }

    public void Dispose()
    {
        _tableChanges.Dispose();
        Query.Enumerable.World.ReleaseQuery(_access);
        Query.Enumerable.World.ReleaseQuery(_members);
        _scheduler.ReleaseResourceToken(_indexResource);
    }

    private readonly struct MaintenanceJob(CollisionMaintenance<TGeometry, TBoundsReader, TFilter, TGroupResults> maintenance) : IJob
    {
        public void Execute() => maintenance.Update();
    }

    private struct AccessExecution(CollisionMaintenance<TGeometry, TBoundsReader, TFilter, TGroupResults> maintenance,
        EntityTableChangeBatch changes) : IQueryExecution
    {
        public uint CurrentSystemVersion;

        public void Execute(QueryCursor access)
        {
            var execution = new MemberExecution(maintenance, changes, access);
            maintenance.Query.Enumerable.World.ExecuteQuery(maintenance._members, maintenance.LastSystemVersion, ref execution);
            CurrentSystemVersion = access.CurrentSystemVersion;
        }
    }

    private readonly ref struct MemberExecution(CollisionMaintenance<TGeometry, TBoundsReader, TFilter, TGroupResults> maintenance,
        EntityTableChangeBatch changes, QueryCursor access) : IQueryExecution
    {
        private readonly QueryCursor _access = access;

        public void Execute(QueryCursor members)
        {
            foreach (Entity entity in changes.Entities)
                maintenance.Reconcile(entity, members, _access);
            foreach (QueryChunkView chunk in members.ChangedChunks(changes))
            {
                if (!chunk.Has<TGeometry>())
                    continue;
                bool geometryChanged = chunk.HasChangedSinceLastSystemVersion<TGeometry>();
                foreach (int row in chunk.RowIndices)
                {
                    if (geometryChanged && chunk.RowChangedSinceLastSystemVersion<TGeometry>(row) ||
                        maintenance.Query.Enumerable.Filter.HasSummaryChange(chunk, row))
                        maintenance.Reconcile(chunk.GetEntity(row), members, _access);
                }
            }
        }
    }
}
