using SomeEngine.ECS;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;

namespace SomeEngine.Physics;

public static class SolveXpbd
{
    public static SolveXpbd<TConstraints> Create<TConstraints>(World world, Query bodies,
        TConstraints constraints, int iterations) where TConstraints : struct, IXpbdConstraints => new(world, bodies, constraints, iterations);
}
/// <summary>Projects concrete constraints while the original body columns remain borrowed.</summary>
public readonly struct SolveXpbd<TConstraints> : IPhysicsStage where TConstraints : struct, IXpbdConstraints
{
    private readonly Query _bodies;
    private readonly TConstraints _constraints;
    private readonly BodyColumns _columns = new();
    private readonly int _iterations;
    public SolveXpbd(World world, Query bodies, TConstraints constraints, int iterations)
    {
        _bodies = bodies; _constraints = constraints; _iterations = iterations;

    }
    public InitializeXpbd<TConstraints> Initialize() => new(this);
    public IterateXpbd<TConstraints> Iterate(int iterations) => new(this, iterations);
    public JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency) => Schedule(in context, true, _iterations, dependency);
    internal JobHandle Schedule(in PhysicsStepContext context, bool initialize, int iterations, JobHandle dependency) =>
        PhysicsBodyJobs.Schedule<ProjectBodyFields, Project>(_bodies, _columns, in context, new Project(this, initialize, iterations), dependency);
    private readonly struct Project(SolveXpbd<TConstraints> solve, bool initialize, int iterations) : IPhysicsBodyWork
    {
        public void AddInputAccesses(World world, List<JobResourceAccess> accesses) => solve._constraints.AddInputAccesses(world, accesses);
        public void AddAccesses(World world, List<JobResourceAccess> accesses) => solve._constraints.AddAccesses(world, accesses);
        public void Execute(in PhysicsStepContext context, scoped PhysicsBodies bodies)
        {
            solve._constraints.Bind(in context);
            if (initialize) solve._constraints.Initialize(in context);
            XpbdIterationLoop.Iterate(in context, bodies, solve._constraints, iterations);
        }
    }
}
public readonly struct InitializeXpbd<TConstraints>(SolveXpbd<TConstraints> solve) : IPhysicsStage where TConstraints : struct, IXpbdConstraints
{
    public JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency) => solve.Schedule(in context, true, 0, dependency);
}
public readonly struct IterateXpbd<TConstraints>(SolveXpbd<TConstraints> solve, int iterations) : IPhysicsStage where TConstraints : struct, IXpbdConstraints
{
    public JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency) => solve.Schedule(in context, false, iterations, dependency);
}
