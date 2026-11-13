using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.Job;

namespace SomeEngine.Physics;

/// <summary>A typed kernel for one persistent XPBD constraint's original values.</summary>
public interface IXpbdConstraint<TConstraint> : IPhysicsConstraintBodies<TConstraint> where TConstraint : struct, IComponent
{
    static virtual void AddInputAccesses(World world, Query members, List<JobResourceAccess> accesses) { }
    static abstract void AddAccesses(World world, Query members, List<JobResourceAccess> accesses);
    void Bind(World world, Query members);
    ref readonly TConstraint ReadConstraint(Entity entity);
    int ProjectRange(ReadOnlySpan<PhysicsConstraintIdentity> constraints, scoped PhysicsBodies bodies, in PhysicsStepContext context) => 0;
    void Initialize(in TConstraint constraint, World world, Entity entity, in PhysicsStepContext context);
    void Project(in TConstraint constraint, World world, Entity entity, scoped PhysicsBodies bodies, in PhysicsStepContext context);
}
public interface IXpbdConstraintIterations
{
    void Initialize(in PhysicsStepContext context);
    void Project(in PhysicsStepContext context, scoped PhysicsBodies bodies);
}
public interface IXpbdConstraints : IXpbdConstraintIterations, IPhysicsConstraintAccess
{
    void Bind(in PhysicsStepContext context) { }
}
internal static class XpbdIterationLoop
{
    internal static void Initialize<TConstraints>(in PhysicsStepContext context, TConstraints constraints)
        where TConstraints : IXpbdConstraintIterations, allows ref struct => constraints.Initialize(in context);
    internal static void Iterate<TConstraints>(in PhysicsStepContext context, scoped PhysicsBodies bodies,
        TConstraints constraints, int iterations) where TConstraints : IXpbdConstraintIterations, allows ref struct
    {
        for (int iteration = 0; iteration < iterations; iteration++) constraints.Project(in context, bodies);
    }
}
public readonly partial struct XpbdConstraints<TConstraint, TProjection>(Query constraints) : IXpbdConstraints
    where TConstraint : struct, IComponent where TProjection : struct, IXpbdConstraint<TConstraint>
{
    private readonly TProjection _math = new();
    public void Bind(in PhysicsStepContext context) => _math.Bind(context.World, constraints);
    public void AddInputAccesses(World world, List<JobResourceAccess> accesses) => TProjection.AddInputAccesses(world, constraints, accesses);
    public void AddAccesses(World world, List<JobResourceAccess> accesses)
    {
        TProjection.AddAccesses(world, constraints, accesses);
    }
    public void Initialize(in PhysicsStepContext context)
    {
        using var scope = constraints.OpenRead();
        foreach (QueryChunk chunk in scope)
        {
            ReadOnlySpan<TConstraint> values = chunk.Read<TConstraint>();
            for (int row = 0; row < chunk.Count; row++)
                _math.Initialize(in values[row], context.World, chunk.Entities[row], in context);
        }
    }
    public void Project(in PhysicsStepContext context, scoped PhysicsBodies bodies)
    {
        using var scope = constraints.OpenRead();
        foreach (QueryChunk chunk in scope)
        {
            ReadOnlySpan<TConstraint> values = chunk.Read<TConstraint>();
            for (int row = 0; row < chunk.Count; row++)
                _math.Project(in values[row], context.World, chunk.Entities[row], bodies, in context);
        }
    }
}

public readonly struct XpbdConstraintGroups<TFirst, TSecond>(TFirst first, TSecond second) : IXpbdConstraints
    where TFirst : struct, IXpbdConstraints
    where TSecond : struct, IXpbdConstraints
{
    public void Bind(in PhysicsStepContext context) { first.Bind(in context); second.Bind(in context); }
    public void AddInputAccesses(World world, List<JobResourceAccess> accesses)
    { first.AddInputAccesses(world, accesses); second.AddInputAccesses(world, accesses); }
    public void AddAccesses(World world, List<JobResourceAccess> accesses)
    {
        first.AddAccesses(world, accesses);
        second.AddAccesses(world, accesses);
    }

    public void Initialize(in PhysicsStepContext context)
    {
        first.Initialize(in context);
        second.Initialize(in context);
    }

    public void Project(in PhysicsStepContext context, scoped PhysicsBodies bodies)
    {
        first.Project(in context, bodies);
        second.Project(in context, bodies);
    }
}

public static class XpbdConstraintGroups
{
    public static XpbdConstraintGroups<TFirst, TSecond> And<TFirst, TSecond>(this TFirst first, TSecond second)
        where TFirst : struct, IXpbdConstraints
        where TSecond : struct, IXpbdConstraints => new(first, second);
}
