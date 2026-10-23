using System.Numerics;
using SomeEngine.Job;

namespace SomeEngine.Navigation;

/// <summary>One fixed boundary round and the mutable arrays owned by its concrete solver.</summary>
internal class NavigationTileFlowData(NavigationFlowBuildState result, float[] values, int[] successors,
    int[] goals, Vector3[] directions)
{
    internal NavigationFlowBuildState Result { get; } = result;
    internal NavigationFlowInput Input => Result.Input;
    internal float[] Values { get; } = values;
    internal int[] Successors { get; } = successors;
    internal int[] Goals { get; } = goals;
    internal Vector3[] Directions { get; } = directions;
    internal float[] BoundaryValues { get; } = new float[values.Length];
    internal int[] BoundaryGoals { get; } = new int[values.Length];
    internal bool[] Active { get; } = new bool[result.Input.Costs.Grid.TileRanges.Length];
    internal int[] RoundTiles = [];
    internal int Processed;
    internal ulong Round;
    internal bool Started;

    internal float Value(int cell, int tile) => Input.Costs.Grid.CellTiles.Span[cell] == tile ? Values[cell] : BoundaryValues[cell];
    internal int Goal(int cell, int tile) => Input.Costs.Grid.CellTiles.Span[cell] == tile ? Goals[cell] : BoundaryGoals[cell];
}

internal sealed class NavigationTileFlowState<TWork>(NavigationFlowBuildState result, float[] values, int[] successors,
    int[] goals, Vector3[] directions) : NavigationTileFlowData(result, values, successors, goals, directions)
    where TWork : class
{
    internal TWork[] Work = [];
}

internal interface INavigationTileFlowSolver<TWork> where TWork : class
{
    static abstract string Id { get; }
    static abstract NavigationFlowSampling Sampling { get; }
    static abstract TWork Create(NavigationGridTileRange range);
    static abstract int Solve(TWork work, NavigationTileFlowData state, int tile, int budget);
    static abstract bool HasWork(TWork work);
    static abstract void BuildDirection(NavigationTileFlowData state, int cell);
}

/// <summary>Runs each active tile against a completed boundary round, then exchanges its changed entrances.</summary>
internal static class NavigationTileFlow<TSolver, TWork>
    where TSolver : struct, INavigationTileFlowSolver<TWork>
    where TWork : class
{
    internal static JobHandle Schedule(Scheduler scheduler, NavigationTileFlowState<TWork> state, int budget, JobHandle dependency)
    {
        var accesses = InputAccesses(scheduler, state);
        accesses.Add(JobResourceAccess.Exclusive(scheduler.GetResourceToken(state)));
        accesses.Add(NavigationJobAccess.Write(scheduler, state.Values));
        accesses.Add(NavigationJobAccess.Write(scheduler, state.Goals));
        accesses.Add(NavigationJobAccess.Write(scheduler, state.Successors));
        accesses.Add(NavigationJobAccess.Write(scheduler, state.BoundaryValues));
        accesses.Add(NavigationJobAccess.Write(scheduler, state.BoundaryGoals));
        return scheduler.Schedule(new Begin(scheduler, state, budget), accesses.ToArray(), dependency);
    }

    private static List<JobResourceAccess> InputAccesses(Scheduler scheduler, NavigationTileFlowData state)
    {
        var accesses = new List<JobResourceAccess>
        {
            NavigationJobAccess.Read(scheduler, state.Input.Costs.CellCosts), NavigationJobAccess.Read(scheduler, state.Input.Costs.EdgeCosts),
            NavigationJobAccess.Read(scheduler, state.Input.Goals),
        };
        state.Input.Costs.Grid.AddReadAccesses(scheduler, accesses);
        return accesses;
    }

    private readonly struct Begin(Scheduler scheduler, NavigationTileFlowState<TWork> state, int budget) : IJob
    {
        public void Execute()
        {
            if (state.Result.Status == NavigationFlowStatus.Ready) return;
            if (!state.Started) Initialize(state);
            ScheduleBatch(scheduler, state, budget);
        }
    }

    private static void Initialize(NavigationTileFlowState<TWork> state)
    {
        state.Started = true;
        NavigationGrid grid = state.Input.Costs.Grid;
        Array.Fill(state.Values, float.PositiveInfinity);
        Array.Fill(state.Goals, -1);
        Array.Fill(state.Successors, -1);
        state.Work = new TWork[grid.TileRanges.Length];
        for (int tile = 0; tile < state.Work.Length; tile++) state.Work[tile] = TSolver.Create(grid.TileRanges.Span[tile]);
        for (int goal = 0; goal < state.Input.Goals.Length; goal++)
        {
            NavigationFlowGoal target = state.Input.Goals.Span[goal];
            bool seeded = false;
            for (int cell = 0; cell < grid.Cells.Length; cell++)
                if (Vector3.Distance(grid.Cells.Span[cell].Position, target.Position) <= target.ArrivalRange)
                {
                    Seed(cell, goal);
                    seeded = true;
                }
            if (!seeded)
            {
                int cell = grid.FindCell(target.Position);
                if (cell >= 0) Seed(cell, goal);
            }
        }
        StartRound(state);
        void Seed(int cell, int goal)
        {
            if (!float.IsFinite(state.Input.Costs.CellCosts.Span[cell])) return;
            state.Values[cell] = 0;
            state.Goals[cell] = goal;
            state.Active[grid.CellTiles.Span[cell]] = true;
        }
    }

    private static void StartRound(NavigationTileFlowState<TWork> state)
    {
        Array.Copy(state.Values, state.BoundaryValues, state.Values.Length);
        Array.Copy(state.Goals, state.BoundaryGoals, state.Goals.Length);
        state.RoundTiles = Enumerable.Range(0, state.Active.Length).Where(tile => state.Active[tile]).ToArray();
        Array.Clear(state.Active);
        state.Processed = 0;
        state.Round++;
    }

    private static void ScheduleBatch(Scheduler scheduler, NavigationTileFlowState<TWork> state, int budget)
    {
        if (state.RoundTiles.Length == 0) { ScheduleDirections(scheduler, state); return; }
        if (budget <= 0) return;
        int count = Math.Min(state.RoundTiles.Length - state.Processed, budget);
        var jobs = new JobHandle[count];
        var used = new int[count];
        for (int index = 0; index < count; index++)
        {
            int tile = state.RoundTiles[state.Processed + index];
            NavigationGridTileRange range = state.Input.Costs.Grid.TileRanges.Span[tile];
            var accesses = InputAccesses(scheduler, state);
            accesses.Add(NavigationJobAccess.Read<float>(scheduler, state.BoundaryValues));
            accesses.Add(NavigationJobAccess.Read<int>(scheduler, state.BoundaryGoals));
            accesses.Add(JobResourceAccess.Exclusive(scheduler.GetResourceToken(state.Work[tile])));
            accesses.Add(JobResourceAccess.Write(scheduler.GetResourceToken(state.Values), range.CellStart, range.CellCount));
            accesses.Add(JobResourceAccess.Write(scheduler.GetResourceToken(state.Goals), range.CellStart, range.CellCount));
            accesses.Add(JobResourceAccess.Write(scheduler.GetResourceToken(state.Successors), range.CellStart, range.CellCount));
            accesses.Add(JobResourceAccess.Write(scheduler.GetResourceToken(used), index, 1));
            jobs[index] = scheduler.Schedule(new SolveTile(state, tile, budget / count + (index < budget % count ? 1 : 0), used, index), accesses.ToArray());
        }
        var mergeAccesses = InputAccesses(scheduler, state);
        mergeAccesses.Add(JobResourceAccess.Exclusive(scheduler.GetResourceToken(state)));
        mergeAccesses.Add(NavigationJobAccess.Read<int>(scheduler, used));
        mergeAccesses.Add(NavigationJobAccess.Read<float>(scheduler, state.Values));
        mergeAccesses.Add(NavigationJobAccess.Read<int>(scheduler, state.Goals));
        mergeAccesses.Add(NavigationJobAccess.Write(scheduler, state.BoundaryValues));
        mergeAccesses.Add(NavigationJobAccess.Write(scheduler, state.BoundaryGoals));
        scheduler.Schedule(new Exchange(scheduler, state, budget, used), mergeAccesses.ToArray(), scheduler.CombineDependencies(jobs));
    }

    private readonly struct SolveTile(NavigationTileFlowState<TWork> state, int tile, int budget, int[] used, int index) : IJob
    {
        public void Execute() => used[index] = TSolver.Solve(state.Work[tile], state, tile, budget);
    }

    private readonly struct Exchange(Scheduler scheduler, NavigationTileFlowState<TWork> state, int budget, int[] used) : IJob
    {
        public void Execute()
        {
            for (int index = 0; index < used.Length; index++)
            {
                int tile = state.RoundTiles[state.Processed + index];
                state.Active[tile] |= TSolver.HasWork(state.Work[tile]);
            }
            state.Processed += used.Length;
            int remaining = budget - used.Sum();
            if (state.Processed == state.RoundTiles.Length)
            {
                NavigationGrid grid = state.Input.Costs.Grid;
                for (int cell = 0; cell < grid.Cells.Length; cell++)
                {
                    if (state.Values[cell] == state.BoundaryValues[cell]) continue;
                    NavigationGridRange incoming = grid.IncomingRanges.Span[cell];
                    foreach (NavigationGridIncoming edge in grid.IncomingEdges.Span.Slice(incoming.Start, incoming.Count))
                    {
                        int sourceTile = grid.CellTiles.Span[edge.FromCell];
                        if (sourceTile != grid.CellTiles.Span[cell]) state.Active[sourceTile] = true;
                    }
                }
                StartRound(state);
            }
            ScheduleBatch(scheduler, state, remaining);
        }
    }

    private static void ScheduleDirections(Scheduler scheduler, NavigationTileFlowState<TWork> state)
    {
        var jobs = new JobHandle[state.Work.Length];
        for (int tile = 0; tile < jobs.Length; tile++)
        {
            NavigationGridTileRange range = state.Input.Costs.Grid.TileRanges.Span[tile];
            var accesses = InputAccesses(scheduler, state);
            accesses.Add(NavigationJobAccess.Read<float>(scheduler, state.Values));
            accesses.Add(JobResourceAccess.Write(scheduler.GetResourceToken(state.Directions), range.CellStart, range.CellCount));
            accesses.Add(JobResourceAccess.Write(scheduler.GetResourceToken(state.Successors), range.CellStart, range.CellCount));
            jobs[tile] = scheduler.Schedule(new BuildDirections(state, range), accesses.ToArray());
        }
        var completedAccesses = InputAccesses(scheduler, state);
        completedAccesses.Add(JobResourceAccess.Exclusive(scheduler.GetResourceToken(state.Result)));
        completedAccesses.Add(NavigationJobAccess.Read<float>(scheduler, state.Values));
        completedAccesses.Add(NavigationJobAccess.Read<int>(scheduler, state.Successors));
        completedAccesses.Add(NavigationJobAccess.Read<int>(scheduler, state.Goals));
        completedAccesses.Add(NavigationJobAccess.Read<Vector3>(scheduler, state.Directions));
        scheduler.Schedule(new CompleteField(state), completedAccesses.ToArray(), scheduler.CombineDependencies(jobs));
    }

    private readonly struct BuildDirections(NavigationTileFlowData state, NavigationGridTileRange range) : IJob
    {
        public void Execute()
        {
            for (int cell = range.CellStart; cell < range.CellStart + range.CellCount; cell++) TSolver.BuildDirection(state, cell);
        }
    }

    private readonly struct CompleteField(NavigationTileFlowData state) : IJob
    {
        public void Execute()
        {
            NavigationFlowInput input = state.Input;
            NavigationGrid grid = input.Costs.Grid;
            NavigationFlowTile[] tiles = grid.TileRanges.ToArray().Select(tile => new NavigationFlowTile(tile.Tile,
                tile.CellStart, tile.CellCount, tile.ContentVersion, input.Costs.Version, input.GoalVersion, tile.Detailed)).ToArray();
            NavigationFlowDependency[] dependencies = tiles.Select(tile => new NavigationFlowDependency(tile.Tile,
                tile.GridVersion, tile.CostVersion, tile.GoalVersion)).ToArray();
            var boundaries = new List<NavigationFlowBoundaryDependency>();
            for (int cell = 0; cell < grid.Cells.Length; cell++)
            {
                NavigationGridCell from = grid.Cells.Span[cell];
                foreach (NavigationGridEdge edge in grid.Edges.Span.Slice(from.EdgeStart, from.EdgeCount))
                    if (from.Tile != grid.Cells.Span[edge.ToCell].Tile)
                        boundaries.Add(new(from.Tile, grid.Cells.Span[edge.ToCell].Tile, cell, edge.ToCell,
                            state.Values[edge.ToCell], input.Version, state.Round));
            }
            NavigationTileCoordinate[] required = tiles.Where(tile => !tile.Detailed).Select(tile => tile.Tile).ToArray();
            state.Result.Field = new(input, state.Values, state.Successors, state.Directions, state.Goals,
                dependencies, TSolver.Id, TSolver.Sampling, tiles, boundaries.ToArray(), required, state.Round);
            state.Result.Status = NavigationFlowStatus.Ready;
        }
    }
}

internal sealed class DijkstraTileWork(NavigationGridTileRange range)
{
    internal NavigationGridTileRange Range { get; } = range;
    internal PriorityQueue<int, float> Open { get; } = new();
    internal bool Started;
}

internal readonly struct DijkstraTileSolver : INavigationTileFlowSolver<DijkstraTileWork>
{
    public static string Id => ReverseDijkstraFlow.Id;
    public static NavigationFlowSampling Sampling => NavigationFlowSampling.Successor;
    public static DijkstraTileWork Create(NavigationGridTileRange range) => new(range);
    public static bool HasWork(DijkstraTileWork work) => work.Open.Count > 0;

    public static int Solve(DijkstraTileWork work, NavigationTileFlowData state, int tile, int budget)
    {
        NavigationGrid grid = state.Input.Costs.Grid;
        ulong capabilities = state.Input.Profile.Capabilities & state.Input.Costs.Policy.AllowedCapabilities;
        for (int cell = work.Range.CellStart; cell < work.Range.CellStart + work.Range.CellCount; cell++)
        {
            if (!work.Started && state.Values[cell] == 0) work.Open.Enqueue(cell, 0);
            NavigationGridCell from = grid.Cells.Span[cell];
            for (int edge = from.EdgeStart; edge < from.EdgeStart + from.EdgeCount; edge++)
            {
                NavigationGridEdge transition = grid.Edges.Span[edge];
                if (grid.CellTiles.Span[transition.ToCell] == tile || (transition.RequiredCapabilities & ~capabilities) != 0) continue;
                Improve(cell, edge, state.BoundaryValues[transition.ToCell] + state.Input.Costs.EdgeCosts.Span[edge], state.BoundaryGoals[transition.ToCell]);
            }
        }
        work.Started = true;
        int used = 0;
        while (used < budget && work.Open.TryDequeue(out int to, out float value))
        {
            if (value != state.Values[to]) continue;
            used++;
            NavigationGridRange range = grid.IncomingRanges.Span[to];
            foreach (NavigationGridIncoming incoming in grid.IncomingEdges.Span.Slice(range.Start, range.Count))
            {
                if (grid.CellTiles.Span[incoming.FromCell] != tile ||
                    (grid.Edges.Span[incoming.Edge].RequiredCapabilities & ~capabilities) != 0) continue;
                Improve(incoming.FromCell, incoming.Edge, value + state.Input.Costs.EdgeCosts.Span[incoming.Edge], state.Goals[to]);
            }
        }
        return used;

        void Improve(int cell, int edge, float value, int goal)
        {
            if (value >= state.Values[cell]) return;
            state.Values[cell] = value;
            state.Successors[cell] = edge;
            state.Goals[cell] = goal;
            work.Open.Enqueue(cell, value);
        }
    }

    public static void BuildDirection(NavigationTileFlowData state, int cell)
    {
        int edge = state.Successors[cell];
        if (edge < 0) return;
        NavigationGrid grid = state.Input.Costs.Grid;
        Vector3 direction = grid.Cells.Span[grid.Edges.Span[edge].ToCell].Position - grid.Cells.Span[cell].Position;
        if (direction.LengthSquared() > 0) state.Directions[cell] = Vector3.Normalize(direction);
    }
}
