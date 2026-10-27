using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;

namespace SomeEngine.Physics;

public static class SolveXpbdGraph
{
    public static SolveXpbdGraph<TConstraints> Create<TConstraints>(World world, QueryHandle bodies,
        TConstraints constraints, PhysicsGraphSettings settings, int iterations, QueryReadResources? resources = null)
        where TConstraints : struct, IXpbdGraphConstraints => new(world, bodies, constraints, settings, iterations, resources);
}

/// <summary>Projects original constraint components in independent entity batches.</summary>
public readonly struct SolveXpbdGraph<TConstraints> : IPhysicsStage, IDisposable where TConstraints : struct, IXpbdGraphConstraints
{
    private readonly TConstraints _constraints;
    private readonly PhysicsGraphData _graph;
    private readonly PhysicsGraphQueries _queries;
    private readonly QueryReadResources? _resources;
    private readonly JobResourceAccess[] _accesses;
    private readonly int _iterations;
    private readonly PhysicsGraphSettings _settings;

    public SolveXpbdGraph(World world, QueryHandle bodies, TConstraints constraints, PhysicsGraphSettings settings,
        int iterations, QueryReadResources? resources = null)
    {
        _constraints = constraints;
        _iterations = iterations;
        _settings = settings;
        _resources = resources;
        _graph = new(WorldJobs.GetScheduler(world), constraints.QueryCount, settings);
        var handles = new QueryHandle[constraints.QueryCount];
        for (int index = 0; index < handles.Length; index++) handles[index] = constraints.GetQuery(index);
        _queries = new(world, bodies, handles, resources);
        var accesses = new List<JobResourceAccess> { _graph.WriteAccess };
        accesses.AddRange(_queries.MetadataAccesses);
        constraints.AddResources(world, accesses, PhysicsGraphPhase.Metadata);
        constraints.AddResources(world, accesses, PhysicsGraphPhase.Initialize);
        constraints.AddResources(world, accesses, PhysicsGraphPhase.Bodies);
        _accesses = accesses.ToArray();
    }

    public InitializeXpbdGraph<TConstraints> Initialize() => new(this);
    public IterateXpbdGraph<TConstraints> Iterate(int iterations) => new(this, iterations);
    public PhysicsActivity<TConstraints> Activity(BodyActivitySettings settings, PhysicsEventBuffer? events = null) => new(_graph, _queries, _constraints, settings, _resources, events);
    public JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency) => Schedule(in context, true, _iterations, dependency);
    internal JobHandle Schedule(in PhysicsStepContext context, bool initialize, int iterations, JobHandle dependency) =>
        WorldJobs.GetScheduler(context.World).Schedule(new CaptureMembers(this, context, initialize, iterations), _accesses, dependency);

    private readonly struct CaptureMembers(SolveXpbdGraph<TConstraints> solve, PhysicsStepContext context, bool initialize, int iterations) : IJob
    {
        public void Execute()
        {
            solve._queries.ReadMembers();
            EntityQueryJobs.ScheduleGroup(context.World, new Work(solve), new Solve(solve, context, initialize, iterations),
                solve._accesses, solve._resources);
        }
    }

    private readonly struct Work(SolveXpbdGraph<TConstraints> solve) : IEntityQueryWork
    {
        public int Count => solve._graph.ProjectionWorkCount + 1;
        public int QueryCount => solve._queries.Queries.Length;
        public QueryHandle GetQuery(int workIndex, int queryIndex) => solve._queries.Queries[queryIndex];
        public ReadOnlySpan<Entity> GetEntities(int workIndex, int queryIndex) => workIndex == 0
            ? solve._queries.GlobalEntities(queryIndex, solve._graph)
            : solve._graph.ProjectionWork(workIndex - 1).Entities(queryIndex);
    }

    private readonly struct Solve(SolveXpbdGraph<TConstraints> solve, PhysicsStepContext context, bool initialize, int iterations) : IEntityQueryGroupJob
    {
        public bool Execute(scoped EntityQueryGroup group)
        {
            if (solve._queries.ReadMembers()) return false;
            solve._graph.Begin();
            solve._queries.ReadBodies(solve._graph);
            solve._queries.ReadGraph(solve._graph, solve._constraints, in context, group.Get(0));
            if (solve._graph.Finish()) return false;
            if (initialize) solve._constraints.Initialize(in context, group.Get(0), 3);
            group.ExecuteParallel(new Project(solve, context, iterations, false), 1, solve._graph.ScalarBatches.Count);
            for (int iteration = 0; iteration < iterations; iteration++)
            {
                int start = solve._graph.ScalarBatches.Count + 1;
                foreach (List<PhysicsGraphBatch> color in solve._graph.ProjectionGroups)
                {
                    group.ExecuteParallel(new Project(solve, context, 1, solve._settings.UseSimd), start, color.Count);
                    start += color.Count;
                }
            }
            return true;
        }
    }

    private readonly struct Project(SolveXpbdGraph<TConstraints> solve, PhysicsStepContext context, int iterations, bool useSimd) : IEntityQueryJob
    {
        public void Execute(int index, scoped EntityQueryViews queries)
        {
            var bodies = new PhysicsBodies(queries.Get(0), queries.Get(1), queries.Get(2));
            XpbdIterationLoop.Iterate(in context, bodies, new Iterations(solve._constraints, queries, useSimd), iterations);
        }
    }

    private readonly ref struct Iterations(TConstraints constraints, EntityQueryViews queries, bool useSimd) : IXpbdConstraintIterations
    {
        private readonly EntityQueryViews _queries = queries;
        public void Initialize(in PhysicsStepContext context) => constraints.Initialize(in context, _queries, 3);
        public void Project(in PhysicsStepContext context, scoped PhysicsBodies bodies) => constraints.Project(in context, _queries, 3, bodies, useSimd);
    }

    public void Dispose() { _queries.Dispose(); _graph.Dispose(); }
}

public readonly struct InitializeXpbdGraph<TConstraints>(SolveXpbdGraph<TConstraints> solve) : IPhysicsStage
    where TConstraints : struct, IXpbdGraphConstraints
{
    public JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency) => solve.Schedule(in context, true, 0, dependency);
}

public readonly struct IterateXpbdGraph<TConstraints>(SolveXpbdGraph<TConstraints> solve, int iterations) : IPhysicsStage
    where TConstraints : struct, IXpbdGraphConstraints
{
    public JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency) => solve.Schedule(in context, false, iterations, dependency);
}
