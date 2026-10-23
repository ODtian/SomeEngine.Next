using System.Numerics;
using SomeEngine.Job;

namespace SomeEngine.Navigation;

/// <summary>One fixed boundary round and the mutable arrays owned by its concrete solver.</summary>
internal class NavigationTileFlowData(NavigationFlowBuildState result, float[] values, int[] successors,
    int[] goals, Vector3[] directions)
{
    internal NavigationFlowBuildState Result { get; } = result;
    internal NavigationFlowInput Input => Result.Input;
    internal float[] Values { get; set; } = values;
    internal int[] Successors { get; set; } = successors;
    internal int[] Goals { get; set; } = goals;
    internal Vector3[] Directions { get; set; } = directions;
    internal int[] Targets { get; set; } = new int[values.Length];
    internal float[] TargetCosts { get; set; } = new float[values.Length];
    internal float[] BoundaryValues { get; set; } = new float[values.Length];
    internal int[] BoundaryGoals { get; set; } = new int[values.Length];
    internal bool[] Active { get; set; } = new bool[result.Input.Costs.Grid.FlowRanges.Length];
    internal bool[] ChangedTiles { get; set; } = new bool[result.Input.Costs.Grid.FlowRanges.Length];
    internal int[] RoundTiles = [];
    internal int Processed;
    internal ulong Round;
    internal bool Started;

    internal float Value(int cell, int tile) => Input.Costs.Grid.CellTiles.Span[cell] == tile ? Values[cell] : BoundaryValues[cell];
    internal int Goal(int cell, int tile) => Input.Costs.Grid.CellTiles.Span[cell] == tile ? Goals[cell] : BoundaryGoals[cell];
    internal void AddReadAccesses(Scheduler scheduler, List<JobResourceAccess> accesses)
    {
        accesses.Add(NavigationJobAccess.Read<float>(scheduler, Values));
        accesses.Add(NavigationJobAccess.Read<int>(scheduler, Successors));
        accesses.Add(NavigationJobAccess.Read<int>(scheduler, Goals));
        accesses.Add(NavigationJobAccess.Read<Vector3>(scheduler, Directions));
        accesses.Add(NavigationJobAccess.Read<int>(scheduler, Targets));
        accesses.Add(NavigationJobAccess.Read<float>(scheduler, TargetCosts));
    }
}

internal sealed class NavigationTileFlowState<TWork>(NavigationFlowBuildState result, float[] values, int[] successors,
    int[] goals, Vector3[] directions) : NavigationTileFlowData(result, values, successors, goals, directions)
    where TWork : class
{
    internal NavigationTileFlowState(NavigationFlowBuildState result) : this(result,
        new float[result.Input.Costs.Grid.Cells.Length], new int[result.Input.Costs.Grid.Cells.Length],
        new int[result.Input.Costs.Grid.Cells.Length], new Vector3[result.Input.Costs.Grid.Cells.Length]) { }
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
    static abstract void AddDependencies(TWork work, NavigationTileFlowData state, List<int>[] dependents);
    static abstract void Rebind(TWork work, NavigationTileFlowData state, NavigationGridTileRange range, int[] mapping, bool[] changed);
}

/// <summary>Runs each active tile against a completed boundary round, then exchanges its changed entrances.</summary>
internal static partial class NavigationTileFlow<TSolver, TWork>
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
        state.Input.HeadingGraph?.AddReadAccesses(scheduler, accesses);
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
        state.Targets = FindTargets(state.Input, out float[] targetCosts);
        state.TargetCosts = targetCosts;
        Array.Fill(state.ChangedTiles, true);
        state.Work = new TWork[grid.FlowRanges.Length];
        for (int tile = 0; tile < state.Work.Length; tile++) state.Work[tile] = TSolver.Create(grid.FlowRanges.Span[tile]);
        for (int cell = 0; cell < state.Targets.Length; cell++)
        {
            int goal = state.Targets[cell];
            if (goal < 0) continue;
            state.Values[cell] = state.TargetCosts[cell];
            state.Goals[cell] = goal;
            state.Active[grid.CellTiles.Span[cell]] = true;
        }
        StartRound(state);
    }

    private static void StartRound(NavigationTileFlowState<TWork> state)
    {
        Array.Copy(state.Values, state.BoundaryValues, state.Values.Length);
        Array.Copy(state.Goals, state.BoundaryGoals, state.Goals.Length);
        state.RoundTiles = Enumerable.Range(0, state.Active.Length).Where(tile => state.Active[tile]).ToArray();
        Array.Clear(state.Active);
        state.Processed = 0;
        state.Round++;
        UpdateTileStates(state);
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
            NavigationGridTileRange range = state.Input.Costs.Grid.FlowRanges.Span[tile];
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
            NavigationGridTileRange range = state.Input.Costs.Grid.FlowRanges.Span[tile];
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
            NavigationFlowTile[] tiles = grid.FlowRanges.ToArray().Select(tile => new NavigationFlowTile(tile.Tile,
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
            state.Result.Field = new(input, state.Values.ToArray(), state.Successors.ToArray(), state.Directions.ToArray(), state.Goals.ToArray(),
                dependencies, TSolver.Id, TSolver.Sampling, tiles, boundaries.ToArray(), required, state.Round);
            state.Result.Status = NavigationFlowStatus.Ready;
            Array.Clear(state.ChangedTiles);
            UpdateTileStates(state);
        }
    }

    private static void UpdateTileStates(NavigationTileFlowData state) =>
        state.Result.TileStates = state.Input.Costs.Grid.FlowRanges.ToArray().Select((tile, index) => new NavigationFlowTileState(
            tile.Tile, state.ChangedTiles[index] ? NavigationFlowStatus.Searching : NavigationFlowStatus.Ready,
            state.Input.Version, state.Input.GoalVersion, tile.Detailed, tile.ContentVersion,
            state.Input.Costs.Version, state.Input.Costs.Grid.ResidenceVersion)).ToArray();
}

internal sealed class DijkstraTileWork(NavigationGridTileRange range)
{
    internal NavigationGridTileRange Range { get; set; } = range;
    internal PriorityQueue<int, float> Open { get; } = new();
    internal Queue<int> Changed { get; } = new();
    internal bool Started;
}

internal readonly struct DijkstraTileSolver : INavigationTileFlowSolver<DijkstraTileWork>
{
    public static string Id => ReverseDijkstraFlow.Id;
    public static NavigationFlowSampling Sampling => NavigationFlowSampling.Successor;
    public static DijkstraTileWork Create(NavigationGridTileRange range) => new(range);
    public static bool HasWork(DijkstraTileWork work) => work.Open.Count > 0 || work.Changed.Count > 0;

    public static void AddDependencies(DijkstraTileWork work, NavigationTileFlowData state, List<int>[] dependents)
    {
        for (int cell = work.Range.CellStart; cell < work.Range.CellStart + work.Range.CellCount; cell++)
        {
            int edge = state.Successors[cell];
            if (edge >= 0) dependents[state.Input.Costs.Grid.Edges.Span[edge].ToCell].Add(cell);
        }
    }

    public static void Rebind(DijkstraTileWork work, NavigationTileFlowData state, NavigationGridTileRange range, int[] mapping, bool[] changed)
    {
        int[] queued = work.Open.UnorderedItems.Select(item => item.Element).ToArray();
        int[] altered = work.Changed.ToArray();
        work.Range = range;
        work.Open.Clear();
        work.Changed.Clear();
        foreach (int old in queued)
            if (mapping[old] >= range.CellStart && mapping[old] < range.CellStart + range.CellCount && float.IsFinite(state.Values[mapping[old]]))
                work.Open.Enqueue(mapping[old], state.Values[mapping[old]]);
        foreach (int old in altered)
            if (mapping[old] >= range.CellStart && mapping[old] < range.CellStart + range.CellCount) changed[mapping[old]] = true;
        for (int cell = range.CellStart; cell < range.CellStart + range.CellCount; cell++)
            if (changed[cell]) work.Changed.Enqueue(cell);
        work.Started = true;
    }

    public static int Solve(DijkstraTileWork work, NavigationTileFlowData state, int tile, int budget)
    {
        NavigationGrid grid = state.Input.Costs.Grid;
        ulong capabilities = state.Input.Profile.Capabilities & state.Input.Costs.Policy.AllowedCapabilities;
        for (int cell = work.Range.CellStart; cell < work.Range.CellStart + work.Range.CellCount; cell++)
        {
            if (!work.Started && state.Targets[cell] >= 0) work.Open.Enqueue(cell, state.Values[cell]);
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
        while (used < budget && work.Changed.TryDequeue(out int cell))
        {
            used++;
            if (state.Targets[cell] >= 0) work.Open.Enqueue(cell, state.Values[cell]);
            NavigationGridCell from = grid.Cells.Span[cell];
            for (int edge = from.EdgeStart; edge < from.EdgeStart + from.EdgeCount; edge++)
            {
                NavigationGridEdge transition = grid.Edges.Span[edge];
                if ((transition.RequiredCapabilities & ~capabilities) != 0) continue;
                Improve(cell, edge, state.Value(transition.ToCell, tile) + state.Input.Costs.EdgeCosts.Span[edge], state.Goal(transition.ToCell, tile));
            }
        }
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
        state.Directions[cell] = default;
        int edge = state.Successors[cell];
        if (edge < 0) return;
        NavigationGrid grid = state.Input.Costs.Grid;
        Vector3 direction = grid.Cells.Span[grid.Edges.Span[edge].ToCell].Position - grid.Cells.Span[cell].Position;
        if (direction.LengthSquared() > 0) state.Directions[cell] = Vector3.Normalize(direction);
    }
}
