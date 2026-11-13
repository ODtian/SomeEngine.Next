using System.Numerics;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;

namespace SomeEngine.Physics;

/// <summary>Sleep and wake stages sharing the numerical solver's original connectivity.</summary>
public readonly struct PhysicsActivity<TInputs> : IDisposable where TInputs : struct, IPhysicsGraphInputs
{
    private readonly PhysicsActivityData<TInputs> _data;
    internal PhysicsActivity(PhysicsGraphData graph, PhysicsGraphQueries queries, Query bodies, TInputs inputs,
        BodyActivitySettings settings, PhysicsEventBuffer? events) => _data = new(graph, queries, bodies, inputs, settings, events);
    public PhysicsActivityStage<TInputs> WakeConnected() => new(this, false);
    public PhysicsActivityStage<TInputs> Finish() => new(this, true);
    internal JobHandle Schedule(in PhysicsStepContext context, bool finish, JobHandle dependency) => _data.Schedule(in context, finish, dependency);
    public void Dispose() => _data.Clear();
}
public readonly struct PhysicsActivityStage<TInputs> : IPhysicsStage where TInputs : struct, IPhysicsGraphInputs
{
    private readonly PhysicsActivity<TInputs> _activity;
    private readonly bool _finish;
    internal PhysicsActivityStage(PhysicsActivity<TInputs> activity, bool finish) { _activity = activity; _finish = finish; }
    public JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency) => _activity.Schedule(in context, _finish, dependency);
}
internal sealed class PhysicsActivityData<TInputs>(PhysicsGraphData graph, PhysicsGraphQueries queries,
    Query members, TInputs inputs, BodyActivitySettings settings, PhysicsEventBuffer? events) where TInputs : struct, IPhysicsGraphInputs
{
    private readonly PhysicsGraphData _graph = graph;
    private readonly PhysicsGraphQueries _queries = queries;
    private readonly TInputs _inputs = inputs;
    private readonly BodyActivitySettings _settings = settings;
    private readonly PhysicsEventBuffer? _events = events;
    private readonly BodyColumns _columns = new();
    private readonly Dictionary<Entity, (ulong Pose, ulong Mass)> _versions = [];
    private readonly List<Entity> _removed = [];
    private readonly HashSet<Entity> _notQuiet = [];
    internal JobHandle Schedule(in PhysicsStepContext context, bool finish, JobHandle dependency) =>
        PhysicsBodyJobs.Schedule<ActivityBodyFields, Update>(members, _columns, in context, new(this, finish), dependency);
    private readonly struct Update(PhysicsActivityData<TInputs> data, bool finish) : IPhysicsBodyWork
    {
        public void AddInputAccesses(World world, List<JobResourceAccess> accesses) => data._inputs.AddInputAccesses(world, accesses);
        public void AddAccesses(World world, List<JobResourceAccess> accesses)
        {
            accesses.Add(data._graph.WriteAccess); accesses.AddRange(data._queries.MetadataAccesses);
            data._inputs.AddResources(world, accesses, PhysicsGraphPhase.Metadata);
            accesses.AddRange(((WorldJobs)world.Execution!).CollectAccess(new SystemAccesses().ReadRandom<SomeEngine.ECS.Buffer<BodyWake>>()));
            if (data._events is not null) accesses.Add(data._events.WriteAccess);
        }
        public void Execute(in PhysicsStepContext context, scoped PhysicsBodies bodies)
        {
            data._graph.Begin(); data._graph.ReadBodies(bodies); data._queries.ReadMembers(in context, data._graph);
            data._inputs.RefreshGraph(in context, bodies);
            var builder = new PhysicsConstraintGraphBuilder(data._graph, bodies);
            data._inputs.ReadGraph(in context, 0, ref builder); data._graph.Finish();
            data._notQuiet.Clear();
            var errors = new PhysicsActivityErrors(data._settings, data._notQuiet);
            data._inputs.ReadActivityErrors(in context, bodies, ref errors);
            data.Wake(bodies, in context); if (finish) data.Finish(bodies, in context);
        }
    }

    private void Wake(scoped PhysicsBodies bodies, in PhysicsStepContext context)
    {
        PruneVersions(bodies);
        foreach (PhysicsActivityIsland island in _graph.ActivityIslands)
        {
            bool awake = false, changed = false;
            foreach (Entity body in island.DynamicBodies)
            {
                changed |= _graph.IsActivityChanged(body);
                awake |= _notQuiet.Contains(body) || bodies.IsAwake(body) || bodies.ReadVelocity(body).Linear != Vector3.Zero || bodies.ReadVelocity(body).Angular != Vector3.Zero;
                if (_versions.TryGetValue(body, out var previous))
                    awake |= previous != (context.World.ComponentDataWriteVersion<BodyPose>(body), context.World.ComponentDataWriteVersion<BodyMass>(body));
            }
            foreach (Entity body in island.References)
            {
                if (context.World.Has<SomeEngine.ECS.Buffer<BodyWake>>(body))
                {
                    using var requests = context.World.ReadBuffer<BodyWake>(body);
                    foreach (BodyWake request in requests.AsSpan()) changed |= request.Time >= context.StartTime && request.Time < context.EndTime;
                }
                if (bodies.ReadMass(body).Motion == BodyMotion.Dynamic) continue;
                ref readonly BodyVelocity velocity = ref bodies.ReadVelocity(body);
                awake |= velocity.Linear != Vector3.Zero || velocity.Angular != Vector3.Zero;
                if (_versions.TryGetValue(body, out var previous))
                    awake |= previous != (context.World.ComponentDataWriteVersion<BodyPose>(body), context.World.ComponentDataWriteVersion<BodyMass>(body));
            }
            if (!awake && !changed) continue;
            foreach (Entity body in island.DynamicBodies)
            {
                if (!bodies.HasActivity(body)) continue;
                if (!changed && !bodies.ReadActivity(body).Sleeping) continue;
                ref BodyActivity activity = ref bodies.WriteActivity(body);
                if (activity.Sleeping)
                {
                    ref readonly BodyStepState step = ref bodies.ReadStepState(body);
                    double time = step.Number == context.Number && step.Substep == context.Substep && step.Time == context.EndTime
                        ? context.EndTime : context.StartTime;
                    _events?.Add(new PhysicsEvent { Kind = PhysicsEventKind.Wake, Subject = body, Substep = context.Substep, Time = time });
                }
                activity.Sleeping = false; activity.QuietSeconds = 0;
            }
        }
        _graph.ClearActivityChanges();
    }

    private void Finish(scoped PhysicsBodies bodies, in PhysicsStepContext context)
    {
        foreach (PhysicsActivityIsland island in _graph.ActivityIslands)
        {
            bool ready = true, awake = false;
            foreach (Entity body in island.DynamicBodies)
            {
                ready &= !_notQuiet.Contains(body);
                if (!bodies.HasActivity(body)) { ready = false; continue; }
                ref readonly BodyActivity activity = ref bodies.ReadActivity(body);
                if (activity.Sleeping) continue;
                awake = true;
                ref readonly BodyVelocity velocity = ref bodies.ReadVelocity(body);
                ref readonly BodyPose pose = ref bodies.ReadPose(body);
                ref readonly BodyMass mass = ref bodies.ReadMass(body);
                ref readonly BodyStepState step = ref bodies.ReadStepState(body);
                float speed = velocity.Linear.Length() + velocity.Angular.Length() * activity.Radius;
                float movement = (pose.GetCenterOfMass(in mass) - step.StartCenterOfMass).Length()
                    + BodyRotation.Velocity(step.StartRotation, pose.Rotation, 1).Length() * activity.Radius;
                ready &= speed <= _settings.SpeedThreshold && movement <= _settings.SpeedThreshold * context.Duration;
            }
            if (!awake) continue;
            bool sleep = ready;
            foreach (Entity body in island.DynamicBodies)
            {
                if (!bodies.HasActivity(body)) continue;
                ref BodyActivity activity = ref bodies.WriteActivity(body);
                activity.QuietSeconds = ready ? activity.QuietSeconds + (float)context.Duration : 0;
                sleep &= activity.QuietSeconds >= _settings.QuietDuration;
            }
            if (sleep)
                foreach (Entity body in island.DynamicBodies)
                {
                    ref BodyActivity activity = ref bodies.WriteActivity(body);
                    if (!activity.Sleeping) _events?.Add(new PhysicsEvent
                    {
                        Kind = PhysicsEventKind.Sleep,
                        Subject = body,
                        Substep = context.Substep,
                        Time = context.EndTime
                    });
                    activity.Sleeping = true;
                    bodies.WriteVelocity(body) = default;
                }
        }
        PruneVersions(bodies);
        foreach (Entity entity in _columns.Entities)
            RecordVersions(entity, context.World);
        foreach (PhysicsActivityIsland island in _graph.ActivityIslands)
            foreach (Entity entity in island.References) RecordVersions(entity, context.World);
    }
    private void RecordVersions(Entity entity, World world) =>
        _versions[entity] = (world.ComponentDataWriteVersion<BodyPose>(entity), world.ComponentDataWriteVersion<BodyMass>(entity));
    private void PruneVersions(scoped PhysicsBodies bodies)
    {
        _removed.Clear();
        foreach (Entity entity in _versions.Keys) if (!bodies.Contains(entity)) _removed.Add(entity);
        foreach (Entity entity in _removed) _versions.Remove(entity);
    }
    internal void Clear() { _versions.Clear(); _removed.Clear(); _notQuiet.Clear(); }
}
