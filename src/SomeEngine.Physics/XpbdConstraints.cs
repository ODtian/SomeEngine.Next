using SomeEngine.ECS;
using SomeEngine.ECS.Queries;
using SomeEngine.Job;

namespace SomeEngine.Physics;

/// <summary>Projects one concrete constraint while its data and participating bodies remain borrowed.</summary>
public interface IXpbdConstraint<TConstraint> where TConstraint : struct, IComponent
{
    static abstract void Initialize(in TConstraint constraint, scoped QueryRow row, in PhysicsStepContext context);
    static abstract void Project(in TConstraint constraint, scoped QueryRow row,
        scoped PhysicsBodies bodies, in PhysicsStepContext context);
}

/// <summary>Concrete constraint sets participating in every iteration of one XPBD solve.</summary>
public interface IXpbdConstraintIterations
{
    void Initialize(in PhysicsStepContext context);
    void Project(in PhysicsStepContext context, scoped PhysicsBodies bodies);
}

public interface IXpbdConstraints : IXpbdConstraintIterations
{
    void AddAccesses(World world, List<JobResourceAccess> accesses);
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

public readonly struct XpbdConstraints<TConstraint, TProjection>(QueryHandle constraints) : IXpbdConstraints
    where TConstraint : struct, IComponent
    where TProjection : struct, IXpbdConstraint<TConstraint>
{
    public void AddAccesses(World world, List<JobResourceAccess> accesses) =>
        PhysicsQueryAccess.AddAccesses(world, constraints, accesses);

    public void Initialize(in PhysicsStepContext context)
    {
        var execution = new InitializeConstraints(context);
        context.World.ExecuteQuery(constraints, ref execution);
    }

    public void Project(in PhysicsStepContext context, scoped PhysicsBodies bodies)
    {
        var execution = new ProjectConstraints(bodies, context);
        context.World.ExecuteQuery(constraints, ref execution);
    }

    private readonly struct InitializeConstraints(PhysicsStepContext context) : IQueryExecution
    {
        private readonly PhysicsStepContext _context = context;

        public void Execute(scoped QueryCursor query)
        {
            XpbdConstraintRows<TConstraint, TProjection>.Initialize(new(query), in _context);
        }
    }

    private readonly ref struct ProjectConstraints(PhysicsBodies bodies, PhysicsStepContext context) : IQueryExecution
    {
        private readonly PhysicsBodies _bodies = bodies;
        private readonly PhysicsStepContext _context = context;

        public void Execute(scoped QueryCursor query)
        {
            XpbdConstraintRows<TConstraint, TProjection>.Project(new(query), _bodies, in _context);
        }
    }
}

public readonly struct XpbdConstraintGroups<TFirst, TSecond>(TFirst first, TSecond second) : IXpbdConstraints
    where TFirst : struct, IXpbdConstraints
    where TSecond : struct, IXpbdConstraints
{
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
