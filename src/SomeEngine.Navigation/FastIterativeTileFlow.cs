using System.Numerics;

namespace SomeEngine.Navigation;

/// <summary>A tile's retained upwind connections and active cells between boundary exchanges.</summary>
internal sealed class FastIterativeTileWork(NavigationGridTileRange range)
{
    internal NavigationGridTileRange Range { get; } = range;
    internal Queue<int> Active { get; } = new();
    internal bool[] IsActive { get; } = new bool[range.CellCount];
    internal int[] Axes { get; } = new int[range.CellCount * 4];
    internal ulong BoundaryRound;
}

internal readonly struct FastIterativeTileSolver : INavigationTileFlowSolver<FastIterativeTileWork>
{
    public static string Id => FastIterativeFlow.Id;
    public static NavigationFlowSampling Sampling => NavigationFlowSampling.Gradient;
    public static FastIterativeTileWork Create(NavigationGridTileRange range) => new(range);
    public static bool HasWork(FastIterativeTileWork work) => work.Active.Count > 0;

    public static int Solve(FastIterativeTileWork work, NavigationTileFlowData state, int tile, int budget)
    {
        NavigationGrid grid = state.Input.Costs.Grid;
        ulong capabilities = state.Input.Profile.Capabilities & state.Input.Costs.Policy.AllowedCapabilities;
        if (work.BoundaryRound != state.Round)
        {
            for (int cell = work.Range.CellStart; cell < work.Range.CellStart + work.Range.CellCount; cell++)
            {
                int local = cell - work.Range.CellStart;
                if (work.BoundaryRound == 0)
                {
                    work.Axes[local * 4] = NavigationFlowSampler.FindEdge(state.Input, cell, -1, 0);
                    work.Axes[local * 4 + 1] = NavigationFlowSampler.FindEdge(state.Input, cell, 1, 0);
                    work.Axes[local * 4 + 2] = NavigationFlowSampler.FindEdge(state.Input, cell, 0, -1);
                    work.Axes[local * 4 + 3] = NavigationFlowSampler.FindEdge(state.Input, cell, 0, 1);
                    Activate(cell);
                }
                else
                {
                    NavigationGridCell from = grid.Cells.Span[cell];
                    foreach (NavigationGridEdge edge in grid.Edges.Span.Slice(from.EdgeStart, from.EdgeCount))
                        if (grid.CellTiles.Span[edge.ToCell] != tile) { Activate(cell); break; }
                }
            }
            work.BoundaryRound = state.Round;
        }
        int used = 0;
        while (used < budget && work.Active.TryDequeue(out int cell))
        {
            work.IsActive[cell - work.Range.CellStart] = false;
            used++;
            if (state.Values[cell] == 0) continue;
            float candidate;
            int source, successor;
            if (work.Range.Detailed)
            {
                int x = Minimum(cell, 0), z = Minimum(cell, 2);
                float a = x >= 0 ? state.Value(grid.Edges.Span[x].ToCell, tile) : float.PositiveInfinity;
                float b = z >= 0 ? state.Value(grid.Edges.Span[z].ToCell, tile) : float.PositiveInfinity;
                float distance = grid.CellSize * state.Input.Costs.CellCosts.Span[cell];
                float smaller = MathF.Min(a, b), difference = MathF.Abs(a - b);
                candidate = !float.IsFinite(smaller) || !float.IsFinite(distance) ? float.PositiveInfinity
                    : difference >= distance ? smaller + distance
                    : (a + b + MathF.Sqrt(2 * distance * distance - difference * difference)) * 0.5f;
                successor = a <= b ? x : z;
                source = successor >= 0 ? grid.Edges.Span[successor].ToCell : -1;
            }
            else
            {
                candidate = float.PositiveInfinity;
                source = successor = -1;
                NavigationGridCell from = grid.Cells.Span[cell];
                for (int edge = from.EdgeStart; edge < from.EdgeStart + from.EdgeCount; edge++)
                {
                    NavigationGridEdge transition = grid.Edges.Span[edge];
                    if ((transition.RequiredCapabilities & ~capabilities) != 0) continue;
                    float value = state.Value(transition.ToCell, tile) + state.Input.Costs.EdgeCosts.Span[edge];
                    if (value >= candidate) continue;
                    candidate = value;
                    successor = edge;
                    source = transition.ToCell;
                }
            }
            if (candidate >= state.Values[cell]) continue;
            state.Values[cell] = candidate;
            state.Goals[cell] = source >= 0 ? state.Goal(source, tile) : -1;
            state.Successors[cell] = successor;
            NavigationGridRange incoming = grid.IncomingRanges.Span[cell];
            foreach (NavigationGridIncoming neighbor in grid.IncomingEdges.Span.Slice(incoming.Start, incoming.Count))
                if (grid.CellTiles.Span[neighbor.FromCell] == tile) Activate(neighbor.FromCell);
        }
        return used;

        int Minimum(int cell, int firstAxis)
        {
            int selected = -1;
            float minimum = float.PositiveInfinity;
            int local = cell - work.Range.CellStart;
            for (int axis = firstAxis; axis < firstAxis + 2; axis++)
            {
                int edge = work.Axes[local * 4 + axis];
                if (edge < 0) continue;
                float value = state.Value(grid.Edges.Span[edge].ToCell, tile);
                if (value >= minimum) continue;
                minimum = value;
                selected = edge;
            }
            return selected;
        }
        void Activate(int cell)
        {
            int local = cell - work.Range.CellStart;
            if (work.IsActive[local]) return;
            work.IsActive[local] = true;
            work.Active.Enqueue(cell);
        }
    }

    public static void BuildDirection(NavigationTileFlowData state, int cell)
    {
        NavigationGrid grid = state.Input.Costs.Grid;
        if (!grid.IsDetailedCell(cell)) { DijkstraTileSolver.BuildDirection(state, cell); return; }
        state.Successors[cell] = -1;
        float value = state.Values[cell];
        if (!float.IsFinite(value) || value == 0) return;
        Span<int> axes = stackalloc int[4]
        {
            NavigationFlowSampler.FindEdge(state.Input, cell, -1, 0), NavigationFlowSampler.FindEdge(state.Input, cell, 1, 0),
            NavigationFlowSampler.FindEdge(state.Input, cell, 0, -1), NavigationFlowSampler.FindEdge(state.Input, cell, 0, 1),
        };
        Vector3 gradient = default;
        float minimum = value;
        for (int axis = 0; axis < 4; axis += 2)
        {
            int selected = -1;
            float lower = value;
            for (int direction = axis; direction < axis + 2; direction++)
            {
                int edge = axes[direction];
                if (edge < 0) continue;
                float neighbor = state.Values[grid.Edges.Span[edge].ToCell];
                if (neighbor >= lower) continue;
                lower = neighbor;
                selected = edge;
            }
            if (selected < 0) continue;
            Vector3 displacement = grid.Cells.Span[grid.Edges.Span[selected].ToCell].Position - grid.Cells.Span[cell].Position;
            gradient += displacement * ((value - lower) / (grid.CellSize * grid.CellSize));
            if (lower < minimum) { minimum = lower; state.Successors[cell] = selected; }
        }
        if (gradient.LengthSquared() > 0) state.Directions[cell] = Vector3.Normalize(gradient);
    }
}
