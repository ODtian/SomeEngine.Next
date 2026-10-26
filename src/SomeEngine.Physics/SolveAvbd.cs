using SomeEngine.ECS;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;

namespace SomeEngine.Physics;

public static class SolveAvbd
{
    public static SolveAvbd<TConstraints> Create<TConstraints>(World world, QueryHandle bodies,
        TConstraints constraints, int iterations, float multiplierRetention, float initialErrorFraction)
        where TConstraints : struct, IAvbdConstraints => new(world, bodies, constraints, iterations,
            multiplierRetention, initialErrorFraction);

    /// <summary>Repeats each body's Newton update with fixed multipliers before the round's dual update.</summary>
    public static SolveAvbd<TConstraints> Create<TConstraints>(World world, QueryHandle bodies,
        TConstraints constraints, int iterations, int bodyIterations, float multiplierRetention, float initialErrorFraction)
        where TConstraints : struct, IAvbdConstraints => new(world, bodies, constraints, iterations,
            bodyIterations, multiplierRetention, initialErrorFraction);
}

/// <summary>Body descent in its active translation/rotation coordinates followed by selected ALM dual updates.</summary>
public readonly struct SolveAvbd<TConstraints> : IPhysicsStage where TConstraints : struct, IAvbdConstraints
{
    private readonly QueryHandle _bodies;
    private readonly TConstraints _constraints;
    private readonly int _iterations;
    private readonly int _bodyIterations;
    private readonly float _retention;
    private readonly float _initialErrorFraction;
    private readonly JobResourceAccess[] _accesses;

    public SolveAvbd(World world, QueryHandle bodies, TConstraints constraints, int iterations,
        float multiplierRetention, float initialErrorFraction)
        : this(world, bodies, constraints, iterations, 1, multiplierRetention, initialErrorFraction) { }

    public SolveAvbd(World world, QueryHandle bodies, TConstraints constraints, int iterations, int bodyIterations,
        float multiplierRetention, float initialErrorFraction)
    {
        _bodies = bodies;
        _constraints = constraints;
        _iterations = iterations;
        _bodyIterations = bodyIterations;
        _retention = multiplierRetention;
        _initialErrorFraction = initialErrorFraction;
        var accesses = new List<JobResourceAccess>();
        PhysicsQueryAccess.AddAccesses(world, bodies, accesses);
        constraints.AddAccesses(world, accesses);
        _accesses = accesses.ToArray();
    }

    public InitializeAvbd<TConstraints> Initialize() => new(this);
    public IterateAvbd<TConstraints> Iterate(int iterations) => new(this, iterations);

    public JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency) =>
        ScheduleQuery(in context, new SolveConstraints(context, _constraints, _iterations, _bodyIterations,
            _retention, _initialErrorFraction), dependency);

    internal JobHandle ScheduleInitialization(in PhysicsStepContext context, JobHandle dependency) =>
        ScheduleQuery(in context, new InitializeConstraints(context, _constraints, _retention), dependency);

    internal JobHandle ScheduleIterations(in PhysicsStepContext context, int iterations, JobHandle dependency) =>
        ScheduleQuery(in context, new IterateConstraints(context, _constraints, iterations, _bodyIterations, _initialErrorFraction), dependency);

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

    private readonly struct SolveConstraints(PhysicsStepContext context, TConstraints constraints,
        int iterations, int bodyIterations, float retention, float initialErrorFraction) : IQueryExecution
    {
        public void Execute(scoped QueryCursor query)
        {
            new InitializeConstraints(context, constraints, retention).Execute(query);
            new IterateConstraints(context, constraints, iterations, bodyIterations, initialErrorFraction).Execute(query);
        }
    }

    private readonly struct InitializeConstraints(PhysicsStepContext context, TConstraints constraints, float retention) : IQueryExecution
    {
        public void Execute(scoped QueryCursor query)
        {
            var bodies = new PhysicsBodies(query);
            foreach (QueryRow row in query.Rows)
            {
                ref readonly BodyMass mass = ref row.ReadRef<BodyMass>();
                if (mass.Motion == BodyMotion.Dynamic)
                    row.ReadWrite<AvbdBodyEquation>().Initialize(in mass);
            }
            constraints.Initialize(in context, bodies, retention);
        }
    }

    private readonly struct IterateConstraints(PhysicsStepContext context, TConstraints constraints,
        int iterations, int bodyIterations, float initialErrorFraction) : IQueryExecution
    {
        public void Execute(scoped QueryCursor query)
        {
            var bodies = new PhysicsBodies(query);
            for (int iteration = 0; iteration < iterations; iteration++)
            {
                foreach (QueryRow row in query.Rows)
                    AvbdBodyIteration.Update(row, bodies, in context, constraints, bodyIterations, initialErrorFraction);
                constraints.UpdateDuals(in context, bodies, initialErrorFraction);
            }
        }
    }
}

/// <summary>Initializes body equations, constraint histories and body indices once for the substep.</summary>
public readonly struct InitializeAvbd<TConstraints>(SolveAvbd<TConstraints> solve) : IPhysicsStage
    where TConstraints : struct, IAvbdConstraints
{
    public JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency) => solve.ScheduleInitialization(in context, dependency);
}

/// <summary>Resumes Newton and dual iterations using the existing component state and body indices.</summary>
public readonly struct IterateAvbd<TConstraints>(SolveAvbd<TConstraints> solve, int iterations) : IPhysicsStage
    where TConstraints : struct, IAvbdConstraints
{
    public JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency) => solve.ScheduleIterations(in context, iterations, dependency);
}
