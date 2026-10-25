using System.Numerics;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;

namespace SomeEngine.Physics;

/// <summary>Current world force and torque from the concrete XPBD multiplier and Jacobian.</summary>
public interface IXpbdConstraintForces<TConstraint> where TConstraint : struct, IComponent
{
    static abstract int GetBodyCount(in TConstraint constraint, scoped QueryRow row, in PhysicsStepContext context);
    static abstract Entity GetBody(in TConstraint constraint, scoped QueryRow row, int index, in PhysicsStepContext context);
    static abstract void GetBodyForce(in TConstraint constraint, scoped QueryRow row, Entity body,
        scoped PhysicsBodies bodies, in PhysicsStepContext context, out Vector3 force, out Vector3 torque);
}

/// <summary>Includes current XPBD forces in AVBD while XPBD retains projection and multiplier updates.</summary>
public readonly struct XpbdForces<TConstraint, TProjection> : IAvbdConstraints
    where TConstraint : struct, IComponent
    where TProjection : struct, IXpbdConstraint<TConstraint>, IXpbdConstraintForces<TConstraint>
{
    private readonly QueryHandle _constraints;
    private readonly AvbdBodyConstraints _byBody;

    public XpbdForces(QueryHandle constraints)
    {
        _constraints = constraints;
        _byBody = new();
    }

    public void AddAccesses(World world, List<JobResourceAccess> accesses)
    {
        PhysicsQueryAccess.AddAccesses(world, _constraints, accesses);
        accesses.Add(JobResourceAccess.Exclusive(WorldJobs.GetScheduler(world).GetResourceToken(_byBody)));
    }

    public void Initialize(in PhysicsStepContext context, scoped PhysicsBodies bodies, float retention)
    {
        _byBody.Clear();
        var read = new ReadParticipants(context, _byBody);
        context.World.ExecuteQuery(_constraints, ref read);
    }

    public void AccumulateBodyEquation(in PhysicsStepContext context, Entity body, scoped PhysicsBodies bodies,
        float initialErrorFraction, ref AvbdBodyEquation equation)
    {
        var read = new ReadForces(context, body, bodies, _byBody.Read(body), ref equation);
        context.World.ExecuteQuery(_constraints, ref read);
    }

    public void UpdateDuals(in PhysicsStepContext context, scoped PhysicsBodies bodies, float initialErrorFraction) { }

    private readonly struct ReadParticipants(PhysicsStepContext context, AvbdBodyConstraints byBody) : IQueryExecution
    {
        public void Execute(scoped QueryCursor query)
        {
            foreach (QueryRow row in query.Rows)
            {
                ref readonly TConstraint constraint = ref row.ReadRef<TConstraint>();
                for (int index = 0; index < TProjection.GetBodyCount(in constraint, row, in context); index++)
                    byBody.Add(TProjection.GetBody(in constraint, row, index, in context), row.Entity);
            }
        }
    }

    private readonly ref struct ReadForces(PhysicsStepContext context, Entity body, PhysicsBodies bodies,
        ReadOnlySpan<Entity> constraints, ref AvbdBodyEquation equation) : IQueryExecution
    {
        private readonly PhysicsBodies _bodies = bodies;
        private readonly ReadOnlySpan<Entity> _constraints = constraints;
        private readonly ref AvbdBodyEquation _equation = ref equation;

        public void Execute(scoped QueryCursor query)
        {
            foreach (Entity entity in _constraints)
            {
                query.TryGetRow(entity, out QueryRow row);
                TProjection.GetBodyForce(in row.ReadRef<TConstraint>(), row, body, _bodies, in context,
                    out Vector3 force, out Vector3 torque);
                _equation.LinearGradient -= force;
                _equation.AngularGradient -= torque;
            }
        }
    }
}
