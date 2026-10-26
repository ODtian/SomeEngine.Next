using System.Numerics;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;

namespace SomeEngine.Physics;

/// <summary>Activity stages sharing their solver's original full constraint graph and query inputs.</summary>
public readonly struct PhysicsActivity<TInputs> : IDisposable where TInputs : struct, IPhysicsGraphInputs
{
    private readonly PhysicsActivityData<TInputs> _data;
    internal PhysicsActivity(PhysicsGraphData graph, PhysicsGraphQueries queries, TInputs inputs,
        BodyActivitySettings settings, QueryReadResources? resources, PhysicsEventBuffer? events) => _data = new(graph, queries, inputs, settings, resources, events);
    public PhysicsActivityStage<TInputs> WakeConnected() => new(this, false);
    public PhysicsActivityStage<TInputs> Finish() => new(this, true);
    internal JobHandle Schedule(in PhysicsStepContext context, bool finish, JobHandle dependency) => _data.Schedule(in context, finish, dependency);
    public void Dispose() => _data.Dispose();
}

public readonly struct PhysicsActivityStage<TInputs> : IPhysicsStage where TInputs : struct, IPhysicsGraphInputs
{
    private readonly PhysicsActivity<TInputs> _activity;
    private readonly bool _finish;
    internal PhysicsActivityStage(PhysicsActivity<TInputs> activity, bool finish) { _activity = activity; _finish = finish; }
    public JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency) => _activity.Schedule(in context, _finish, dependency);
}

internal sealed class PhysicsActivityData<TInputs> : IDisposable where TInputs : struct, IPhysicsGraphInputs
{
    private readonly PhysicsGraphData _graph;
    private readonly PhysicsGraphQueries _queries;
    private readonly TInputs _inputs;
    private readonly BodyActivitySettings _settings;
    private readonly QueryReadResources? _resources;
    private readonly PhysicsEventBuffer? _events;
    private readonly QueryHandle _bodies;
    private readonly JobResourceAccess[] _accesses;
    private readonly Dictionary<Entity, (uint Pose, uint Mass)> _versions = [];
    private readonly List<Entity> _removed = [];
    private readonly HashSet<Entity> _notQuiet = [];

    internal PhysicsActivityData(PhysicsGraphData graph, PhysicsGraphQueries queries, TInputs inputs,
        BodyActivitySettings settings, QueryReadResources? resources, PhysicsEventBuffer? events)
    {
        _graph = graph; _queries = queries; _inputs = inputs; _settings = settings; _resources = resources; _events = events;
        World world = queries.World;
        QueryDefinition access = new QueryDefinitionBuilder().Read<BodyPose>().Read<BodyMass>().Read<BodyStepState>()
            .ReadWrite<BodyVelocity>().Optional<BodyActivity>(QueryAccess.ReadWrite).OptionalBuffer<BodyWake>(QueryAccess.Read).Build();
        QueryDefinition definition = world.GetQueryDefinition(queries.Queries[0]).AsReadOnly().WithAccess(access);
        _bodies = world.Query(definition);
        var accesses = new List<JobResourceAccess> { graph.WriteAccess };
        accesses.AddRange(queries.MetadataAccesses);
        var bodyReads = new JobResourceAccess[QueryJobAccess.Count(definition.AsReadOnly())];
        QueryJobAccess.CopyAccesses(world, definition.AsReadOnly(), bodyReads);
        accesses.AddRange(bodyReads);
        inputs.AddResources(world, accesses, PhysicsGraphPhase.Metadata);
        if (events is not null) accesses.Add(events.WriteAccess);
        _accesses = accesses.ToArray();
    }

    internal JobHandle Schedule(in PhysicsStepContext context, bool finish, JobHandle dependency) =>
        WorldJobs.GetScheduler(context.World).Schedule(new Capture(this, context, finish), _accesses, dependency);

    private readonly struct Capture(PhysicsActivityData<TInputs> data, PhysicsStepContext context, bool finish) : IJob
    {
        public void Execute()
        {
            data._queries.ReadMembers();
            EntityQueryJobs.ScheduleGroup(context.World, new Work(data), new Update(data, context, finish), data._accesses, data._resources);
        }
    }
    private readonly struct Work(PhysicsActivityData<TInputs> data) : IEntityQueryWork
    {
        public int Count => 1;
        public int QueryCount => data._queries.Queries.Length + 1;
        public QueryHandle GetQuery(int workIndex, int queryIndex) => queryIndex == data._queries.Queries.Length
            ? data._bodies : data._queries.Queries[queryIndex];
        public ReadOnlySpan<Entity> GetEntities(int workIndex, int queryIndex) => data._queries.GlobalEntities(
            queryIndex == data._queries.Queries.Length ? 0 : queryIndex, data._graph);
    }
    private readonly struct Update(PhysicsActivityData<TInputs> data, PhysicsStepContext context, bool finish) : IEntityQueryGroupJob
    {
        public bool Execute(scoped EntityQueryGroup group)
        {
            if (data._queries.ReadMembers()) return false;
            data._graph.Begin(); data._queries.ReadBodies(data._graph);
            data._queries.ReadGraph(data._graph, data._inputs, in context);
            if (data._graph.Finish()) return false;
            EntityQueryViews global = group.Get(0);
            var bodies = new PhysicsBodies(global.Get(0), global.Get(1), global.Get(2));
            data._notQuiet.Clear();
            var errors = new PhysicsActivityErrors(data._settings, data._notQuiet);
            data._inputs.ReadActivityErrors(in context, global, 3, bodies, ref errors);
            EntityQuery rows = global.Get(data._queries.Queries.Length);
            data.Wake(rows, in context);
            if (finish) data.Finish(rows, in context);
            return true;
        }
    }

    private void Wake(scoped EntityQuery rows, in PhysicsStepContext context)
    {
        PruneVersions(rows);
        foreach (PhysicsGraphData.Island island in _graph.ActivityIslands)
        {
            bool awake = false, changed = false;
            foreach (Entity body in island.DynamicBodies)
            {
                rows.TryGetRow(body, out QueryRow row);
                changed |= _graph.HasActivityChange(body);
                awake |= _notQuiet.Contains(body) || BodyActivities.IsAwake(row) || row.ReadRef<BodyVelocity>().Linear != Vector3.Zero || row.ReadRef<BodyVelocity>().Angular != Vector3.Zero;
                if (_versions.TryGetValue(body, out var previous))
                    awake |= previous != (row.WriteVersion<BodyPose>(), row.WriteVersion<BodyMass>());
            }
            foreach (Entity body in island.Reads)
            {
                rows.TryGetRow(body, out QueryRow row);
                if (row.HasBuffer<BodyWake>())
                    foreach (BodyWake request in row.ReadBuffer<BodyWake>().AsSpan())
                        changed |= request.Time >= context.StartTime && request.Time < context.EndTime;
                if (row.ReadRef<BodyMass>().Motion == BodyMotion.Dynamic) continue;
                ref readonly BodyVelocity velocity = ref row.ReadRef<BodyVelocity>();
                awake |= velocity.Linear != Vector3.Zero || velocity.Angular != Vector3.Zero;
                if (_versions.TryGetValue(body, out var previous))
                    awake |= previous != (row.WriteVersion<BodyPose>(), row.WriteVersion<BodyMass>());
            }
            if (!awake && !changed) continue;
            foreach (Entity body in island.DynamicBodies)
            {
                rows.TryGetRow(body, out QueryRow row);
                if (!row.Has<BodyActivity>()) continue;
                if (!changed && !row.ReadRef<BodyActivity>().Sleeping) continue;
                ref BodyActivity activity = ref row.ReadWrite<BodyActivity>();
                if (activity.Sleeping)
                {
                    ref readonly BodyStepState step = ref row.ReadRef<BodyStepState>();
                    double time = step.Number == context.Number && step.Substep == context.Substep && step.Time == context.EndTime
                        ? context.EndTime : context.StartTime;
                    _events?.Add(new PhysicsEvent { Kind = PhysicsEventKind.Wake, Subject = body, Substep = context.Substep, Time = time });
                }
                activity.Sleeping = false; activity.QuietSeconds = 0;
            }
        }
        _graph.ClearActivityChanges();
    }

    private void Finish(scoped EntityQuery rows, in PhysicsStepContext context)
    {
        foreach (PhysicsGraphData.Island island in _graph.ActivityIslands)
        {
            bool ready = !island.DynamicBodies.Any(_notQuiet.Contains), awake = false;
            foreach (Entity body in island.DynamicBodies)
            {
                rows.TryGetRow(body, out QueryRow row);
                if (!row.Has<BodyActivity>()) { ready = false; continue; }
                ref readonly BodyActivity activity = ref row.ReadRef<BodyActivity>();
                if (activity.Sleeping) continue;
                awake = true;
                ref readonly BodyVelocity velocity = ref row.ReadRef<BodyVelocity>();
                ref readonly BodyPose pose = ref row.ReadRef<BodyPose>();
                ref readonly BodyMass mass = ref row.ReadRef<BodyMass>();
                ref readonly BodyStepState step = ref row.ReadRef<BodyStepState>();
                float speed = velocity.Linear.Length() + velocity.Angular.Length() * activity.Radius;
                float movement = (pose.GetCenterOfMass(in mass) - step.StartCenterOfMass).Length()
                    + BodyRotation.Velocity(step.StartRotation, pose.Rotation, 1).Length() * activity.Radius;
                ready &= speed <= _settings.SpeedThreshold && movement <= _settings.SpeedThreshold * context.Duration;
            }
            if (!awake) continue;
            bool sleep = ready;
            foreach (Entity body in island.DynamicBodies)
            {
                rows.TryGetRow(body, out QueryRow row);
                if (!row.Has<BodyActivity>()) continue;
                ref BodyActivity activity = ref row.ReadWrite<BodyActivity>();
                activity.QuietSeconds = ready ? activity.QuietSeconds + (float)context.Duration : 0;
                sleep &= activity.QuietSeconds >= _settings.QuietDuration;
            }
            if (sleep)
                foreach (Entity body in island.DynamicBodies)
                {
                    rows.TryGetRow(body, out QueryRow row);
                    ref BodyActivity activity = ref row.ReadWrite<BodyActivity>();
                    if (!activity.Sleeping) _events?.Add(new PhysicsEvent { Kind = PhysicsEventKind.Sleep, Subject = body,
                        Substep = context.Substep, Time = context.EndTime });
                    activity.Sleeping = true;
                    row.ReadWrite<BodyVelocity>() = default;
                }
        }
        PruneVersions(rows);
        foreach (QueryRow row in rows.Rows)
            _versions[row.Entity] = (row.WriteVersion<BodyPose>(), row.WriteVersion<BodyMass>());
    }
    private void PruneVersions(scoped EntityQuery rows)
    {
        _removed.Clear();
        foreach (Entity entity in _versions.Keys)
            if (!rows.TryGetRow(entity, out _)) _removed.Add(entity);
        foreach (Entity entity in _removed) _versions.Remove(entity);
    }
    public void Dispose() => _queries.World.ReleaseQuery(_bodies);
}
