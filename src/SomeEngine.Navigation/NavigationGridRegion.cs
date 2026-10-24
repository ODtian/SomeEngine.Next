using System.Numerics;
using SomeEngine.Job;

namespace SomeEngine.Navigation;

public sealed record NavigationGridTile(NavigationTileCoordinate Tile, NavigationGrid Grid);
public readonly record struct NavigationGridTileRange(NavigationTileCoordinate Tile, int CellStart, int CellCount,
    ulong ContentVersion, bool Detailed = true);
public readonly record struct NavigationGridEntrance(NavigationTileCoordinate Tile, int Cell, Vector3 Position);
public readonly record struct NavigationGridRegionEdge(int From, int To, float Cost);
public readonly record struct NavigationGridCrossing(NavigationTileCoordinate From, int Cell,
    NavigationTileCoordinate To, NavigationGridEdge Edge);

/// <summary>A grid's own directed support-cell entrances and policy-weighted intra-tile routes.</summary>
public sealed class NavigationGridRegionGraph(NavigationBounds bounds, float cellSize, NavigationGridShape shape,
    ReadOnlyMemory<NavigationRegionTile> tiles, ReadOnlyMemory<NavigationGridEntrance> entrances,
    ReadOnlyMemory<NavigationGridRegionEdge> edges, ReadOnlyMemory<NavigationGridCrossing> crossings,
    ulong contentVersion, ulong costVersion = 1)
{
    public NavigationBounds Bounds { get; } = bounds;
    public float CellSize { get; } = cellSize;
    public NavigationGridShape Shape { get; } = shape;
    public ReadOnlyMemory<NavigationRegionTile> Tiles { get; } = tiles;
    public ReadOnlyMemory<NavigationGridEntrance> Entrances { get; } = entrances;
    public ReadOnlyMemory<NavigationGridRegionEdge> Edges { get; } = edges;
    public ReadOnlyMemory<NavigationGridCrossing> Crossings { get; } = crossings;
    public ulong ContentVersion { get; } = contentVersion;
    public ulong CostVersion { get; } = costVersion;

    internal NavigationTileCoordinate? FindTile(Vector3 position)
    {
        NavigationTileCoordinate? result = null;
        float closest = float.PositiveInfinity;
        foreach (NavigationRegionTile tile in Tiles.Span)
        {
            if (position.X < tile.Bounds.Minimum.X || position.X >= tile.Bounds.Maximum.X ||
                position.Z < tile.Bounds.Minimum.Z || position.Z >= tile.Bounds.Maximum.Z) continue;
            float distance = MathF.Abs(position.Y - Math.Clamp(position.Y, tile.Bounds.Minimum.Y, tile.Bounds.Maximum.Y));
            if (distance >= closest) continue;
            closest = distance;
            result = tile.Tile;
        }
        return result;
    }

    internal void AddReadAccesses(Scheduler scheduler, List<JobResourceAccess> accesses)
    {
        accesses.Add(NavigationJobAccess.Read(scheduler, Tiles));
        accesses.Add(NavigationJobAccess.Read(scheduler, Entrances));
        accesses.Add(NavigationJobAccess.Read(scheduler, Edges));
        accesses.Add(NavigationJobAccess.Read(scheduler, Crossings));
        accesses.Add(NavigationJobAccess.Read(scheduler, Shape.Footprint));
    }
}

public sealed class NavigationGridRegionBuildState(NavigationGrid grid, NavigationProfile profile, NavigationPolicy policy)
{
    public NavigationGrid Grid { get; internal set; } = null!;
    internal NavigationGrid Input { get; } = grid;
    internal NavigationProfile Profile { get; } = profile;
    internal NavigationPolicy Policy { get; } = policy;
    internal NavigationGridTile[] Tiles = [];
    internal NavigationRegionTile[] Directory = [];
    internal NavigationGridEntrance[] Entrances = [];
    internal NavigationGridCrossing[] Crossings = [];
    internal NavigationGridRegionEdge[][] LocalEdges = [];
    internal NavigationGridRegionEdge[] CrossingEdges = [];
}

public static class NavigationGridRegionBuilder
{
    public static NavigationBuild<NavigationGridRegionBuildState> Schedule(NavigationGrid grid, NavigationProfile profile,
        NavigationPolicy policy, Scheduler? scheduler = null, JobHandle dependency = default)
    {
        scheduler ??= JobSystem.Default;
        var state = new NavigationGridRegionBuildState(grid, profile, policy);
        var accesses = new List<JobResourceAccess>
        {
            NavigationJobAccess.Read(scheduler, policy.AreaCosts), NavigationJobAccess.Read(scheduler, policy.ActionCosts),
        };
        grid.AddReadAccesses(scheduler, accesses);
        return new(state, scheduler.Schedule(new SplitGrid(state, scheduler), accesses.ToArray(), dependency));
    }

    private readonly struct SplitGrid(NavigationGridRegionBuildState state, Scheduler scheduler) : IJob
    {
        public void Execute()
        {
            NavigationGrid input = state.Input;
            var groups = new Dictionary<NavigationTileCoordinate, List<int>>();
            var local = new int[input.Cells.Length];
            for (int cell = 0; cell < input.Cells.Length; cell++)
            {
                NavigationTileCoordinate tile = input.Cells.Span[cell].Tile;
                if (!groups.TryGetValue(tile, out List<int>? cells)) groups.Add(tile, cells = []);
                local[cell] = cells.Count;
                cells.Add(cell);
            }
            var tiles = new List<NavigationGridTile>();
            var crossings = new List<NavigationGridCrossing>();
            var entranceCells = new HashSet<int>();
            foreach (var group in groups)
            {
                var cells = new NavigationGridCell[group.Value.Count];
                var edges = new List<NavigationGridEdge>();
                Vector3 minimum = new(float.PositiveInfinity), maximum = new(float.NegativeInfinity);
                foreach (int cell in group.Value)
                {
                    NavigationGridCell source = input.Cells.Span[cell];
                    int first = edges.Count;
                    foreach (NavigationGridEdge edge in input.Edges.Span.Slice(source.EdgeStart, source.EdgeCount))
                    {
                        NavigationTileCoordinate target = input.Cells.Span[edge.ToCell].Tile;
                        if (target == group.Key) edges.Add(edge with { ToCell = local[edge.ToCell] });
                        else
                        {
                            crossings.Add(new(group.Key, local[cell], target, edge with { ToCell = local[edge.ToCell] }));
                            entranceCells.Add(cell);
                            entranceCells.Add(edge.ToCell);
                        }
                    }
                    cells[local[cell]] = source with { EdgeStart = first, EdgeCount = edges.Count - first };
                    Vector3 padding = new(input.CellSize * 0.5f, 0, input.CellSize * 0.5f);
                    minimum = Vector3.Min(minimum, source.Position - padding);
                    maximum = Vector3.Max(maximum, source.Position + padding);
                }
                tiles.Add(new(group.Key, new(new(minimum, maximum), input.CellSize, cells, edges.ToArray(), input.Shape, input.ContentVersion)));
            }
            var entrances = new List<NavigationGridEntrance>();
            var directory = new List<NavigationRegionTile>();
            var entranceIndices = new Dictionary<int, int>();
            foreach (NavigationGridTile tile in tiles)
            {
                int first = entrances.Count;
                foreach (int cell in groups[tile.Tile])
                    if (entranceCells.Contains(cell))
                    {
                        entranceIndices.Add(cell, entrances.Count);
                        entrances.Add(new(tile.Tile, local[cell], input.Cells.Span[cell].Position));
                    }
                directory.Add(new(tile.Tile, tile.Grid.Bounds, input.ContentVersion, first, entrances.Count - first));
            }
            NavigationCosts costs = NavigationCostSystem.Create(input, state.Policy, 1);
            var crossingEdges = new List<NavigationGridRegionEdge>();
            ulong capabilities = state.Profile.Capabilities & state.Policy.AllowedCapabilities;
            for (int cell = 0; cell < input.Cells.Length; cell++)
            {
                NavigationGridCell source = input.Cells.Span[cell];
                for (int edge = source.EdgeStart; edge < source.EdgeStart + source.EdgeCount; edge++)
                {
                    NavigationGridEdge target = input.Edges.Span[edge];
                    if (source.Tile == input.Cells.Span[target.ToCell].Tile || (target.RequiredCapabilities & ~capabilities) != 0) continue;
                    crossingEdges.Add(new(entranceIndices[cell], entranceIndices[target.ToCell], costs.EdgeCosts.Span[edge]));
                }
            }
            state.Tiles = tiles.ToArray(); state.Directory = directory.ToArray(); state.Entrances = entrances.ToArray();
            state.Crossings = crossings.ToArray(); state.CrossingEdges = crossingEdges.ToArray();
            state.LocalEdges = new NavigationGridRegionEdge[state.Tiles.Length][];
            var jobs = new JobHandle[state.Tiles.Length];
            for (int tile = 0; tile < jobs.Length; tile++)
            {
                var accesses = new List<JobResourceAccess>();
                state.Tiles[tile].Grid.AddReadAccesses(scheduler, accesses);
                accesses.Add(NavigationJobAccess.Read<NavigationGridEntrance>(scheduler, state.Entrances));
                accesses.Add(NavigationJobAccess.Read(scheduler, state.Policy.AreaCosts));
                accesses.Add(NavigationJobAccess.Read(scheduler, state.Policy.ActionCosts));
                jobs[tile] = scheduler.Schedule(new BuildLocalEdges(state, tile), accesses.ToArray());
            }
            scheduler.Schedule(new JoinGraph(state), scheduler.CombineDependencies(jobs));
        }
    }

    private readonly struct BuildLocalEdges(NavigationGridRegionBuildState state, int tileIndex) : IJob
    {
        public void Execute()
        {
            NavigationGrid grid = state.Tiles[tileIndex].Grid;
            NavigationRegionTile tile = state.Directory[tileIndex];
            NavigationCosts costs = NavigationCostSystem.Create(grid, state.Policy, 1);
            var result = new List<NavigationGridRegionEdge>();
            var values = new float[grid.Cells.Length];
            var queue = new PriorityQueue<int, float>();
            ulong capabilities = state.Profile.Capabilities & state.Policy.AllowedCapabilities;
            for (int target = tile.EntranceStart; target < tile.EntranceStart + tile.EntranceCount; target++)
            {
                Array.Fill(values, float.PositiveInfinity);
                int cell = state.Entrances[target].Cell;
                values[cell] = 0;
                queue.Enqueue(cell, 0);
                while (queue.TryDequeue(out int to, out float cost))
                {
                    if (cost != values[to]) continue;
                    NavigationGridRange range = grid.IncomingRanges.Span[to];
                    foreach (NavigationGridIncoming incoming in grid.IncomingEdges.Span.Slice(range.Start, range.Count))
                    {
                        if ((grid.Edges.Span[incoming.Edge].RequiredCapabilities & ~capabilities) != 0) continue;
                        float candidate = cost + costs.EdgeCosts.Span[incoming.Edge];
                        if (candidate >= values[incoming.FromCell]) continue;
                        values[incoming.FromCell] = candidate;
                        queue.Enqueue(incoming.FromCell, candidate);
                    }
                }
                for (int source = tile.EntranceStart; source < tile.EntranceStart + tile.EntranceCount; source++)
                    if (source != target && float.IsFinite(values[state.Entrances[source].Cell]))
                        result.Add(new(source, target, values[state.Entrances[source].Cell]));
            }
            state.LocalEdges[tileIndex] = result.ToArray();
        }
    }

    private readonly struct JoinGraph(NavigationGridRegionBuildState state) : IJob
    {
        public void Execute()
        {
            var graph = new NavigationGridRegionGraph(state.Input.Bounds, state.Input.CellSize, state.Input.Shape,
                state.Directory, state.Entrances, state.CrossingEdges.Concat(state.LocalEdges.SelectMany(edges => edges)).ToArray(),
                state.Crossings, state.Input.ContentVersion);
            state.Grid = NavigationGrid.Compose(graph, state.Tiles, default, state.Policy);
        }
    }
}
