using SomeEngine.ECS;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;

namespace SomeEngine.Physics;

public static class SolveAvbd
{
    public static SolveAvbd<TConstraints> Create<TConstraints>(World world, Query bodies, TConstraints constraints,
        int iterations, float multiplierRetention, float initialErrorFraction) where TConstraints : struct, IAvbdConstraints =>
        new(world, bodies, constraints, iterations, 1, multiplierRetention, initialErrorFraction);
    public static SolveAvbd<TConstraints> Create<TConstraints>(World world, Query bodies, TConstraints constraints,
        int iterations, int bodyIterations, float multiplierRetention, float initialErrorFraction)
        where TConstraints : struct, IAvbdConstraints =>
        new(world, bodies, constraints, iterations, bodyIterations, multiplierRetention, initialErrorFraction);
}

/// <summary>Body descent and dual updates over original ECS body fields and typed constraint values.</summary>
public readonly struct SolveAvbd<TConstraints> : IPhysicsStage where TConstraints : struct, IAvbdConstraints
{
    private readonly Query _bodies;
    private readonly TConstraints _constraints;
    private readonly BodyColumns _columns = new();
    private readonly int _iterations, _bodyIterations;
    private readonly float _retention, _initialErrorFraction;
    public SolveAvbd(World world, Query bodies, TConstraints constraints, int iterations,
        float multiplierRetention, float initialErrorFraction)
        : this(world, bodies, constraints, iterations, 1, multiplierRetention, initialErrorFraction) { }
    public SolveAvbd(World world, Query bodies, TConstraints constraints, int iterations, int bodyIterations,
        float multiplierRetention, float initialErrorFraction)
    {
        _bodies = bodies; _constraints = constraints; _iterations = iterations; _bodyIterations = bodyIterations;
        _retention = multiplierRetention; _initialErrorFraction = initialErrorFraction;

    }
    public InitializeAvbd<TConstraints> Initialize() => new(this);
    public IterateAvbd<TConstraints> Iterate(int iterations) => new(this, iterations);
    public JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency) => Schedule(in context, true, _iterations, dependency);
    internal JobHandle Schedule(in PhysicsStepContext context, bool initialize, int iterations, JobHandle dependency) =>
        PhysicsBodyJobs.Schedule<SolveBodyFields, Solve>(_bodies, _columns, in context, new Solve(this, initialize, iterations), dependency);
    private readonly struct Solve(SolveAvbd<TConstraints> solve, bool initialize, int iterations) : IPhysicsBodyWork
    {
        public void AddInputAccesses(World world, List<JobResourceAccess> accesses) => solve._constraints.AddInputAccesses(world, accesses);
        public void AddAccesses(World world, List<JobResourceAccess> accesses) => solve._constraints.AddAccesses(world, accesses);
        public void Execute(in PhysicsStepContext context, scoped PhysicsBodies bodies)
        {
            solve._constraints.Bind(in context);
            if (initialize)
            {
                for (int index = 0; index < solve._columns.Count; index++)
                {
                    var entity = solve._columns.EntityAt(index);
                    ref readonly BodyMass mass = ref bodies.ReadMass(entity);
                    if (mass.Motion == BodyMotion.Dynamic) bodies.WriteEquation(entity).Initialize(in mass);
                }
                solve._constraints.Initialize(in context, bodies, solve._retention);
            }
            for (int iteration = 0; iteration < iterations; iteration++)
            {
                for (int index = 0; index < solve._columns.Count; index++)
                {
                    var entity = solve._columns.EntityAt(index);
                    if (bodies.IsAwake(entity)) AvbdBodyIteration.Update(entity, bodies, in context, solve._constraints,
                        solve._bodyIterations, solve._initialErrorFraction);
                }
                solve._constraints.UpdateDuals(in context, bodies, solve._initialErrorFraction);
            }
        }
    }
}

public readonly struct InitializeAvbd<TConstraints>(SolveAvbd<TConstraints> solve) : IPhysicsStage where TConstraints : struct, IAvbdConstraints
{
    public JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency) => solve.Schedule(in context, true, 0, dependency);
}
public readonly struct IterateAvbd<TConstraints>(SolveAvbd<TConstraints> solve, int iterations) : IPhysicsStage where TConstraints : struct, IAvbdConstraints
{
    public JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency) => solve.Schedule(in context, false, iterations, dependency);
}
