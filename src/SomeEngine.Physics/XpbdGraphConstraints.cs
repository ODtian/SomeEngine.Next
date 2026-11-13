using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.Job;

namespace SomeEngine.Physics;

/// <summary>Typed XPBD projections selected by a graph constraint identity.</summary>
public interface IXpbdGraphConstraints : IXpbdConstraints, IPhysicsGraphInputs
{
    void Project(in PhysicsStepContext context, int set, Entity constraint, int element, scoped PhysicsBodies bodies, bool useSimd);
    void ProjectRange(in PhysicsStepContext context, int set, ReadOnlySpan<PhysicsConstraintIdentity> constraints,
        scoped PhysicsBodies bodies, bool useSimd)
    {
        foreach (PhysicsConstraintIdentity value in constraints) Project(in context, set, value.Entity, value.Element, bodies, useSimd);
    }
}
public readonly partial struct XpbdConstraints<TConstraint, TProjection> : IXpbdGraphConstraints
    where TConstraint : struct, IComponent where TProjection : struct, IXpbdConstraint<TConstraint>
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
            for (int row = 0; row < chunk.Count; row++) graph.Add<TConstraint, TProjection>(firstSet,
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
                errors.Add<TConstraint, TProjection>(context.World, chunk.Entities[row], in values[row], bodies, in context);
        }
    }
    public void Project(in PhysicsStepContext context, int set, Entity entity, int element, scoped PhysicsBodies bodies, bool useSimd)
    {
        ref readonly TConstraint value = ref _math.ReadConstraint(entity);
        _math.Project(in value, context.World, entity, bodies, in context);
    }
    public void ProjectRange(in PhysicsStepContext context, int set, ReadOnlySpan<PhysicsConstraintIdentity> rows,
        scoped PhysicsBodies bodies, bool useSimd)
    {
        int completed = useSimd ? _math.ProjectRange(rows, bodies, in context) : 0;
        foreach (PhysicsConstraintIdentity row in rows[completed..]) Project(in context, set, row.Entity, row.Element, bodies, false);
    }

}
public readonly struct XpbdGraphConstraintGroups<TFirst, TSecond>(TFirst first, TSecond second) : IXpbdGraphConstraints
    where TFirst : struct, IXpbdGraphConstraints where TSecond : struct, IXpbdGraphConstraints
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
    public void Initialize(in PhysicsStepContext context) { first.Initialize(in context); second.Initialize(in context); }
    public void Project(in PhysicsStepContext context, scoped PhysicsBodies bodies)
    { first.Project(in context, bodies); second.Project(in context, bodies); }
    public void Project(in PhysicsStepContext context, int set, Entity entity, int element, scoped PhysicsBodies bodies, bool useSimd)
    {
        if (set < first.QueryCount) first.Project(in context, set, entity, element, bodies, useSimd);
        else second.Project(in context, set - first.QueryCount, entity, element, bodies, useSimd);
    }
    public void ProjectRange(in PhysicsStepContext context, int set, ReadOnlySpan<PhysicsConstraintIdentity> rows,
        scoped PhysicsBodies bodies, bool useSimd)
    {
        if (set < first.QueryCount) first.ProjectRange(in context, set, rows, bodies, useSimd);
        else second.ProjectRange(in context, set - first.QueryCount, rows, bodies, useSimd);
    }

}
public static class XpbdGraphConstraints
{
    public static XpbdGraphConstraintGroups<TFirst, TSecond> Combine<TFirst, TSecond>(TFirst first, TSecond second)
        where TFirst : struct, IXpbdGraphConstraints where TSecond : struct, IXpbdGraphConstraints => new(first, second);
}
