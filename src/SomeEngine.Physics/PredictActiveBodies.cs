using System.Numerics;
using System.Runtime.InteropServices;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;

namespace SomeEngine.Physics;

/// <summary>Refreshes common substep inputs for all bodies and writes motion only for the awake entity ranges.</summary>
public readonly struct PredictActiveBodies : IPhysicsStage, IDisposable
{
    private readonly ActivePredictionData _data;
    public PredictActiveBodies(World world, QueryHandle bodies, Vector3 acceleration, int rowsPerPacket = 64) =>
        _data = new(world, bodies, acceleration, rowsPerPacket);
    public ResumeActiveBodyPrediction Resume() => new(this);
    public JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency) => Schedule(in context, false, dependency);
    internal JobHandle Schedule(in PhysicsStepContext context, bool resume, JobHandle dependency) => _data.Schedule(in context, resume, dependency);
    public void Dispose() => _data.Dispose();
}

public readonly struct ResumeActiveBodyPrediction : IPhysicsStage
{
    private readonly PredictActiveBodies _prediction;
    internal ResumeActiveBodyPrediction(PredictActiveBodies prediction) => _prediction = prediction;
    public JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency) => _prediction.Schedule(in context, true, dependency);
}

internal sealed class ActivePredictionData : IDisposable
{
    private readonly World _world;
    private readonly QueryHandle _inputs, _motion, _reads;
    private readonly Vector3 _acceleration;
    private readonly int _rowsPerPacket;
    private readonly JobResourceAccess[] _accesses;
    private readonly Lock _gate = new();
    private readonly Stack<PredictionRows> _available = [];

    internal ActivePredictionData(World world, QueryHandle bodies, Vector3 acceleration, int rowsPerPacket)
    {
        _world = world; _acceleration = acceleration; _rowsPerPacket = rowsPerPacket;
        QueryDefinition members = world.GetQueryDefinition(bodies).AsReadOnly();
        QueryDefinition inputs = members.WithAccess(new QueryDefinitionBuilder().Read<BodyPose>().Read<BodyVelocity>().Read<BodyMass>()
            .ReadWrite<BodyStepState>().Optional<BodyActivity>(QueryAccess.ReadWrite).Build());
        _inputs = world.Query(inputs);
        _reads = world.Query(inputs.AsReadOnly());
        _motion = world.Query(inputs.WithAccess(new QueryDefinitionBuilder().ReadWrite<BodyPose>().ReadWrite<BodyVelocity>().Build()));
        var accesses = new List<JobResourceAccess>();
        PhysicsQueryAccess.AddAccesses(world, _reads, accesses);
        _accesses = accesses.ToArray();
    }

    internal JobHandle Schedule(in PhysicsStepContext context, bool resume, JobHandle dependency)
    {
        PredictionRows rows;
        lock (_gate) rows = _available.Count == 0 ? new() : _available.Pop();
        Scheduler scheduler = WorldJobs.GetScheduler(context.World);
        JobHandle handle = scheduler.Schedule(new Capture(this, rows, context, resume), _accesses, dependency);
        return scheduler.ScheduleFinally(new ReturnRows(this, rows), JobScheduleOptions.Default, handle);
    }

    private bool ReadMembers(PredictionRows rows, bool resume)
    {
        rows.NextAll.Clear(); rows.NextMoving.Clear();
        var selection = new ReadSelection(rows, resume);
        _world.ExecuteReadQuery(_reads, ref selection, static (scoped QueryCursor query, ref ReadSelection state) => state.Execute(query));
        bool changed = !CollectionsMarshal.AsSpan(rows.All).SequenceEqual(CollectionsMarshal.AsSpan(rows.NextAll))
            || !CollectionsMarshal.AsSpan(rows.Moving).SequenceEqual(CollectionsMarshal.AsSpan(rows.NextMoving));
        (rows.All, rows.NextAll) = (rows.NextAll, rows.All);
        (rows.Moving, rows.NextMoving) = (rows.NextMoving, rows.Moving);
        return changed;
    }

    private readonly struct ReadSelection(PredictionRows rows, bool resume)
    {
        public void Execute(scoped QueryCursor query)
        {
            foreach (QueryRow row in query.Rows)
            {
                rows.NextAll.Add(row.Entity);
                BodyMotion motion = row.ReadRef<BodyMass>().Motion;
                if (motion == BodyMotion.Static) continue;
                bool selected = resume
                    ? row.TryReadRef<BodyActivity>(out var activity) && !activity.Value.Sleeping && !activity.Value.MotionPredicted
                    : motion == BodyMotion.Kinematic || BodyActivities.IsAwake(row);
                if (selected) rows.NextMoving.Add(row.Entity);
            }
        }
    }
    private sealed class PredictionRows
    {
        internal List<Entity> All = [], Moving = [], NextAll = [], NextMoving = [];
    }
    private readonly struct ReturnRows(ActivePredictionData data, PredictionRows rows) : IJob
    {
        public void Execute() { lock (data._gate) data._available.Push(rows); }
    }
    private readonly struct Capture(ActivePredictionData data, PredictionRows rows, PhysicsStepContext context, bool resume) : IJob
    {
        public void Execute()
        {
            data.ReadMembers(rows, resume);
            EntityQueryJobs.ScheduleGroup(context.World, new Work(data, rows, resume), new Predict(data, rows, context, resume), data._accesses);
        }
    }
    private readonly struct Work(ActivePredictionData data, PredictionRows rows, bool resume) : IEntityQueryWork
    {
        public int Count => 2;
        public int QueryCount => 1;
        public QueryHandle GetQuery(int workIndex, int queryIndex) => workIndex == 1 ? data._motion : resume ? data._reads : data._inputs;
        public ReadOnlySpan<Entity> GetEntities(int workIndex, int queryIndex) =>
            CollectionsMarshal.AsSpan(workIndex == 0 ? rows.All : rows.Moving);
    }
    private readonly struct Predict(ActivePredictionData data, PredictionRows rows, PhysicsStepContext context, bool resume) : IEntityQueryGroupJob
    {
        public bool Execute(scoped EntityQueryGroup group)
        {
            if (data.ReadMembers(rows, resume)) return false;
            if (!resume)
            {
                var inputs = new Packets(data._inputs, rows.All, data._rowsPerPacket);
                group.ExecuteParallel(in inputs, new BeginSteps(context), 0, inputs.Count);
            }
            var motion = new Packets(data._motion, rows.Moving, data._rowsPerPacket);
            group.ExecuteParallel(in motion, new MoveBodies(data._acceleration, context), 0, motion.Count);
            return true;
        }
    }
    private readonly struct Packets(QueryHandle query, List<Entity> entities, int rowsPerPacket) : IEntityQueryWork
    {
        public int Count => (entities.Count + rowsPerPacket - 1) / rowsPerPacket;
        public int QueryCount => 1;
        public QueryHandle GetQuery(int workIndex, int queryIndex) => query;
        public ReadOnlySpan<Entity> GetEntities(int workIndex, int queryIndex)
        {
            int first = workIndex * rowsPerPacket;
            return CollectionsMarshal.AsSpan(entities).Slice(first, Math.Min(rowsPerPacket, entities.Count - first));
        }
    }
    private readonly struct BeginSteps(PhysicsStepContext context) : IEntityQueryJob
    {
        public void Execute(int index, scoped EntityQueryViews queries)
        {
            foreach (QueryRow row in queries.Get(0).Rows)
            {
                BodyPrediction.Begin(in row.ReadRef<BodyPose>(), in row.ReadRef<BodyMass>(), ref row.ReadWrite<BodyStepState>(),
                    context.Number, context.Substep, context.EndTime);
                if (row.Has<BodyActivity>()) row.ReadWrite<BodyActivity>().MotionPredicted = false;
            }
        }
    }
    private readonly struct MoveBodies(Vector3 acceleration, PhysicsStepContext context) : IEntityQueryJob
    {
        public void Execute(int index, scoped EntityQueryViews queries)
        {
            foreach (QueryRow row in queries.Get(0).Rows)
            {
                BodyPrediction.Move(ref row.ReadWrite<BodyPose>(), ref row.ReadWrite<BodyVelocity>(), in row.ReadRef<BodyMass>(),
                    ref row.ReadWrite<BodyStepState>(), acceleration, (float)context.Duration);
                if (row.Has<BodyActivity>()) row.ReadWrite<BodyActivity>().MotionPredicted = true;
            }
        }
    }
    public void Dispose()
    {
        _world.ReleaseQuery(_reads); _world.ReleaseQuery(_motion); _world.ReleaseQuery(_inputs);
    }
}
