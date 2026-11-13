using SomeEngine.ECS;
using SomeEngine.Job;
using SomeEngine.ECS.Systems;

namespace SomeEngine.Physics;

public readonly partial struct SolveAvbdGraph
{
    public static SolveAvbdGraph Create(AvbdGraphCommands commands, bool buildGraph = true) => new(commands, buildGraph);
    public static SolveAvbdGraph<TConstraints> Create<TConstraints>(World world, Query bodies, TConstraints constraints,
        PhysicsGraphSettings settings, int iterations, float multiplierRetention, float initialErrorFraction, int bodyIterations = 1)
        where TConstraints : struct, IAvbdGraphConstraints =>
        new(world, bodies, constraints, settings, iterations, multiplierRetention, initialErrorFraction, bodyIterations);
}

/// <summary>One parent borrow supplies original body and constraint columns to each joined graph phase.</summary>
public readonly struct SolveAvbdGraph<TConstraints> : IPhysicsStage, IPhysicsIterations, IDisposable where TConstraints : struct, IAvbdGraphConstraints
{
    private readonly Query _bodies;
    private readonly BodyColumns _columns = new();
    private readonly TConstraints _constraints;
    private readonly PhysicsGraphData _graph;
    private readonly PhysicsGraphQueries _queries;
    private readonly int _iterations, _bodyIterations;
    private readonly float _retention, _initialErrorFraction;
    public SolveAvbdGraph(World world, Query bodies, TConstraints constraints, PhysicsGraphSettings settings,
        int iterations, float multiplierRetention, float initialErrorFraction, int bodyIterations = 1)
    {
        _bodies = bodies; _constraints = constraints; _iterations = iterations; _bodyIterations = bodyIterations;
        _retention = multiplierRetention; _initialErrorFraction = initialErrorFraction;
        _graph = new((world.Execution!.Scheduler!), constraints.QueryCount, settings, bodyUpdates: true);
        var inputs = new Query?[constraints.QueryCount];
        for (int index = 0; index < inputs.Length; index++) inputs[index] = constraints.GetQuery(index);
        _queries = new(world, bodies, inputs);

    }
    public InitializeAvbdGraph<TConstraints> Initialize() => new(this);
    public IterateAvbdGraph<TConstraints> Iterate(int iterations) => new(this, iterations);
    public PhysicsActivity<TConstraints> Activity(BodyActivitySettings settings, PhysicsEventBuffer? events = null) =>
        new(_graph, _queries, _bodies, _constraints, settings, events);
    public JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency) => Schedule(in context, true, _iterations, dependency);
    public JobHandle ScheduleIterations(in PhysicsStepContext context, int iterations, JobHandle dependency) =>
        Schedule(in context, true, iterations, dependency, context.FirstIteration == 0 ? _retention : 1);
    internal JobHandle Schedule(in PhysicsStepContext context, bool initialize, int iterations, JobHandle dependency, float? retention = null) =>
        PhysicsBodyJobs.Schedule<SolveBodyFields, Solve>(_bodies, _columns, in context, new Solve(this, initialize, iterations, retention ?? _retention), dependency);
    private readonly struct Solve(SolveAvbdGraph<TConstraints> solve, bool initialize, int iterations, float retention) : IPhysicsBodyWork
    {
        public void AddInputAccesses(World world, List<JobResourceAccess> accesses) => solve._constraints.AddInputAccesses(world, accesses);
        public void AddAccesses(World world, List<JobResourceAccess> accesses)
        {
            accesses.Add(solve._graph.WriteAccess); accesses.AddRange(solve._queries.MetadataAccesses);
            solve._constraints.AddAccesses(world, accesses);
            foreach (PhysicsGraphPhase phase in Enum.GetValues<PhysicsGraphPhase>()) solve._constraints.AddResources(world, accesses, phase);
        }
        public void Execute(in PhysicsStepContext context, scoped PhysicsBodies bodies)
        {
            solve._constraints.Bind(in context);
            ReadGraph(solve, bodies, in context);
            if (initialize)
            {
                foreach (var entity in solve._columns.Entities)
                {
                    ref readonly BodyMass mass = ref bodies.ReadMass(entity);
                    if (mass.Motion == BodyMotion.Dynamic) bodies.WriteEquation(entity).Initialize(in mass);
                }
                solve._constraints.Initialize(in context, bodies, retention);
            }
            var work = new BodyWork(solve, context);
            for (int iteration = 0; iteration < iterations; iteration++)
            {
                if (solve._constraints.HasGraphChanges) ReadGraph(solve, bodies, in context);
                foreach (PhysicsBodyGroup group in solve._graph.BodyGroups)
                {
                    if (group.Count == 1) work.Execute(in group.Ranges[0]);
                    else context.World.Execution!.ExecuteRanges(group.Ranges, group.Count, in work);
                }
                solve._constraints.UpdateDuals(in context, bodies, solve._initialErrorFraction);
            }
        }
    }
    private static void ReadGraph(in SolveAvbdGraph<TConstraints> solve, scoped PhysicsBodies bodies, in PhysicsStepContext context)
    {
        solve._graph.Begin(); solve._graph.ReadBodies(bodies); solve._queries.ReadMembers(in context, solve._graph);
        solve._constraints.RefreshGraph(in context, bodies);
        var builder = new PhysicsConstraintGraphBuilder(solve._graph, bodies);
        solve._constraints.ReadGraph(in context, 0, ref builder); solve._graph.Finish();
    }
    private readonly struct BodyWork(SolveAvbdGraph<TConstraints> solve, PhysicsStepContext context) : IRangeWork<PhysicsBodyRange>
    {
        public void Execute(in PhysicsBodyRange range)
        {
            var bodies = new PhysicsBodies(solve._columns);
            for (int index = range.Start; index < range.Start + range.Count; index++)
            {
                var entity = solve._graph.BodyAt(index);
                if (bodies.IsAwake(entity)) AvbdBodyIteration.Update(entity, bodies, in context,
                    solve._constraints, solve._bodyIterations, solve._initialErrorFraction);
            }
        }
    }
    public void Dispose() { _queries.Dispose(); _graph.Dispose(); }
}
public readonly struct InitializeAvbdGraph<TConstraints>(SolveAvbdGraph<TConstraints> solve) : IPhysicsStage where TConstraints : struct, IAvbdGraphConstraints
{
    public JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency) => solve.Schedule(in context, true, 0, dependency);
}
public readonly struct IterateAvbdGraph<TConstraints>(SolveAvbdGraph<TConstraints> solve, int iterations) : IPhysicsStage where TConstraints : struct, IAvbdGraphConstraints
{
    public JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency) => solve.Schedule(in context, false, iterations, dependency);
}
