using System.Numerics;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;

namespace SomeEngine.Physics;

public readonly partial struct SolveAvbdGraph
{
    public static SolveAvbdGraph Create(AvbdGraphCommands commands, bool buildGraph = true) => new(commands, buildGraph);
    public static SolveAvbdGraph<TConstraints> Create<TConstraints>(World world, QueryHandle bodies,
        TConstraints constraints, PhysicsGraphSettings settings, int iterations, float multiplierRetention,
        float initialErrorFraction, QueryReadResources? resources = null, int bodyIterations = 1)
        where TConstraints : struct, IAvbdGraphConstraints => new(world, bodies, constraints, settings, iterations,
            multiplierRetention, initialErrorFraction, resources, bodyIterations);
}

/// <summary>Global numerical preparation and duals around entity-range body descent and synchronous colors.</summary>
public readonly struct SolveAvbdGraph<TConstraints> : IPhysicsStage, IPhysicsIterations, IDisposable where TConstraints : struct, IAvbdGraphConstraints
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
    public PhysicsActivity<TConstraints> Activity(BodyActivitySettings settings, PhysicsEventBuffer? events = null) => new(_graph, _queries, _constraints, settings, _resources, events);
    public JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency) => Schedule(in context, true, _iterations, dependency);
    public JobHandle ScheduleIterations(in PhysicsStepContext context, int iterations, JobHandle dependency) =>
        Schedule(in context, true, iterations, dependency, context.FirstIteration == 0 ? null : 1);
    internal JobHandle Schedule(in PhysicsStepContext context, bool initialize, int iterations, JobHandle dependency, float? retention = null) =>
        WorldJobs.GetScheduler(context.World).Schedule(new CaptureMembers(this, context, initialize, iterations, retention ?? _retention), _accesses, dependency);

    private readonly struct CaptureMembers(SolveAvbdGraph<TConstraints> solve, PhysicsStepContext context, bool initialize, int iterations, float retention) : IJob
    {
        public void Execute()
        {
            solve._queries.ReadMembers(in context);
            EntityQueryJobs.ScheduleGroup(context.World, new GlobalWork(solve), new Solve(solve, context, initialize, iterations, retention),
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

    private readonly struct Solve(SolveAvbdGraph<TConstraints> solve, PhysicsStepContext context, bool initialize, int iterations, float retention) : IEntityQueryGroupJob
    {
        public bool Execute(scoped EntityQueryGroup group)
        {
            if (solve._queries.ReadMembers(in context)) return false;
            solve._graph.Begin();
            solve._queries.ReadBodies(solve._graph, in context);
            solve._queries.ReadGraph(solve._graph, solve._constraints, in context, group.Get(0));
            if (solve._graph.Finish()) return false;
            if (solve._graph.BodyWorkCount == 0) return true;
            EntityQueryViews global = group.Get(0);
            var selectedWork = new PhysicsSolverRows(solve._queries, solve._graph);
            using EntityQueryGroup selectedRows = group.Borrow(in selectedWork);
            EntityQueryViews selected = selectedRows.Get(0);
            var bodies = new PhysicsBodies(selected.Get(0), selected.Get(1), selected.Get(2));
            if (initialize)
            {
                foreach (QueryRow row in selected.Get(1).Rows)
                    row.ReadWrite<AvbdBodyEquation>().Initialize(in row.ReadRef<BodyMass>());
                solve._constraints.Initialize(in context, selected, 3, bodies, retention);
            }
            solve._graph.RecordNumericalWrites(global.Get(1).CurrentSystemVersion, selected);
            if (iterations == 0) return true;
            var work = new BodyWork(solve);
            using EntityQueryGroup bodyRows = group.Borrow(in work);
            for (int iteration = 0; iteration < iterations; iteration++)
            {
                bool changed = false;
                if (solve._constraints.HasGraphChanges)
                {
                    solve._graph.Begin(); solve._queries.ReadBodies(solve._graph, in context);
                    solve._queries.ReadGraph(solve._graph, solve._constraints, in context, global);
                    changed = solve._graph.Finish();
                }
                if (changed || solve._resources is not null) bodyRows.Refresh(in work);
                if (changed)
                {
                    selectedRows.Refresh(in selectedWork);
                    selected = selectedRows.Get(0);
                    bodies = new PhysicsBodies(selected.Get(0), selected.Get(1), selected.Get(2));
                }
                var update = new UpdateBodies(solve, context);
                UpdateBodyRange(bodyRows, in update, 0, solve._graph.ScalarBatches.Count);
                int start = solve._graph.ScalarBatches.Count;
                foreach (List<PhysicsGraphBatch> color in solve._graph.BodyGroups)
                {
                    UpdateBodyRange(bodyRows, in update, start, color.Count);
                    start += color.Count;
                }
                solve._constraints.UpdateDuals(in context, selected, 3, bodies, solve._initialErrorFraction);
            }
            return true;
        }
    }

    private static void UpdateBodyRange(scoped EntityQueryGroup rows, in UpdateBodies update, int start, int count)
    {
        if (count == 1) update.Execute(start, rows.Get(start));
        else if (count > 1) rows.ExecuteParallel(in update, start, count);
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
