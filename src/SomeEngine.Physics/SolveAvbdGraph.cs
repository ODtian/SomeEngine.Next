using System.Numerics;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;

namespace SomeEngine.Physics;

public static class SolveAvbdGraph
{
    public static SolveAvbdGraph<TConstraints> Create<TConstraints>(World world, QueryHandle bodies,
        TConstraints constraints, PhysicsGraphSettings settings, int iterations, float multiplierRetention,
        float initialErrorFraction, QueryReadResources? resources = null, int bodyIterations = 1)
        where TConstraints : struct, IAvbdGraphConstraints => new(world, bodies, constraints, settings, iterations,
            multiplierRetention, initialErrorFraction, resources, bodyIterations);
}

/// <summary>Global numerical preparation and duals around entity-range body descent and synchronous colors.</summary>
public readonly struct SolveAvbdGraph<TConstraints> : IPhysicsStage, IDisposable where TConstraints : struct, IAvbdGraphConstraints
{
    private readonly TConstraints _constraints;
    private readonly PhysicsGraphData _graph;
    private readonly PhysicsGraphQueries _queries;
    private readonly QueryReadResources? _resources;
    private readonly JobResourceAccess[] _accesses;
    private readonly int _iterations, _bodyIterations;
    private readonly float _retention, _initialErrorFraction;

    public SolveAvbdGraph(World world, QueryHandle bodies, TConstraints constraints, PhysicsGraphSettings settings,
        int iterations, float multiplierRetention, float initialErrorFraction, QueryReadResources? resources = null,
        int bodyIterations = 1)
    {
        _constraints = constraints; _iterations = iterations; _bodyIterations = bodyIterations;
        _retention = multiplierRetention; _initialErrorFraction = initialErrorFraction; _resources = resources;
        _graph = new(WorldJobs.GetScheduler(world), constraints.QueryCount, settings, bodyUpdates: true);
        var handles = new QueryHandle[constraints.QueryCount];
        for (int index = 0; index < handles.Length; index++) handles[index] = constraints.GetQuery(index);
        _queries = new(world, bodies, handles, resources, bodyUpdates: true);
        var accesses = new List<JobResourceAccess> { _graph.WriteAccess };
        accesses.AddRange(_queries.MetadataAccesses);
        constraints.AddResources(world, accesses, PhysicsGraphPhase.Metadata);
        constraints.AddResources(world, accesses, PhysicsGraphPhase.Initialize);
        constraints.AddResources(world, accesses, PhysicsGraphPhase.Bodies);
        constraints.AddResources(world, accesses, PhysicsGraphPhase.Duals);
        _accesses = accesses.ToArray();
    }

    public InitializeAvbdGraph<TConstraints> Initialize() => new(this);
    public IterateAvbdGraph<TConstraints> Iterate(int iterations) => new(this, iterations);
    public JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency) => Schedule(in context, true, _iterations, dependency);
    internal JobHandle Schedule(in PhysicsStepContext context, bool initialize, int iterations, JobHandle dependency) =>
        WorldJobs.GetScheduler(context.World).Schedule(new CaptureMembers(this, context, initialize, iterations), _accesses, dependency);

    private readonly struct CaptureMembers(SolveAvbdGraph<TConstraints> solve, PhysicsStepContext context, bool initialize, int iterations) : IJob
    {
        public void Execute()
        {
            solve._queries.ReadMembers();
            EntityQueryJobs.ScheduleGroup(context.World, new GlobalWork(solve), new Solve(solve, context, initialize, iterations),
                solve._accesses, solve._resources);
        }
    }

    private readonly struct GlobalWork(SolveAvbdGraph<TConstraints> solve) : IEntityQueryWork
    {
        public int Count => 1;
        public int QueryCount => solve._queries.Queries.Length;
        public QueryHandle GetQuery(int workIndex, int queryIndex) => solve._queries.Queries[queryIndex];
        public ReadOnlySpan<Entity> GetEntities(int workIndex, int queryIndex) => solve._queries.GlobalEntities(queryIndex, solve._graph);
    }
    private readonly struct BodyWork(SolveAvbdGraph<TConstraints> solve) : IEntityQueryWork
    {
        public int Count => solve._graph.BodyWorkCount;
        public int QueryCount => solve._queries.Queries.Length;
        public QueryHandle GetQuery(int workIndex, int queryIndex) => solve._queries.Queries[queryIndex];
        public ReadOnlySpan<Entity> GetEntities(int workIndex, int queryIndex) => solve._graph.BodyWork(workIndex).Entities(queryIndex);
    }

    private readonly struct Solve(SolveAvbdGraph<TConstraints> solve, PhysicsStepContext context, bool initialize, int iterations) : IEntityQueryGroupJob
    {
        public bool Execute(scoped EntityQueryGroup group)
        {
            if (solve._queries.ReadMembers()) return false;
            EntityQueryViews global = group.Get(0);
            var bodies = new PhysicsBodies(global.Get(0), global.Get(1), global.Get(2));
            if (initialize)
            {
                foreach (QueryRow row in global.Get(1).Rows)
                    row.ReadWrite<AvbdBodyEquation>().Initialize(in row.ReadRef<BodyMass>());
                solve._constraints.Initialize(in context, global, 3, bodies, solve._retention);
            }
            for (int iteration = 0; iteration < iterations; iteration++)
            {
                solve._graph.Begin(); solve._queries.ReadBodies(solve._graph);
                var builder = new PhysicsConstraintGraphBuilder(solve._graph);
                solve._constraints.ReadGraph(in context, solve._queries.Reads, 0, ref builder);
                solve._graph.Finish();
                var work = new BodyWork(solve);
                var update = new UpdateBodies(solve, context);
                group.ExecuteParallel(in work, in update, 0, solve._graph.ScalarBatches.Count);
                int start = solve._graph.ScalarBatches.Count;
                foreach (List<PhysicsGraphBatch> color in solve._graph.BodyGroups)
                {
                    group.ExecuteParallel(in work, in update, start, color.Count);
                    start += color.Count;
                }
                solve._constraints.UpdateDuals(in context, global, 3, bodies, solve._initialErrorFraction);
            }
            return true;
        }
    }

    private readonly struct UpdateBodies(SolveAvbdGraph<TConstraints> solve, PhysicsStepContext context) : IEntityQueryJob
    {
        public void Execute(int index, scoped EntityQueryViews queries)
        {
            var bodies = new PhysicsBodies(queries.Get(0), queries.Get(1), queries.Get(2));
            var constraints = new BodyConstraints(solve._constraints, queries);
            foreach (QueryRow row in queries.Get(1).Rows)
                AvbdBodyIteration.Update(row, bodies, in context, constraints, solve._bodyIterations, solve._initialErrorFraction);
        }
    }
    private readonly ref struct BodyConstraints(TConstraints constraints, EntityQueryViews queries) : IAvbdBodyConstraints
    {
        private readonly EntityQueryViews _queries = queries;
        public void AccumulateBodyEquation(in PhysicsStepContext context, Entity body, scoped PhysicsBodies bodies,
            float fraction, ref AvbdBodyEquation equation) => constraints.AccumulateBodyEquation(in context, _queries, 3, body, bodies, fraction, ref equation);
        public void AdjustBodyChange(in PhysicsStepContext context, Entity body, scoped PhysicsBodies bodies,
            in AvbdBodyEquation equation, ref Vector3 translation, ref Vector3 rotation) =>
            constraints.AdjustBodyChange(in context, _queries, 3, body, bodies, in equation, ref translation, ref rotation);
    }
    public void Dispose() { _queries.Dispose(); _graph.Dispose(); }
}

public readonly struct InitializeAvbdGraph<TConstraints>(SolveAvbdGraph<TConstraints> solve) : IPhysicsStage
    where TConstraints : struct, IAvbdGraphConstraints
{
    public JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency) => solve.Schedule(in context, true, 0, dependency);
}
public readonly struct IterateAvbdGraph<TConstraints>(SolveAvbdGraph<TConstraints> solve, int iterations) : IPhysicsStage
    where TConstraints : struct, IAvbdGraphConstraints
{
    public JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency) => solve.Schedule(in context, false, iterations, dependency);
}
