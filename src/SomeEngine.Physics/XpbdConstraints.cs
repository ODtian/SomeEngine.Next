using SomeEngine.ECS;
using SomeEngine.ECS.Queries;
using SomeEngine.Job;

namespace SomeEngine.Physics;

/// <summary>Projects one concrete constraint while its data and participating bodies remain borrowed.</summary>
public interface IXpbdConstraint<TConstraint> where TConstraint : struct, IComponent
{
    static abstract void Initialize(in TConstraint constraint, scoped QueryRow row);
    static abstract void Project(in TConstraint constraint, scoped QueryRow row,
        scoped PhysicsBodies bodies, float duration);
}

/// <summary>Concrete constraint sets participating in every iteration of one XPBD solve.</summary>
public interface IXpbdConstraints
{
    void AddAccesses(World world, List<JobResourceAccess> accesses);
    void Initialize(World world);
    void Project(World world, scoped PhysicsBodies bodies, float duration);
}

public readonly struct XpbdConstraints<TConstraint, TProjection>(QueryHandle constraints) : IXpbdConstraints
    where TConstraint : struct, IComponent
    where TProjection : struct, IXpbdConstraint<TConstraint>
{
    public void AddAccesses(World world, List<JobResourceAccess> accesses) =>
        PhysicsQueryAccess.AddAccesses(world, constraints, accesses);

    public void Initialize(World world)
    {
        var execution = new InitializeConstraints();
        world.ExecuteQuery(constraints, ref execution);
    }

    public void Project(World world, scoped PhysicsBodies bodies, float duration)
    {
        var execution = new ProjectConstraints(bodies, duration);
        world.ExecuteQuery(constraints, ref execution);
    }

    private readonly struct InitializeConstraints : IQueryExecution
    {
        public void Execute(scoped QueryCursor query)
        {
            foreach (QueryRow row in query.Rows)
                TProjection.Initialize(in row.ReadRef<TConstraint>(), row);
        }
    }

    private readonly ref struct ProjectConstraints(PhysicsBodies bodies, float duration) : IQueryExecution
    {
        private readonly PhysicsBodies _bodies = bodies;

        public void Execute(scoped QueryCursor query)
        {
            foreach (QueryRow row in query.Rows)
                TProjection.Project(in row.ReadRef<TConstraint>(), row, _bodies, duration);
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

    public void Initialize(World world)
    {
        first.Initialize(world);
        second.Initialize(world);
    }

    public void Project(World world, scoped PhysicsBodies bodies, float duration)
    {
        first.Project(world, bodies, duration);
        second.Project(world, bodies, duration);
    }
}

public static class XpbdConstraintGroups
{
    public static XpbdConstraintGroups<TFirst, TSecond> And<TFirst, TSecond>(this TFirst first, TSecond second)
        where TFirst : struct, IXpbdConstraints
        where TSecond : struct, IXpbdConstraints => new(first, second);
}
