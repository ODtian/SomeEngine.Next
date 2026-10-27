using System.Numerics;
using SomeEngine.ECS;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;

namespace SomeEngine.Physics;

/// <summary>Limits on the current resultant force in N and torque in N·m of each participating body.</summary>
public readonly record struct ConstraintBreakSettings(float MaximumForce, float MaximumTorque) : IComponent;

/// <summary>Records the selected constraint's current load and destroys it through ordinary structural commands.</summary>
public readonly struct BreakConstraints<TConstraint, TForces> : IPhysicsStage, IDisposable
    where TConstraint : struct, IComponent where TForces : struct, IPhysicsConstraintForces<TConstraint>
{
    private readonly World _world;
    private readonly QueryHandle _bodies, _constraints;
    private readonly PhysicsEventBuffer _events;
    private readonly QueryReadResources? _resources;
    private readonly JobResourceAccess[] _accesses;
    public BreakConstraints(World world, QueryHandle bodies, QueryHandle constraints, PhysicsEventBuffer events, QueryReadResources? resources = null)
        : this(world, bodies, constraints, events, resources, default) { }

    public BreakConstraints(World world, QueryHandle bodies, QueryHandle constraints, PhysicsEventBuffer events,
        QueryReadResources? resources, ReadOnlySpan<JobResourceAccess> extraAccesses)
    {
        _world = world; _events = events; _resources = resources;
        _bodies = world.Query(world.GetQueryDefinition(bodies).AsReadOnly().WithAccess(new QueryDefinitionBuilder()
            .Read<BodyPose>().Read<BodyMass>().Read<BodyStepState>().Build()));
        _constraints = world.Query(world.GetQueryDefinition(constraints).AsReadOnly().WithOptionalRead<ConstraintBreakSettings>());
        var accesses = new List<JobResourceAccess> { events.WriteAccess };
        PhysicsQueryAccess.AddAccesses(world, _bodies, accesses, resources); PhysicsQueryAccess.AddAccesses(world, _constraints, accesses, resources);
        foreach (JobResourceAccess access in extraAccesses) accesses.Add(access);
        _accesses = accesses.ToArray();
    }
    public JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency)
    {
        var commands = new JobCommandBuffer(context.World, 1);
        commands.Schedule(0, new Check(this, context), _accesses, dependency);
        JobHandle playback = commands.SchedulePlayback();
        Scheduler scheduler = WorldJobs.GetScheduler(context.World);
        JobHandle cleanup = scheduler.ScheduleFinally(new ReleasePhysicsEventCommands(commands), JobScheduleOptions.Default, playback);
        return scheduler.CombineDependencies([playback, cleanup]);
    }
    private readonly struct Check(BreakConstraints<TConstraint, TForces> stage, PhysicsStepContext context) : IJobCommandProducer
    {
        public void Execute(ref JobCommandWriter commands)
        {
            var read = new ReadBodies(stage, context, commands);
            stage._world.ExecuteQuery(stage._bodies, ref read);
        }
    }
    private ref struct ReadBodies(BreakConstraints<TConstraint, TForces> stage, PhysicsStepContext context, JobCommandWriter commands) : IQueryExecution
    {
        private JobCommandWriter _commands = commands;
        public void Execute(scoped QueryCursor rows)
        {
            if (stage._resources is null) ExecuteRows(rows);
            else
            {
                var read = this;
                stage._resources.Execute(rows, ref read, static (scoped QueryCursor query, ref ReadBodies state) => state.ExecuteRows(query));
            }
        }
        private void ExecuteRows(scoped QueryCursor rows)
        {
            var read = new ReadConstraints(stage._events, stage._resources, context, new PhysicsBodies(rows), _commands);
            stage._world.ExecuteQuery(stage._constraints, ref read);
        }
    }
    private ref struct ReadConstraints(PhysicsEventBuffer events, QueryReadResources? resources, PhysicsStepContext context, PhysicsBodies bodies, JobCommandWriter commands) : IQueryExecution
    {
        private readonly PhysicsBodies _bodies = bodies;
        private JobCommandWriter _commands = commands;
        public void Execute(scoped QueryCursor rows)
        {
            if (resources is null) ExecuteRows(rows);
            else
            {
                var read = this;
                resources.Execute(rows, ref read, static (scoped QueryCursor query, ref ReadConstraints state) => state.ExecuteRows(query));
            }
        }
        private void ExecuteRows(scoped QueryCursor rows)
        {
            foreach (QueryRow row in rows.Rows)
            {
                if (!row.TryReadRef<ConstraintBreakSettings>(out var settings)) continue;
                ref readonly TConstraint constraint = ref row.ReadRef<TConstraint>();
                int count = TForces.GetBodyCount(in constraint, row, in context);
                for (int index = 0; index < count; index++)
                {
                    var body = TForces.GetBody(in constraint, row, index, in context);
                    TForces.GetBodyForce(in constraint, row, body, _bodies, in context, out Vector3 force, out Vector3 torque);
                    if (force.LengthSquared() <= settings.Value.MaximumForce * settings.Value.MaximumForce &&
                        torque.LengthSquared() <= settings.Value.MaximumTorque * settings.Value.MaximumTorque) continue;
                    events.Add(new PhysicsEvent { Kind = PhysicsEventKind.Break, Subject = row.Entity, Body = body,
                        First = TForces.GetBody(in constraint, row, 0, in context),
                        Second = count > 1 ? TForces.GetBody(in constraint, row, 1, in context) : default,
                        Substep = context.Substep, Time = context.EndTime, Force = force, Torque = torque });
                    _commands.DestroyEntity(row.Entity);
                    break;
                }
            }
        }
    }
    public void Dispose() { _world.ReleaseQuery(_bodies); _world.ReleaseQuery(_constraints); }
}
