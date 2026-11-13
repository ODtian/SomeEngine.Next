using SomeEngine.ECS;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;

namespace SomeEngine.Physics;

public readonly partial struct SolveXpbdGraph
{
    public static SolveXpbdGraph Create(XpbdGraphCommands commands, bool buildGraph = true) => new(commands, buildGraph);
    public static SolveXpbdGraph<TConstraints> Create<TConstraints>(World world, Query bodies, TConstraints constraints,
        PhysicsGraphSettings settings, int iterations) where TConstraints : struct, IXpbdGraphConstraints => new(world, bodies, constraints, settings, iterations);
}
/// <summary>Projects independent graph ranges through fields borrowed once by the parent Job.</summary>
public readonly struct SolveXpbdGraph<TConstraints> : IPhysicsStage, IPhysicsIterations, IDisposable where TConstraints : struct, IXpbdGraphConstraints
{
    private readonly Query _bodies;
    private readonly BodyColumns _columns = new();
    private readonly TConstraints _constraints;
    private readonly PhysicsGraphData _graph;
    private readonly PhysicsGraphQueries _queries;
    private readonly PhysicsGraphSettings _settings;
    private readonly int _iterations;
    public SolveXpbdGraph(World world, Query bodies, TConstraints constraints, PhysicsGraphSettings settings, int iterations)
    {
        _bodies = bodies; _constraints = constraints; _settings = settings; _iterations = iterations;
        _graph = new((world.Execution!.Scheduler!), constraints.QueryCount, settings);
        var inputs = new Query?[constraints.QueryCount];
        for (int index = 0; index < inputs.Length; index++) inputs[index] = constraints.GetQuery(index);
        _queries = new(world, bodies, inputs);

    }
    public InitializeXpbdGraph<TConstraints> Initialize() => new(this);
    public IterateXpbdGraph<TConstraints> Iterate(int iterations) => new(this, iterations);
    public PhysicsActivity<TConstraints> Activity(BodyActivitySettings settings, PhysicsEventBuffer? events = null) =>
        new(_graph, _queries, _bodies, _constraints, settings, events);
    public JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency) => Schedule(in context, true, _iterations, dependency);
    public JobHandle ScheduleIterations(in PhysicsStepContext context, int iterations, JobHandle dependency) =>
        Schedule(in context, context.FirstIteration == 0, iterations, dependency);
    internal JobHandle Schedule(in PhysicsStepContext context, bool initialize, int iterations, JobHandle dependency) =>
        PhysicsBodyJobs.Schedule<ProjectBodyFields, Solve>(_bodies, _columns, in context, new Solve(this, initialize, iterations), dependency);
    private readonly struct Solve(SolveXpbdGraph<TConstraints> solve, bool initialize, int iterations) : IPhysicsBodyWork
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
            if (initialize) solve._constraints.Initialize(in context);
            var work = new ProjectWork(solve, context);
            for (int iteration = 0; iteration < iterations; iteration++)
            {
                if (solve._constraints.HasGraphChanges) ReadGraph(solve, bodies, in context);
                foreach (PhysicsConstraintGroup group in solve._graph.ProjectionGroups)
                {
                    if (group.Count == 1) work.Execute(in group.Ranges[0]);
                    else context.World.Execution!.ExecuteRanges(group.Ranges, group.Count, in work);
                }
            }
        }
    }
    private static void ReadGraph(in SolveXpbdGraph<TConstraints> solve, scoped PhysicsBodies bodies, in PhysicsStepContext context)
    {
        solve._graph.Begin(); solve._graph.ReadBodies(bodies); solve._queries.ReadMembers(in context, solve._graph);
        solve._constraints.RefreshGraph(in context, bodies);
        var builder = new PhysicsConstraintGraphBuilder(solve._graph, bodies);
        solve._constraints.ReadGraph(in context, 0, ref builder); solve._graph.Finish();
    }
    private readonly struct ProjectWork(SolveXpbdGraph<TConstraints> solve, PhysicsStepContext context) : IRangeWork<PhysicsConstraintRange>
    {
        public void Execute(in PhysicsConstraintRange range)
        {
            var bodies = new PhysicsBodies(solve._columns);
            ReadOnlySpan<PhysicsConstraintIdentity> rows = solve._graph.ConstraintSpan(range.Start, range.Count);
            for (int start = 0; start < rows.Length;)
            {
                int set = rows[start].Query, end = start + 1;
                while (end < rows.Length && rows[end].Query == set) end++;
                solve._constraints.ProjectRange(in context, set, rows[start..end], bodies, solve._settings.UseSimd && range.Independent);
                start = end;
            }
        }
    }
    public void Dispose() { _queries.Dispose(); _graph.Dispose(); }
}
public readonly struct InitializeXpbdGraph<TConstraints>(SolveXpbdGraph<TConstraints> solve) : IPhysicsStage where TConstraints : struct, IXpbdGraphConstraints
{
    public JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency) => solve.Schedule(in context, true, 0, dependency);
}
public readonly struct IterateXpbdGraph<TConstraints>(SolveXpbdGraph<TConstraints> solve, int iterations) : IPhysicsStage where TConstraints : struct, IXpbdGraphConstraints
{
    public JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency) => solve.Schedule(in context, false, iterations, dependency);
}
