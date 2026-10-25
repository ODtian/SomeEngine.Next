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
    private readonly World _world;
    private readonly TConstraints _constraints;
    private readonly PhysicsGraphData _graph;
    private readonly QueryHandle[] _queries;
    private readonly QueryHandle[] _reads;
    private readonly QueryReadResources? _resources;
    private readonly JobResourceAccess[] _metadataAccesses;
    private readonly int _iterations;

    public SolveXpbdGraph(World world, QueryHandle bodies, TConstraints constraints, PhysicsGraphSettings settings,
        int iterations, QueryReadResources? resources = null)
    {
        _world = world;
        _constraints = constraints;
        _iterations = iterations;
        _resources = resources;
        _graph = new(WorldJobs.GetScheduler(world), constraints.QueryCount, settings);
        _queries = new QueryHandle[constraints.QueryCount + 3];
        _reads = new QueryHandle[constraints.QueryCount];
        QueryDefinition bodyDefinition = world.GetQueryDefinition(bodies);
        _queries[0] = world.Query(bodyDefinition.AsReadOnly());
        _queries[1] = world.Query(bodyDefinition);
        QueryDefinition references = QueryDefinition.Empty.WithAccessFrom(bodyDefinition.AsReadOnly());
        var accesses = new List<JobResourceAccess> { _graph.WriteAccess };
        PhysicsQueryAccess.AddAccesses(world, _queries[0], accesses, resources);
        for (int index = 0; index < constraints.QueryCount; index++)
        {
            QueryDefinition definition = world.GetQueryDefinition(constraints.GetQuery(index));
            _queries[index + 3] = world.Query(definition);
            _reads[index] = world.Query(definition.AsReadOnly());
            references = references.WithAccessFrom(definition.AsReadOnly());
            PhysicsQueryAccess.AddAccesses(world, _reads[index], accesses, resources);
        }
        _queries[2] = world.Query(references);
        _metadataAccesses = accesses.ToArray();
    }

    public JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency)
    {
        Scheduler scheduler = WorldJobs.GetScheduler(context.World);
        JobHandle graph = scheduler.Schedule(new ReadGraph(this, context), _metadataAccesses, dependency);
        return EntityQueryJobs.Schedule(context.World, new Work(this), new Project(this, context),
            [_graph.ReadAccess], _resources, graph);
    }

    private readonly struct ReadGraph(SolveXpbdGraph<TConstraints> solve, PhysicsStepContext context) : IJob
    {
        public void Execute()
        {
            solve._graph.Begin();
            PhysicsGraphData bodies = solve._graph;
            context.World.ExecuteReadQuery(solve._queries[0], ref bodies,
                static (scoped QueryCursor rows, ref PhysicsGraphData data) => data.ReadBodies(rows));
            var builder = new PhysicsConstraintGraphBuilder(solve._graph);
            solve._constraints.ReadGraph(in context, solve._reads, 0, ref builder);
            solve._graph.Finish();
        }
    }

    private readonly struct Work(SolveXpbdGraph<TConstraints> solve) : IEntityQueryWork
    {
        public int Count => solve._graph.Batches.Count;
        public int QueryCount => solve._queries.Length;
        public QueryHandle GetQuery(int workIndex, int queryIndex) => solve._queries[queryIndex];
        public ReadOnlySpan<Entity> GetEntities(int workIndex, int queryIndex) => solve._graph.Batches[workIndex].Entities(queryIndex);
    }

    private readonly struct Project(SolveXpbdGraph<TConstraints> solve, PhysicsStepContext context) : IEntityQueryJob
    {
        public void Execute(int index, scoped EntityQueryViews queries)
        {
            var bodies = new PhysicsBodies(queries.Get(0), queries.Get(1), queries.Get(2));
            var constraints = new Iterations(solve._constraints, queries);
            XpbdIterationLoop.Initialize(in context, constraints);
            XpbdIterationLoop.Iterate(in context, bodies, constraints, solve._iterations);
        }
    }

    private readonly ref struct Iterations(TConstraints constraints, EntityQueryViews queries) : IXpbdConstraintIterations
    {
        private readonly EntityQueryViews _queries = queries;
        public void Initialize(in PhysicsStepContext context) => constraints.Initialize(in context, _queries, 3);
        public void Project(in PhysicsStepContext context, scoped PhysicsBodies bodies) => constraints.Project(in context, _queries, 3, bodies, false);
    }

    public void Dispose()
    {
        foreach (QueryHandle query in _reads) _world.ReleaseQuery(query);
        foreach (QueryHandle query in _queries) _world.ReleaseQuery(query);
        _graph.Dispose();
    }
}
