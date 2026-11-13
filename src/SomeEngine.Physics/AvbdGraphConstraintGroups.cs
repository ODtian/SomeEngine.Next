using System.Numerics;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.Job;

namespace SomeEngine.Physics;

public readonly partial struct AvbdConstraints<TConstraint, TMath> : IAvbdGraphConstraints
    where TConstraint : struct, IComponent where TMath : struct, IAvbdConstraint<TConstraint>
{
    public int QueryCount => 1;
    public Query? GetQuery(int index) => constraints;
    public void AddResources(World world, List<JobResourceAccess> accesses, PhysicsGraphPhase phase)
    { if (phase == PhysicsGraphPhase.Metadata) AddAccesses(world, accesses); }
    public void ReadGraph(in PhysicsStepContext context, int firstSet, ref PhysicsConstraintGraphBuilder graph)
    {
        using var scope = constraints.OpenRead();
        foreach (QueryChunk chunk in scope)
        {
            ReadOnlySpan<TConstraint> values = chunk.Read<TConstraint>();
            for (int row = 0; row < chunk.Count; row++) graph.Add<TConstraint, TMath>(firstSet,
                context.World, chunk.Entities[row], in values[row], in context);
        }
    }
    public void ReadActivityErrors(in PhysicsStepContext context, scoped PhysicsBodies bodies, ref PhysicsActivityErrors errors)
    {
        using var scope = constraints.OpenRead();
        foreach (QueryChunk chunk in scope)
        {
            ReadOnlySpan<TConstraint> values = chunk.Read<TConstraint>();
            for (int row = 0; row < chunk.Count; row++)
                errors.Add<TConstraint, TMath>(context.World, chunk.Entities[row], in values[row], bodies, in context);
        }
    }
}

public readonly struct AvbdGraphConstraintGroups<TFirst, TSecond>(TFirst first, TSecond second) : IAvbdGraphConstraints
    where TFirst : struct, IAvbdGraphConstraints where TSecond : struct, IAvbdGraphConstraints
{
    public int QueryCount => first.QueryCount + second.QueryCount;
    public bool HasGraphChanges => first.HasGraphChanges || second.HasGraphChanges;
    public Query? GetQuery(int index) => index < first.QueryCount ? first.GetQuery(index) : second.GetQuery(index - first.QueryCount);
    public void Bind(in PhysicsStepContext context) { first.Bind(in context); second.Bind(in context); }
    public void AddInputAccesses(World world, List<JobResourceAccess> accesses)
    { first.AddInputAccesses(world, accesses); second.AddInputAccesses(world, accesses); }
    public void AddAccesses(World world, List<JobResourceAccess> accesses) { first.AddAccesses(world, accesses); second.AddAccesses(world, accesses); }
    public void AddResources(World world, List<JobResourceAccess> accesses, PhysicsGraphPhase phase)
    { first.AddResources(world, accesses, phase); second.AddResources(world, accesses, phase); }
    public void RefreshGraph(in PhysicsStepContext context, scoped PhysicsBodies bodies)
    { first.RefreshGraph(in context, bodies); second.RefreshGraph(in context, bodies); }
    public void ReadGraph(in PhysicsStepContext context, int firstSet, ref PhysicsConstraintGraphBuilder graph)
    { first.ReadGraph(in context, firstSet, ref graph); second.ReadGraph(in context, firstSet + first.QueryCount, ref graph); }
    public void ReadActivityErrors(in PhysicsStepContext context, scoped PhysicsBodies bodies, ref PhysicsActivityErrors errors)
    { first.ReadActivityErrors(in context, bodies, ref errors); second.ReadActivityErrors(in context, bodies, ref errors); }
    public void Initialize(in PhysicsStepContext context, scoped PhysicsBodies bodies, float retention)
    { first.Initialize(in context, bodies, retention); second.Initialize(in context, bodies, retention); }
    public void AccumulateBodyEquation(in PhysicsStepContext context, Entity body, scoped PhysicsBodies bodies, float fraction, ref AvbdBodyEquation equation)
    { first.AccumulateBodyEquation(in context, body, bodies, fraction, ref equation); second.AccumulateBodyEquation(in context, body, bodies, fraction, ref equation); }
    public void AdjustBodyChange(in PhysicsStepContext context, Entity body, scoped PhysicsBodies bodies,
        in AvbdBodyEquation equation, ref Vector3 translation, ref Vector3 rotation)
    {
        first.AdjustBodyChange(in context, body, bodies, in equation, ref translation, ref rotation);
        second.AdjustBodyChange(in context, body, bodies, in equation, ref translation, ref rotation);
    }
    public void UpdateDuals(in PhysicsStepContext context, scoped PhysicsBodies bodies, float fraction)
    { first.UpdateDuals(in context, bodies, fraction); second.UpdateDuals(in context, bodies, fraction); }
}
public static class AvbdGraphConstraints
{
    public static AvbdGraphConstraintGroups<TFirst, TSecond> Combine<TFirst, TSecond>(TFirst first, TSecond second)
        where TFirst : struct, IAvbdGraphConstraints where TSecond : struct, IAvbdGraphConstraints => new(first, second);
}
