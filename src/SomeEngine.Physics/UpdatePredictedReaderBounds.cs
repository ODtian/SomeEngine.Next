using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;

namespace SomeEngine.Physics;

/// <summary>Updates selected geometry ranges through a concrete motion reader and its actual referenced inputs.</summary>
public readonly struct UpdatePredictedBounds<TGeometry, TMotion> : IPhysicsStage, IDisposable
    where TGeometry : struct where TMotion : struct, ICollisionGeometryMotion<TGeometry>
{
    private readonly PredictedGeometryBounds<TGeometry, TMotion> _bounds;
    public UpdatePredictedBounds(World world, QueryHandle members, QueryHandle access, TMotion motion, float padding,
        QueryReadResources? resources = null) => _bounds = new(world, members, access, motion, padding, resources ?? new());
    public JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency) => _bounds.Schedule(in context, dependency);
    public void Dispose() => _bounds.Dispose();
}

internal sealed class PredictedGeometryBounds<TGeometry, TMotion> : IDisposable
    where TGeometry : struct where TMotion : struct, ICollisionGeometryMotion<TGeometry>
{
    private readonly World _world;
    private readonly QueryHandle _members, _access, _writes;
    private readonly TMotion _motion;
    private readonly float _padding;
    private readonly QueryReadResources _resources;
    private readonly Scheduler _scheduler;
    private readonly JobResourceToken _selection;
    private readonly JobResourceAccess[] _collectAccesses, _writeAccesses;
    private readonly List<Entity> _entities = [];

    internal PredictedGeometryBounds(World world, QueryHandle members, QueryHandle access, TMotion motion, float padding,
        QueryReadResources resources)
    {
        _world = world; _members = members; _motion = motion; _padding = padding; _resources = resources;
        _scheduler = WorldJobs.GetScheduler(world);
        _selection = _scheduler.CreateResourceToken("Predicted geometry members");
        _access = world.Query(motion.AddReads(world.GetQueryDefinition(access)));
        _writes = world.Query(world.GetQueryDefinition(members).WithAccess(new QueryDefinitionBuilder().ReadWrite<CollisionBounds>().Build()));
        var reads = new List<JobResourceAccess>();
        PhysicsQueryAccess.AddAccesses(world, members, reads, resources);
        reads.Add(JobResourceAccess.Write(_selection));
        _collectAccesses = reads.ToArray();
        reads.Clear();
        PhysicsQueryAccess.AddAccesses(world, _writes, reads, resources);
        reads.Add(JobResourceAccess.Read(_selection));
        _writeAccesses = reads.ToArray();
    }

    internal JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency)
    {
        JobHandle selected = _scheduler.Schedule(new CollectMembers(this), _collectAccesses, dependency);
        return _resources.ScheduleEntities(_world, _access, new GeometryMembers(this), new UpdateBounds(this, context), _writeAccesses, selected);
    }

    private readonly struct CollectMembers(PredictedGeometryBounds<TGeometry, TMotion> bounds) : IJob, IQueryExecution
    {
        public void Execute()
        {
            bounds._entities.Clear();
            var collect = this;
            bounds._world.ExecuteQuery(bounds._members, ref collect);
        }
        public void Execute(scoped QueryCursor members)
        {
            foreach (QueryRow row in members.Rows) bounds._entities.Add(row.Entity);
        }
    }

    private readonly struct GeometryMembers(PredictedGeometryBounds<TGeometry, TMotion> bounds) : IQueryReadEntities
    {
        public int Count => bounds._entities.Count;
        public Entity GetEntity(int index) => bounds._entities[index];
    }

    private readonly struct UpdateBounds(PredictedGeometryBounds<TGeometry, TMotion> bounds, PhysicsStepContext context) : IJob, IQueryExecution
    {
        private readonly PhysicsStepContext _context = context;
        public void Execute()
        {
            var read = this;
            bounds._world.ExecuteQuery(bounds._access, ref read);
        }
        public void Execute(scoped QueryCursor access)
        {
            var write = new WriteBounds(bounds, access, _context);
            bounds._world.ExecuteQuery(bounds._writes, ref write);
        }
    }

    private readonly ref struct WriteBounds(PredictedGeometryBounds<TGeometry, TMotion> bounds,
        QueryCursor access, PhysicsStepContext context) : IQueryExecution
    {
        private readonly QueryCursor _access = access;
        private readonly PhysicsStepContext _context = context;
        public void Execute(scoped QueryCursor members)
        {
            foreach (Entity entity in bounds._entities)
            {
                if (!members.TryGetRow(entity, out QueryRow row)) continue;
                _access.TryGetRow(entity, out QueryRow geometry);
                row.ReadWrite<CollisionBounds>() = bounds._motion.ReadMotionBounds(geometry, _access, in _context, bounds._padding);
            }
        }
    }

    public void Dispose()
    {
        _world.ReleaseQuery(_writes);
        _world.ReleaseQuery(_access);
        _scheduler.ReleaseResourceToken(_selection);
    }
}
