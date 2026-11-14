using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Relations;
using SomeEngine.ECS.Spatial;
using SomeEngine.Job;
using CategorySummary = SomeEngine.ECS.Spatial.BoundsBitmapSummary<
    SomeEngine.ECS.Benchmarks.Relations.Spatial.MutualCategories,
    SomeEngine.ECS.Benchmarks.Relations.Spatial.MutualCategoryBitmap,
    SomeEngine.ECS.Benchmarks.Relations.Spatial.Body, SomeEngine.ECS.Relations.MutualPermission>;

namespace SomeEngine.ECS.Benchmarks.Relations.Spatial;

public sealed class HandwrittenSpatialMaintenance : IDisposable
{
    private readonly EventReader<MembershipChange> _memberships;
    private readonly Scheduler _scheduler;
    private readonly Query _members, _access;
    private readonly JobResource _indexResource;
    private JobHandle _completion;
    private bool _initialized;
    public HandwrittenSpatialMaintenance(World world, Query bodies, BoundsTree tree, Scheduler scheduler)
    {
        World = world;
        Bodies = bodies;
        Tree = tree;
        Keys = tree.CreateKeySummary<Hair, Entity>();
        Categories = tree.CreateBitmapSummary<MutualCategories, MutualCategoryBitmap, Body, MutualPermission>(default, default);
        _scheduler = scheduler;
        _members = bodies;
        _access = bodies.WithOptionalRead<Body>().WithOptionalRead<Hair>();
        _memberships = world.WatchMembership(_members);
        _indexResource = scheduler.CreateResource("Handwritten spatial tree and summaries");
    }
    public World World { get; }
    public Query Bodies { get; }
    public BoundsTree Tree { get; }
    public BoundsKeySummary<Hair, Entity> Keys { get; }
    public CategorySummary Categories { get; }
    public ulong LastSystemVersion { get; private set; }
    public RelationQuery<HandwrittenSpatialPartitions, (Entity, Entity)> Query =>
        new(new(World, Bodies, Tree, Keys, Categories));
    public bool Enumerate<TOutput>(ref TOutput output)
        where TOutput : struct, IRelationOutput<(Entity, Entity)>, allows ref struct =>
        HandwrittenSpatialCollision.Filtered(World, Bodies, Tree, Keys, Categories, ref output);
    public JobResourceAccess ReadAccess => JobResourceAccess.Read(_indexResource);
    public int AccessCount => ComponentReads().Length + 1;
    private JobResourceAccess[] ComponentReads() => World.Execution?.CollectReadAccess(_access) ?? [];
    public int CopyReadAccesses(Span<JobResourceAccess> destination)
    {
        JobResourceAccess[] reads = ComponentReads();
        reads.CopyTo(destination);
        destination[reads.Length] = ReadAccess;
        return reads.Length + 1;
    }
    public JobHandle Schedule(JobHandle dependency = default)
    {
        _completion = _scheduler.Schedule(new BeginMaintenance(this), dependency: _scheduler.CombineDependencies([_completion, dependency]));
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
    private readonly struct BeginMaintenance(HandwrittenSpatialMaintenance data) : IJob
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
    private readonly struct UpdateIndex(HandwrittenSpatialMaintenance data,
        EventReadRange membership) : IJob
    {
        public void Execute() => data.Update(membership);
    }
    private void Update(in EventReadRange membership)
    {
        World world = World;
        ulong version = world.Version;
        using (QueryScope access = _access.OpenRead())
        using (QueryScope members = _members.OpenRead())
        {
            if (!_initialized)
            {
                Tree.Rebuild<Body, BodyBoundsReader>(members, default);
                Keys.Rebuild(world);
                Categories.Rebuild(world);
                _initialized = true;
            }
            else
            {
                using EventRead<MembershipChange> changes = _memberships.Open(membership);
                foreach (MembershipChange change in changes.First) Reconcile(change.Entity, members);
                foreach (MembershipChange change in changes.Second) Reconcile(change.Entity, members);
                foreach (QueryChunk chunk in members)
                {
                    if (!chunk.Has<Body>()) continue;
                    for (int row = 0; row < chunk.Count; row++)
                        if (chunk.WasWritten<Body>(row, LastSystemVersion)
                            || chunk.WasWritten<Hair>(row, LastSystemVersion))
                            Reconcile(chunk.Entities[row], members);
                }
            }
        }
        _memberships.Advance(membership.Through);
        LastSystemVersion = version;
    }
    private void Reconcile(Entity entity, scoped QueryScope members)
    {
        World world = World;
        if (members.Contains(entity) && world.Has<Body>(entity))
        {
            SpatialBounds bounds = world.Read<Body>(entity).Bounds;
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
            Keys.Refit(node, World);
            Categories.Refit(node, World);
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
