using System.Numerics;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;

namespace SomeEngine.Physics;

/// <summary>Current world force and torque from the concrete XPBD multiplier and Jacobian.</summary>
public interface IXpbdConstraintForces<TConstraint> : IPhysicsConstraintForces<TConstraint> where TConstraint : struct, IComponent;

/// <summary>Includes current XPBD forces in AVBD while XPBD retains projection and multiplier updates.</summary>
public readonly struct XpbdForces<TConstraint, TProjection> : IAvbdConstraints, IAvbdGraphConstraints
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

    public int QueryCount => 1;
    public QueryHandle GetQuery(int index) => _constraints;
    public void ReadActivityErrors(in PhysicsStepContext context, scoped EntityQueryViews queries, int offset,
        scoped PhysicsBodies bodies, ref PhysicsActivityErrors errors) =>
        PhysicsActivityRows<TConstraint, TProjection>.Read(new(queries.Get(offset), queries.Get(2)), bodies, in context, ref errors);
    public void AddResources(World world, List<JobResourceAccess> accesses, PhysicsGraphPhase phase)
    {
        JobResourceToken token = WorldJobs.GetScheduler(world).GetResourceToken(_byBody);
        accesses.Add(phase == PhysicsGraphPhase.Initialize ? JobResourceAccess.Write(token) : JobResourceAccess.Read(token));
    }
    public void ReadGraph(in PhysicsStepContext context, ReadOnlySpan<QueryHandle> queries, int offset,
        ref PhysicsConstraintGraphBuilder graph)
    {
        var read = new ReadGraphParticipants(context, graph, offset);
        context.World.ExecuteReadQuery(queries[offset], ref read,
            static (scoped QueryCursor rows, ref ReadGraphParticipants state) => state.Execute(rows));
    }
    public void Initialize(in PhysicsStepContext context, scoped EntityQueryViews queries, int offset,
        scoped PhysicsBodies bodies, float retention)
    {
        _byBody.Clear();
        XpbdForceRows<TConstraint, TProjection>.Initialize(new(queries.Get(offset)), in context, _byBody);
    }
    public void AccumulateBodyEquation(in PhysicsStepContext context, scoped EntityQueryViews queries, int offset,
        Entity body, scoped PhysicsBodies bodies, float initialErrorFraction, ref AvbdBodyEquation equation) =>
        XpbdForceRows<TConstraint, TProjection>.Accumulate(new(queries.Get(offset)), in context, body, bodies, _byBody.Read(body), ref equation);
    public void UpdateDuals(in PhysicsStepContext context, scoped EntityQueryViews queries, int offset,
        scoped PhysicsBodies bodies, float initialErrorFraction) { }

    private ref struct ReadGraphParticipants(PhysicsStepContext context, PhysicsConstraintGraphBuilder graph, int query)
    {
        private PhysicsConstraintGraphBuilder _graph = graph;
        public void Execute(scoped QueryCursor rows)
        { foreach (QueryRow row in rows.Rows) _graph.Add<TConstraint, TProjection>(query, row, in context); }
    }

    private readonly struct ReadParticipants(PhysicsStepContext context, AvbdBodyConstraints byBody) : IQueryExecution
    {
        public void Execute(scoped QueryCursor query)
            => XpbdForceRows<TConstraint, TProjection>.Initialize(new(query), in context, byBody);
    }

    private readonly ref struct ReadForces(PhysicsStepContext context, Entity body, PhysicsBodies bodies,
        ReadOnlySpan<Entity> constraints, ref AvbdBodyEquation equation) : IQueryExecution
    {
        private readonly PhysicsBodies _bodies = bodies;
        private readonly ReadOnlySpan<Entity> _constraints = constraints;
        private readonly ref AvbdBodyEquation _equation = ref equation;

        public void Execute(scoped QueryCursor query)
            => XpbdForceRows<TConstraint, TProjection>.Accumulate(new(query), in context, body, _bodies, _constraints, ref _equation);
    }
}

internal static class XpbdForceRows<TConstraint, TProjection>
    where TConstraint : struct, IComponent
    where TProjection : struct, IXpbdConstraint<TConstraint>, IXpbdConstraintForces<TConstraint>
{
    internal static void Initialize(scoped PhysicsQueryRows rows, in PhysicsStepContext context, AvbdBodyConstraints byBody)
    {
        foreach (QueryRow row in rows.Rows)
        {
            ref readonly TConstraint constraint = ref row.ReadRef<TConstraint>();
            for (int index = 0; index < TProjection.GetBodyCount(in constraint, row, in context); index++)
                byBody.Add(TProjection.GetBody(in constraint, row, index, in context), row.Entity);
        }
    }
    internal static void Accumulate(scoped PhysicsQueryRows rows, in PhysicsStepContext context, Entity body,
        scoped PhysicsBodies bodies, ReadOnlySpan<Entity> constraints, ref AvbdBodyEquation equation)
    {
        foreach (Entity entity in constraints)
        {
            rows.TryGetRow(entity, out QueryRow row);
            TProjection.GetBodyForce(in row.ReadRef<TConstraint>(), row, body, bodies, in context, out Vector3 force, out Vector3 torque);
            equation.LinearGradient -= force;
            equation.AngularGradient -= torque;
        }
    }
}
