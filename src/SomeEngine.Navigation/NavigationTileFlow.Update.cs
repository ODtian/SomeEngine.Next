using System.Numerics;

namespace SomeEngine.Navigation;

internal static partial class NavigationTileFlow<TSolver, TWork>
    where TSolver : struct, INavigationTileFlowSolver<TWork>
    where TWork : class
{
    internal static void Update(NavigationTileFlowState<TWork> state, NavigationFlowInput input)
    {
        NavigationFlowInput before = state.Input;
        NavigationGrid oldGrid = before.Costs.Grid, grid = input.Costs.Grid;
        var mapping = new int[oldGrid.Cells.Length];
        Array.Fill(mapping, -1);
        var indices = new Dictionary<(Vector3 Position, int Layer, NavigationTileCoordinate Tile, bool Detailed), int>();
        for (int cell = 0; cell < grid.Cells.Length; cell++) indices[Key(grid, cell)] = cell;
        for (int cell = 0; cell < mapping.Length; cell++)
            if (indices.TryGetValue(Key(oldGrid, cell), out int next)) mapping[cell] = next;
        var edges = new int[oldGrid.Edges.Length];
        Array.Fill(edges, -1);
        var goalMapping = new int[before.Goals.Length];
        for (int goal = 0; goal < goalMapping.Length; goal++) goalMapping[goal] = input.Goals.Span.IndexOf(before.Goals.Span[goal]);
        int[] targets = FindTargets(input);
        var changed = new bool[grid.Cells.Length];
        Array.Fill(changed, true);
        var invalid = new bool[mapping.Length];
        var raised = new Queue<int>();
        var dependents = new List<int>[mapping.Length];
        for (int cell = 0; cell < dependents.Length; cell++) dependents[cell] = [];
        if (state.Started)
            foreach (TWork work in state.Work) TSolver.AddDependencies(work, state, dependents);
        for (int cell = 0; cell < mapping.Length; cell++)
        {
            int next = mapping[cell];
            if (next < 0) { Raise(cell); continue; }
            changed[next] = !state.Started;
            float oldCost = before.Costs.CellCosts.Span[cell], cost = input.Costs.CellCosts.Span[next];
            if (cost != oldCost) changed[next] = true;
            if (cost > oldCost || oldGrid.CellSize != grid.CellSize) Raise(cell);
            if (state.Started && state.Targets[cell] >= 0 &&
                (goalMapping[state.Targets[cell]] < 0 || targets[next] != goalMapping[state.Targets[cell]])) Raise(cell);
            NavigationGridCell oldCell = oldGrid.Cells.Span[cell], nextCell = grid.Cells.Span[next];
            if (oldCell.EdgeCount != nextCell.EdgeCount) changed[next] = true;
            for (int edge = oldCell.EdgeStart; edge < oldCell.EdgeStart + oldCell.EdgeCount; edge++)
            {
                NavigationGridEdge transition = oldGrid.Edges.Span[edge];
                int destination = mapping[transition.ToCell];
                for (int candidate = nextCell.EdgeStart; candidate < nextCell.EdgeStart + nextCell.EdgeCount; candidate++)
                {
                    NavigationGridEdge other = grid.Edges.Span[candidate];
                    if (other.ToCell == destination && other.Action == transition.Action &&
                        other.RequiredCapabilities == transition.RequiredCapabilities) { edges[edge] = candidate; break; }
                }
                float previous = EdgeCost(before, edge), current = edges[edge] >= 0 ? EdgeCost(input, edges[edge]) : float.PositiveInfinity;
                if (previous != current) changed[next] = true;
                if (current > previous) Raise(cell);
            }
            for (int edge = nextCell.EdgeStart; edge < nextCell.EdgeStart + nextCell.EdgeCount; edge++)
            {
                bool existing = false;
                for (int old = oldCell.EdgeStart; old < oldCell.EdgeStart + oldCell.EdgeCount; old++)
                    if (edges[old] == edge) { existing = true; break; }
                if (!existing) changed[next] = true;
            }
        }
        while (raised.TryDequeue(out int cell))
            foreach (int dependent in dependents[cell]) Raise(dependent);

        var values = new float[grid.Cells.Length];
        var successors = new int[grid.Cells.Length];
        var goals = new int[grid.Cells.Length];
        var directions = new Vector3[grid.Cells.Length];
        Array.Fill(values, float.PositiveInfinity);
        Array.Fill(successors, -1);
        Array.Fill(goals, -1);
        if (state.Started)
        for (int old = 0; old < mapping.Length; old++)
        {
            int cell = mapping[old];
            if (cell < 0) continue;
            if (invalid[old]) { changed[cell] = true; continue; }
            values[cell] = state.Values[old];
            successors[cell] = state.Successors[old] >= 0 ? edges[state.Successors[old]] : -1;
            goals[cell] = state.Goals[old] >= 0 ? goalMapping[state.Goals[old]] : -1;
            directions[cell] = state.Directions[old];
            if (state.Values[old] != state.BoundaryValues[old]) changed[cell] = true;
        }
        for (int cell = 0; cell < targets.Length; cell++)
        {
            if (targets[cell] < 0) continue;
            changed[cell] |= values[cell] != 0 || goals[cell] != targets[cell];
            values[cell] = 0;
            goals[cell] = targets[cell];
            successors[cell] = -1;
            directions[cell] = default;
        }
        for (int index = state.Processed; index < state.RoundTiles.Length; index++)
        {
            NavigationGridTileRange range = oldGrid.FlowRanges.Span[state.RoundTiles[index]];
            for (int cell = range.CellStart; cell < range.CellStart + range.CellCount; cell++)
                if (mapping[cell] >= 0) changed[mapping[cell]] = true;
        }
        int[] changedCells = Enumerable.Range(0, changed.Length).Where(cell => changed[cell]).ToArray();
        foreach (int cell in changedCells)
        {
            NavigationGridRange incoming = grid.IncomingRanges.Span[cell];
            foreach (NavigationGridIncoming from in grid.IncomingEdges.Span.Slice(incoming.Start, incoming.Count)) changed[from.FromCell] = true;
        }
        var oldWork = new Dictionary<NavigationTileCoordinate, TWork>();
        for (int tile = 0; tile < state.Work.Length; tile++) oldWork[oldGrid.FlowRanges.Span[tile].Tile] = state.Work[tile];
        state.Result.Input = input;
        state.Result.Status = NavigationFlowStatus.Searching;
        state.Values = values;
        state.Successors = successors;
        state.Goals = goals;
        state.Directions = directions;
        state.Targets = targets;
        state.BoundaryValues = new float[values.Length];
        state.BoundaryGoals = new int[values.Length];
        state.Active = new bool[grid.FlowRanges.Length];
        state.ChangedTiles = new bool[grid.FlowRanges.Length];
        state.Work = new TWork[grid.FlowRanges.Length];
        for (int tile = 0; tile < state.Work.Length; tile++)
        {
            NavigationGridTileRange range = grid.FlowRanges.Span[tile];
            bool retained = oldWork.TryGetValue(range.Tile, out TWork? existing);
            TWork work = retained ? existing! : TSolver.Create(range);
            state.Work[tile] = work;
            TSolver.Rebind(work, state, range, retained ? mapping : [], changed);
            for (int cell = range.CellStart; cell < range.CellStart + range.CellCount; cell++) state.Active[tile] |= changed[cell];
            state.Active[tile] |= TSolver.HasWork(work);
            state.ChangedTiles[tile] = state.Active[tile];
        }
        var affected = new Queue<int>(Enumerable.Range(0, changed.Length).Where(cell => changed[cell]));
        while (affected.TryDequeue(out int cell))
        {
            state.ChangedTiles[grid.CellTiles.Span[cell]] = true;
            NavigationGridRange incoming = grid.IncomingRanges.Span[cell];
            foreach (NavigationGridIncoming edge in grid.IncomingEdges.Span.Slice(incoming.Start, incoming.Count))
                if (!changed[edge.FromCell]) { changed[edge.FromCell] = true; affected.Enqueue(edge.FromCell); }
        }
        state.Started = true;
        StartRound(state);

        void Raise(int cell)
        {
            if (invalid[cell]) return;
            invalid[cell] = true;
            raised.Enqueue(cell);
        }
    }

    private static (Vector3, int, NavigationTileCoordinate, bool) Key(NavigationGrid grid, int cell)
    {
        NavigationGridCell value = grid.Cells.Span[cell];
        return (value.Position, value.Layer, value.Tile, grid.IsDetailedCell(cell));
    }

    private static float EdgeCost(NavigationFlowInput input, int edge)
    {
        ulong capabilities = input.Profile.Capabilities & input.Costs.Policy.AllowedCapabilities;
        return (input.Costs.Grid.Edges.Span[edge].RequiredCapabilities & ~capabilities) == 0
            ? input.Costs.EdgeCosts.Span[edge] : float.PositiveInfinity;
    }

    private static int[] FindTargets(NavigationFlowInput input)
    {
        NavigationGrid grid = input.Costs.Grid;
        var targets = new int[grid.Cells.Length];
        Array.Fill(targets, -1);
        for (int goal = 0; goal < input.Goals.Length; goal++)
        {
            NavigationFlowGoal target = input.Goals.Span[goal];
            bool seeded = false;
            for (int cell = 0; cell < grid.Cells.Length; cell++)
                if (Vector3.Distance(grid.Cells.Span[cell].Position, target.Position) <= target.ArrivalRange)
                { Seed(cell, goal); seeded = true; }
            if (!seeded)
            {
                int cell = grid.FindCell(target.Position);
                if (cell >= 0) Seed(cell, goal);
            }
        }
        return targets;
        void Seed(int cell, int goal)
        {
            if (float.IsFinite(input.Costs.CellCosts.Span[cell])) targets[cell] = goal;
        }
    }
}
