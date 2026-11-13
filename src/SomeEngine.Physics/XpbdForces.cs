using System.Numerics;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.Job;

namespace SomeEngine.Physics;

public interface IXpbdConstraintForces<TConstraint> : IPhysicsConstraintForces<TConstraint> where TConstraint : struct, IComponent;

/// <summary>Reads current XPBD reactions into AVBD while projection retains its original multiplier fields.</summary>
public readonly struct XpbdForces<TConstraint, TProjection>(Query constraints) : IAvbdGraphConstraints
    where TConstraint : struct, IComponent where TProjection : struct, IXpbdConstraint<TConstraint>, IPhysicsConstraintForces<TConstraint>
{
    private readonly AvbdBodyConstraints _byBody = new();
    public int QueryCount => 1;
    public Query? GetQuery(int index) => constraints;
    public void AddInputAccesses(World world, List<JobResourceAccess> accesses) => TProjection.AddForceInputAccesses(world, constraints, accesses);
    public void AddAccesses(World world, List<JobResourceAccess> accesses) => TProjection.AddForceAccesses(world, constraints, accesses);
    public void AddResources(World world, List<JobResourceAccess> accesses, PhysicsGraphPhase phase)
    { if (phase == PhysicsGraphPhase.Metadata) AddAccesses(world, accesses); }
    public void ReadGraph(in PhysicsStepContext context, int firstSet, ref PhysicsConstraintGraphBuilder graph)
    {
        using var scope = constraints.OpenRead();
        foreach (QueryChunk chunk in scope)
        {
            ReadOnlySpan<TConstraint> values = chunk.Read<TConstraint>();
            for (int row = 0; row < chunk.Count; row++)
                graph.Add<TConstraint, TProjection>(firstSet, context.World, chunk.Entities[row], in values[row], in context);
        }
    }
    public void ReadActivityErrors(in PhysicsStepContext context, scoped PhysicsBodies bodies, ref PhysicsActivityErrors errors)
    {
        using var scope = constraints.OpenRead();
        foreach (QueryChunk chunk in scope)
        {
            ReadOnlySpan<TConstraint> values = chunk.Read<TConstraint>();
            for (int row = 0; row < chunk.Count; row++)
                errors.Add<TConstraint, TProjection>(context.World, chunk.Entities[row], in values[row], bodies, in context);
        }
    }
    public void Initialize(in PhysicsStepContext context, scoped PhysicsBodies bodies, float retention)
    {
        _byBody.Clear();
        using var scope = constraints.OpenRead();
        foreach (QueryChunk chunk in scope)
        {
            ReadOnlySpan<TConstraint> values = chunk.Read<TConstraint>();
            for (int row = 0; row < chunk.Count; row++)
            {
                Entity entity = chunk.Entities[row];
                for (int index = 0; index < TProjection.GetBodyCount(in values[row], context.World, entity, in context); index++)
                    _byBody.Add(TProjection.GetBody(in values[row], context.World, entity, index, in context), entity);
            }
        }
    }
    public void AccumulateBodyEquation(in PhysicsStepContext context, Entity body, scoped PhysicsBodies bodies,
        float initialErrorFraction, ref AvbdBodyEquation equation)
    {
        using var values = context.World.OpenRead<TConstraint>();
        foreach (Entity entity in _byBody.Read(body))
        {
            TProjection.GetBodyForce(in values.Read(entity), context.World, entity, body, bodies, in context, out Vector3 force, out Vector3 torque);
            equation.LinearGradient -= force; equation.AngularGradient -= torque;
        }
    }
    public void AdjustBodyChange(in PhysicsStepContext context, Entity body, scoped PhysicsBodies bodies,
        in AvbdBodyEquation equation, ref Vector3 translation, ref Vector3 rotation)
    { }
    public void UpdateDuals(in PhysicsStepContext context, scoped PhysicsBodies bodies, float initialErrorFraction) { }
}
