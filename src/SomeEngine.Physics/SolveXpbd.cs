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

    public JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency) =>
        WorldJobs.GetScheduler(context.World).Schedule(
            new SolveConstraints(context.World, _bodies, _constraints, _iterations, (float)context.Duration),
            _accesses, dependency);

    private readonly struct SolveConstraints(World world, QueryHandle bodies, TConstraints constraints,
        int iterations, float duration) : IJob, IQueryExecution
    {
        public void Execute()
        {
            var execution = this;
            world.ExecuteQuery(bodies, ref execution);
        }

        public void Execute(scoped QueryCursor query)
        {
            var bodies = new PhysicsBodies(query);
            constraints.Initialize(world);
            for (int iteration = 0; iteration < iterations; iteration++)
                constraints.Project(world, bodies, duration);
        }
    }
}
