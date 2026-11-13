using System.Numerics;
using SomeEngine.ECS;
using SomeEngine.ECS.Commands;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;

namespace SomeEngine.Physics;

/// <summary>Limits on the resultant force in N and torque in N��m of each participating body.</summary>
public readonly record struct ConstraintBreakSettings(float MaximumForce, float MaximumTorque) : ITrackedComponent;

/// <summary>Records current loads and removes overloaded constraints after the read stage completes.</summary>
public readonly struct BreakConstraints<TConstraint, TForces> : IPhysicsStage, IDisposable
    where TConstraint : struct, IComponent where TForces : struct, IPhysicsConstraintForces<TConstraint>
{
    private readonly Query _bodies, _constraints;
    private readonly BodyColumns _columns = new();
    private readonly PhysicsEventBuffer _events;
    private readonly CommandBuffer _commands;
    private readonly JobResourceAccess[] _extraAccesses;
    public BreakConstraints(World world, Query bodies, Query constraints, PhysicsEventBuffer events,
        ReadOnlySpan<JobResourceAccess> extraAccesses = default)
    {
        _bodies = bodies; _constraints = constraints; _events = events; _commands = new(world);
        _extraAccesses = extraAccesses.ToArray();
    }
    public JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency)
    {
        Scheduler scheduler = (context.World.Execution!.Scheduler!);
        JobHandle checkedLoads = PhysicsBodyJobs.Schedule<ReadBodyFields, Check>(_bodies, _columns, in context, new Check(this), dependency);
        return scheduler.Schedule(new Remove(_commands), ((WorldJobs)context.World.Execution!).StructureWrite, dependency: checkedLoads);
    }
    private readonly struct Check(BreakConstraints<TConstraint, TForces> stage) : IPhysicsBodyWork
    {
        public void AddInputAccesses(World world, List<JobResourceAccess> accesses) =>
            TForces.AddForceInputAccesses(world, stage._constraints, accesses);
        public void AddAccesses(World world, List<JobResourceAccess> accesses)
        {
            TForces.AddForceAccesses(world, stage._constraints, accesses);
            accesses.AddRange(((WorldJobs)world.Execution!).CollectAccess(new SystemAccesses().ReadRandom<ConstraintBreakSettings>()));
            accesses.Add(stage._events.WriteAccess); accesses.AddRange(stage._extraAccesses);
        }
        public void Execute(in PhysicsStepContext context, scoped PhysicsBodies bodies)
        {
            stage._commands.Clear();
            using var limits = context.World.OpenRead<ConstraintBreakSettings>();
            using var constraints = stage._constraints.OpenRead();
            foreach (QueryChunk chunk in constraints)
            {
                ReadOnlySpan<TConstraint> values = chunk.Read<TConstraint>();
                for (int row = 0; row < chunk.Count; row++)
                {
                    Entity entity = chunk.Entities[row];
                    if (!limits.Has(entity)) continue;
                    ref readonly ConstraintBreakSettings limit = ref limits.Read(entity);
                    int count = TForces.GetBodyCount(in values[row], context.World, entity, in context);
                    for (int index = 0; index < count; index++)
                    {
                        Entity body = TForces.GetBody(in values[row], context.World, entity, index, in context);
                        TForces.GetBodyForce(in values[row], context.World, entity, body, bodies, in context, out Vector3 force, out Vector3 torque);
                        if (force.LengthSquared() <= limit.MaximumForce * limit.MaximumForce
                            && torque.LengthSquared() <= limit.MaximumTorque * limit.MaximumTorque) continue;
                        stage._events.Add(new PhysicsEvent
                        {
                            Kind = PhysicsEventKind.Break,
                            Subject = entity,
                            Body = body,
                            First = TForces.GetBody(in values[row], context.World, entity, 0, in context),
                            Second = count > 1 ? TForces.GetBody(in values[row], context.World, entity, 1, in context) : default,
                            Substep = context.Substep,
                            Time = context.EndTime,
                            Force = force,
                            Torque = torque
                        });
                        stage._commands.Destroy(entity); break;
                    }
                }
            }
        }
    }
    private readonly struct Remove(CommandBuffer commands) : IJob { public void Execute() => commands.Playback(); }
    public void Dispose() => _commands.Dispose();
}
