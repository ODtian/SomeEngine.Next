using SomeEngine.ECS;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;

namespace SomeEngine.Physics;

public static class SolveXpbd
{
    public static SolveXpbd<TConstraints> Create<TConstraints>(World world, QueryHandle bodies,
        TConstraints constraints, int iterations) where TConstraints : struct, IXpbdConstraints =>
        new(world, bodies, constraints, iterations);
}

/// <summary>Projects every selected constraint set against the current body components in each iteration.</summary>
public readonly struct SolveXpbd<TConstraints> : IPhysicsStage where TConstraints : struct, IXpbdConstraints
{
    private readonly QueryHandle _bodies;
    private readonly TConstraints _constraints;
    private readonly int _iterations;
    private readonly JobResourceAccess[] _accesses;

    public SolveXpbd(World world, QueryHandle bodies, TConstraints constraints, int iterations)
    {
        _bodies = bodies;
        _constraints = constraints;
        _iterations = iterations;
        var accesses = new List<JobResourceAccess>();
        PhysicsQueryAccess.AddAccesses(world, bodies, accesses);
        constraints.AddAccesses(world, accesses);
        _accesses = accesses.ToArray();
    }

    public InitializeXpbd<TConstraints> Initialize() => new(this);
    public IterateXpbd<TConstraints> Iterate(int iterations) => new(this, iterations);

    public JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency) =>
        ScheduleQuery(in context, new SolveConstraints(context, _constraints, _iterations), dependency);

    internal JobHandle ScheduleInitialization(in PhysicsStepContext context, JobHandle dependency) =>
        ScheduleQuery(in context, new InitializeConstraints(context, _constraints), dependency);

    internal JobHandle ScheduleIterations(in PhysicsStepContext context, int iterations, JobHandle dependency) =>
        ScheduleQuery(in context, new ProjectConstraints(context, _constraints, iterations), dependency);

    private JobHandle ScheduleQuery<TExecution>(in PhysicsStepContext context, TExecution execution, JobHandle dependency)
        where TExecution : struct, IQueryExecution => WorldJobs.GetScheduler(context.World).Schedule(
            new ReadBodies<TExecution>(context.World, _bodies, execution), _accesses, dependency);

    private readonly struct ReadBodies<TExecution>(World world, QueryHandle bodies, TExecution execution) : IJob
        where TExecution : struct, IQueryExecution
    {
        public void Execute()
        {
            var work = execution;
            world.ExecuteQuery(bodies, ref work);
        }
    }

    private readonly struct SolveConstraints(PhysicsStepContext context, TConstraints constraints, int iterations) : IQueryExecution
    {
        public void Execute(scoped QueryCursor query)
        {
            XpbdIterationLoop.Initialize(in context, constraints);
            new ProjectConstraints(context, constraints, iterations).Execute(query);
        }
    }

    private readonly struct InitializeConstraints(PhysicsStepContext context, TConstraints constraints) : IQueryExecution
    {
        public void Execute(scoped QueryCursor query) => XpbdIterationLoop.Initialize(in context, constraints);
    }

    private readonly struct ProjectConstraints(PhysicsStepContext context, TConstraints constraints, int iterations) : IQueryExecution
    {
        public void Execute(scoped QueryCursor query)
        {
            var bodies = new PhysicsBodies(query);
            XpbdIterationLoop.Iterate(in context, bodies, constraints, iterations);
        }
    }
}

/// <summary>Starts the selected XPBD multipliers once for the substep.</summary>
public readonly struct InitializeXpbd<TConstraints>(SolveXpbd<TConstraints> solve) : IPhysicsStage
    where TConstraints : struct, IXpbdConstraints
{
    public JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency) => solve.ScheduleInitialization(in context, dependency);
}

/// <summary>Resumes projection with the component multipliers already established for this substep.</summary>
public readonly struct IterateXpbd<TConstraints>(SolveXpbd<TConstraints> solve, int iterations) : IPhysicsStage
    where TConstraints : struct, IXpbdConstraints
{
    public JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency) => solve.ScheduleIterations(in context, iterations, dependency);
}
