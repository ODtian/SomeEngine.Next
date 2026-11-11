using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Relations;
using SomeEngine.Job;

namespace SomeEngine.ECS.Spatial;

public static class SpatialMaintenanceQueries
{
    public static SpatialMaintenance<TGeometry, TBoundsReader, TFilter, TGroupResults>
        CreateMaintenance<TGeometry, TBoundsReader, TFilter, TGroupResults>(
            this RelationQuery<SpatialCandidates<TGeometry, TBoundsReader, TFilter, TGroupResults>, (Entity, Entity)> query,
            Scheduler scheduler)
        where TGeometry : struct
        where TBoundsReader : struct, ISpatialBoundsReader<TGeometry>
        where TFilter : struct, ISpatialFilter<TGroupResults>
        where TGroupResults : unmanaged => new(query.Enumerable, scheduler);
    public static SpatialMaintenance<TGeometry, TBoundsReader, SpatialAll, GroupMatch>
        CreateMaintenance<TGeometry, TBoundsReader>(
            this RelationQuery<TreeCandidates<TGeometry, TBoundsReader>, (Entity, Entity)> query, Scheduler scheduler)
        where TGeometry : struct
        where TBoundsReader : struct, ISpatialBoundsReader<TGeometry> =>
        new(new SpatialCandidates<TGeometry, TBoundsReader, SpatialAll, GroupMatch>(query.Enumerable, default), scheduler);
}

public readonly struct SpatialAll : ISpatialFilter<GroupMatch>
{
    public QueryDescription AddReads(QueryDescription definition) => definition;
    public void RebuildSummaries(World world) { }
    public void RefitSummary(int node, World world) { }
    public bool HasChanges<TGeometry>(QueryChunk chunk, int row, ulong since) where TGeometry : struct => false;
    public GroupMatch MatchGroups(int firstNode, int secondNode, in BoundsNode first, in BoundsNode second,
        in GroupMatch inherited, out GroupMatch results) => results = GroupMatch.All;
    public bool MatchesLeaves(QueryChunk first, int firstRow, QueryChunk second, int secondRow,
        int firstNode, int secondNode, in GroupMatch results) => true;
}

/// <summary>Maintains the tree and its summaries from query membership and component write versions.</summary>
public sealed class SpatialMaintenance<TGeometry, TBoundsReader, TFilter, TGroupResults> : IDisposable
    where TGeometry : struct
    where TBoundsReader : struct, ISpatialBoundsReader<TGeometry>
    where TFilter : struct, ISpatialFilter<TGroupResults>
    where TGroupResults : unmanaged
{
    private readonly EventReader<MembershipChange> _memberships;
    private readonly Scheduler _scheduler;
    private readonly Query _members, _access;
    private readonly JobResource _indexResource;
    private JobHandle _completion;
    private bool _initialized;
    public SpatialMaintenance(SpatialCandidates<TGeometry, TBoundsReader, TFilter, TGroupResults> candidates,
        Scheduler scheduler)
    {
        _scheduler = scheduler;
        var maintained = new SpatialCandidates<TGeometry, TBoundsReader, TFilter, TGroupResults>(
            candidates.Source.WithTree(), candidates.Filter);
        Query = new(maintained);
        _members = maintained.GetQuery(0);
        _access = SpatialQueries.Reads(_members, maintained.AddReads(new QueryDescription(), 0));
        _memberships = maintained.World.WatchMembership(_members);
        _indexResource = scheduler.CreateResource("Spatial tree and summaries");
    }
    public RelationQuery<SpatialCandidates<TGeometry, TBoundsReader, TFilter, TGroupResults>, (Entity, Entity)> Query { get; }
    public BoundsTree Tree => Query.Enumerable.Tree;
    public ulong LastSystemVersion { get; private set; }
    public JobResourceAccess ReadAccess => JobResourceAccess.Read(_indexResource);
    public int AccessCount => ComponentReads().Length + 1;
    private JobResourceAccess[] ComponentReads() => Query.Enumerable.World.Execution?.CollectReadAccess(_access) ?? [];
    public int CopyReadAccesses(Span<JobResourceAccess> destination)
    {
        JobResourceAccess[] reads = ComponentReads();
        reads.CopyTo(destination);
        destination[reads.Length] = ReadAccess;
        return reads.Length + 1;
    }
    public JobHandle Schedule(JobHandle dependency = default)
    {
        _completion = _scheduler.Schedule(new BeginMaintenance(this),
            _scheduler.CombineDependencies([_completion, dependency]));
        return _completion;
    }
    public JobHandle ScheduleRead<TJob>(in TJob job, ReadOnlySpan<JobResourceAccess> additionalAccesses,
        JobHandle dependency = default) where TJob : struct, IJob
    {
        Span<JobResourceAccess> accesses = stackalloc JobResourceAccess[AccessCount + additionalAccesses.Length];
        int count = CopyReadAccesses(accesses);
        additionalAccesses.CopyTo(accesses[count..]);
        return _scheduler.Schedule(job, accesses, dependency);
    }
    private readonly struct BeginMaintenance(SpatialMaintenance<TGeometry, TBoundsReader, TFilter, TGroupResults> data) : IJob
    {
        public void Execute()
        {
            EventReadRange membership = data._memberships.Capture();
            JobResourceAccess[] reads = data.ComponentReads();
            Span<JobResourceAccess> accesses = stackalloc JobResourceAccess[reads.Length + 2];
            reads.CopyTo(accesses);
            accesses[^2] = membership.Read;
            accesses[^1] = JobResourceAccess.Write(data._indexResource);
            data._scheduler.Schedule(new UpdateIndex(data, membership), accesses, membership.Ready);
        }
    }
    private readonly struct UpdateIndex(SpatialMaintenance<TGeometry, TBoundsReader, TFilter, TGroupResults> data,
        EventReadRange membership) : IJob
    {
        public void Execute() => data.Update(membership);
    }
    private void Update(in EventReadRange membership)
    {
        World world = Query.Enumerable.World;
        ulong version = world.Version;
        using (QueryScope access = _access.OpenRead())
        using (QueryScope members = _members.OpenRead())
        {
            if (!_initialized)
            {
                Query.Enumerable.Source.BuildTree(new(world, access, members, default));
                Query.Enumerable.Filter.RebuildSummaries(world);
                _initialized = true;
            }
            else
            {
                using EventRead<MembershipChange> changes = _memberships.Open(membership);
                foreach (MembershipChange change in changes.First) Reconcile(change.Entity, members);
                foreach (MembershipChange change in changes.Second) Reconcile(change.Entity, members);
                TFilter filter = Query.Enumerable.Filter;
                foreach (QueryChunk chunk in members)
                {
                    if (!chunk.Has<TGeometry>()) continue;
                    for (int row = 0; row < chunk.Count; row++)
                        if (chunk.WasWritten<TGeometry>(row, LastSystemVersion)
                            || filter.HasChanges<TGeometry>(chunk, row, LastSystemVersion))
                            Reconcile(chunk.Entities[row], members);
                }
            }
        }
        _memberships.Advance(membership.Through);
        LastSystemVersion = version;
    }
    private void Reconcile(Entity entity, scoped QueryScope members)
    {
        World world = Query.Enumerable.World;
        if (members.Contains(entity) && world.Has<TGeometry>(entity))
        {
            SpatialBounds bounds = Query.Enumerable.Source.ReadBounds(in world.Read<TGeometry>(entity));
            if (Tree.TryGetLeaf(entity, out int leaf)) Tree.SetBounds(leaf, in bounds);
            else leaf = Tree.Insert(entity, in bounds);
            Refit(leaf);
        }
        else if (Tree.TryGetLeaf(entity, out _)) Refit(Tree.Remove(entity));
    }
    private void Refit(int node)
    {
        while (node >= 0)
        {
            Tree.Refit(node);
            Query.Enumerable.Filter.RefitSummary(node, Query.Enumerable.World);
            node = Tree.Nodes[node].Parent;
        }
    }
    public void Dispose()
    {
        _completion.Complete();
        _memberships.Dispose();
        _scheduler.ReleaseResource(_indexResource);
    }
}
