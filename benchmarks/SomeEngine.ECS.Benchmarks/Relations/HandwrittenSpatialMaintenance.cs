using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Relations;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;
using SomeEngine.Physics;
using CategorySummary = SomeEngine.Physics.CollisionBitmapSummary<
    SomeEngine.ECS.Benchmarks.Relations.Spatial.MutualCategories,
    SomeEngine.ECS.Benchmarks.Relations.Spatial.MutualCategoryBitmap,
    SomeEngine.ECS.Benchmarks.Relations.Spatial.Body, SomeEngine.ECS.Relations.MutualPermission>;

namespace SomeEngine.ECS.Benchmarks.Relations.Spatial;

/// <summary>Independent maintenance of the same tree and summary layout for paired measurements.</summary>
public sealed class HandwrittenSpatialMaintenance : IDisposable
{
    private readonly EntityTableChanges _tableChanges;
    private readonly Scheduler _scheduler;
    private readonly QueryHandle _members;
    private readonly QueryHandle _access;
    private readonly JobResourceToken _indexResource;
    private readonly JobResourceAccess[] _updateAccesses;

    public HandwrittenSpatialMaintenance(World world, QueryHandle bodies, CollisionTree tree, Scheduler scheduler)
    {
        World = world;
        Bodies = bodies;
        Tree = tree;
        Keys = tree.CreateKeySummary<Hair, Entity>();
        Categories = tree.CreateBitmapSummary<MutualCategories, MutualCategoryBitmap, Body, MutualPermission>(default, default);
        _scheduler = scheduler;
        _tableChanges = world.ObserveEntityTables();
        QueryDefinition members = world.GetQueryDefinition(bodies).WithOptionalRead<Body>().WithOptionalRead<Hair>();
        QueryDefinition access = QueryDefinition.Empty.WithAccessFrom(members)
            .WithOptionalRead<Body>().WithOptionalRead<Hair>();
        _members = world.Query(members);
        _access = world.Query(access);
        _indexResource = scheduler.CreateResourceToken("Handwritten collision tree and summaries");
        _updateAccesses = new JobResourceAccess[QueryJobAccess.Count(access) + 1];
        QueryJobAccess.CopyAccesses(world, access, _updateAccesses);
        _updateAccesses[^1] = JobResourceAccess.Write(_indexResource);
    }

    public World World { get; }
    public QueryHandle Bodies { get; }
    public CollisionTree Tree { get; }
    public CollisionKeySummary<Hair, Entity> Keys { get; }
    public CategorySummary Categories { get; }
    public uint LastSystemVersion { get; private set; }
    public int AccessCount => _updateAccesses.Length;
    public RelationQuery<HandwrittenSpatialPartitions, (Entity, Entity)> Query =>
        new(new HandwrittenSpatialPartitions(World, Bodies, Tree, Keys, Categories));

    public int CopyReadAccesses(Span<JobResourceAccess> destination)
    {
        _updateAccesses.AsSpan(0, _updateAccesses.Length - 1).CopyTo(destination);
        destination[_updateAccesses.Length - 1] = JobResourceAccess.Read(_indexResource);
        return _updateAccesses.Length;
    }

    public JobHandle Schedule(JobHandle dependency = default)
    {
        var job = new MaintenanceJob(this);
        return _scheduler.Schedule(job, _updateAccesses, dependency);
    }

    public JobHandle ScheduleRead<TJob>(in TJob job, ReadOnlySpan<JobResourceAccess> additionalAccesses,
        JobHandle dependency = default)
        where TJob : struct, IJob
    {
        Span<JobResourceAccess> accesses = stackalloc JobResourceAccess[AccessCount + additionalAccesses.Length];
        CopyReadAccesses(accesses);
        additionalAccesses.CopyTo(accesses[AccessCount..]);
        return _scheduler.Schedule(job, accesses, dependency);
    }

    public bool Enumerate<TOutput>(ref TOutput output)
        where TOutput : struct, IRelationOutput<(Entity, Entity)>, allows ref struct =>
        HandwrittenSpatialCollision.Filtered(World, Bodies, Tree, Keys, Categories, ref output);

    private void Update()
    {
        EntityTableChangeBatch changes = _tableChanges.Read();
        var execution = new AccessExecution(this, changes);
        World.ExecuteQuery(_access, LastSystemVersion, ref execution);
        _tableChanges.Accept(changes);
        LastSystemVersion = execution.CurrentSystemVersion;
    }

    private void Reconcile(Entity entity, QueryCursor members, QueryCursor access)
    {
        if (members.TryGetRow(entity, out _) && access.TryGetRow(entity, out var row) && row.Has<Body>())
        {
            CollisionBounds bounds = row.ReadRef<Body>().Bounds;
            if (Tree.TryGetLeaf(entity, out int leaf))
                Tree.SetBounds(leaf, in bounds);
            else
                leaf = Tree.Insert(entity, in bounds);
            Refit(leaf, access);
        }
        else if (Tree.TryGetLeaf(entity, out _))
            Refit(Tree.Remove(entity), access);
    }

    private void Refit(int node, QueryCursor access)
    {
        while (node >= 0)
        {
            Tree.Refit(node);
            Categories.Refit(node, access);
            Keys.Refit(node, access);
            node = Tree.Nodes[node].Parent;
        }
    }

    public void Dispose()
    {
        _tableChanges.Dispose();
        World.ReleaseQuery(_access);
        World.ReleaseQuery(_members);
        _scheduler.ReleaseResourceToken(_indexResource);
    }

    private readonly struct MaintenanceJob(HandwrittenSpatialMaintenance maintenance) : IJob
    {
        public void Execute() => maintenance.Update();
    }

    private struct AccessExecution(HandwrittenSpatialMaintenance maintenance, EntityTableChangeBatch changes) : IQueryExecution
    {
        public uint CurrentSystemVersion;

        public void Execute(QueryCursor access)
        {
            var execution = new MemberExecution(maintenance, changes, access);
            maintenance.World.ExecuteQuery(maintenance._members, maintenance.LastSystemVersion, ref execution);
            CurrentSystemVersion = access.CurrentSystemVersion;
        }
    }

    private readonly ref struct MemberExecution(HandwrittenSpatialMaintenance maintenance,
        EntityTableChangeBatch changes, QueryCursor access) : IQueryExecution
    {
        private readonly QueryCursor _access = access;

        public void Execute(QueryCursor members)
        {
            foreach (Entity entity in changes.Entities)
                maintenance.Reconcile(entity, members, _access);
            foreach (QueryChunkView chunk in members.ChangedChunks(changes))
            {
                if (!chunk.Has<Body>())
                    continue;
                var bodyChanges = chunk.ReadRowChanges<Body>();
                var hairChanges = chunk.ReadRowChanges<Hair>();
                foreach (int row in chunk.RowIndices)
                {
                    if (bodyChanges.HasChanged(row) || hairChanges.HasChanged(row))
                        maintenance.Reconcile(chunk.GetEntity(row), members, _access);
                }
            }
        }
    }
}
