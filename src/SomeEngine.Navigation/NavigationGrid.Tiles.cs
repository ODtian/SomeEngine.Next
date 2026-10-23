using System.Numerics;
using SomeEngine.Job;

namespace SomeEngine.Navigation;

public sealed partial class NavigationGrid
{
    public static NavigationGrid CombineTiles(NavigationGridRegionGraph graph, ReadOnlyMemory<NavigationGridTile> tiles) =>
        Compose(graph, tiles, default, new NavigationPolicy());

    internal static NavigationGrid Compose(NavigationGridRegionGraph graph, ReadOnlyMemory<NavigationGridTile> tiles,
        ReadOnlyMemory<NavigationFlowGoal> goals, NavigationPolicy policy)
    {
        var resident = tiles.ToArray().ToDictionary(tile => tile.Tile, tile => tile.Grid);
        var cells = new List<NavigationGridCell>();
        var outgoing = new List<List<NavigationGridEdge>>();
        var mapping = new Dictionary<(NavigationTileCoordinate Tile, int Cell), int>();
        var entranceIndices = new Dictionary<(NavigationTileCoordinate Tile, int Cell), int>();
        var ranges = new List<NavigationGridTileRange>();
        var goalCells = new List<(int Cell, int Goal)>();
        for (int index = 0; index < graph.Entrances.Length; index++)
        {
            NavigationGridEntrance entrance = graph.Entrances.Span[index];
            entranceIndices.Add((entrance.Tile, entrance.Cell), index);
        }
        foreach (NavigationRegionTile tile in graph.Tiles.Span)
        {
            int first = cells.Count;
            if (resident.TryGetValue(tile.Tile, out NavigationGrid? grid))
            {
                for (int cell = 0; cell < grid.Cells.Length; cell++)
                {
                    mapping.Add((tile.Tile, cell), cells.Count);
                    cells.Add(grid.Cells.Span[cell]);
                    outgoing.Add([]);
                }
            }
            else
            {
                foreach (NavigationGridEntrance entrance in graph.Entrances.Span.Slice(tile.EntranceStart, tile.EntranceCount))
                {
                    mapping.Add((tile.Tile, entrance.Cell), cells.Count);
                    cells.Add(new(entrance.Position, 0, 0, Tile: tile.Tile));
                    outgoing.Add([]);
                }
                for (int goal = 0; goal < goals.Length; goal++)
                    if (graph.FindTile(goals.Span[goal].Position) == tile.Tile)
                    {
                        goalCells.Add((cells.Count, goal));
                        cells.Add(new(goals.Span[goal].Position, 0, 0, Tile: tile.Tile));
                        outgoing.Add([]);
                    }
            }
            ranges.Add(new(tile.Tile, first, cells.Count - first, tile.ContentVersion, grid is not null));
        }
        foreach (NavigationGridTile tile in tiles.Span)
        for (int cell = 0; cell < tile.Grid.Cells.Length; cell++)
        {
            NavigationGridCell from = tile.Grid.Cells.Span[cell];
            List<NavigationGridEdge> edges = outgoing[mapping[(tile.Tile, cell)]];
            foreach (NavigationGridEdge edge in tile.Grid.Edges.Span.Slice(from.EdgeStart, from.EdgeCount))
                edges.Add(edge with { ToCell = mapping[(tile.Tile, edge.ToCell)] });
        }
        var crossingCosts = new Dictionary<(int From, int To), float>();
        foreach (NavigationGridRegionEdge edge in graph.Edges.Span)
        {
            NavigationGridEntrance from = graph.Entrances.Span[edge.From], to = graph.Entrances.Span[edge.To];
            if (from.Tile != to.Tile) { crossingCosts[(edge.From, edge.To)] = edge.Cost; continue; }
            if (resident.ContainsKey(from.Tile)) continue;
            outgoing[mapping[(from.Tile, from.Cell)]].Add(new(mapping[(to.Tile, to.Cell)], edge.Cost, IncludesPolicyCost: true));
        }
        foreach (NavigationGridCrossing crossing in graph.Crossings.Span)
        {
            NavigationGridEdge edge = crossing.Edge with { ToCell = mapping[(crossing.To, crossing.Edge.ToCell)] };
            if (!resident.ContainsKey(crossing.From) || !resident.ContainsKey(crossing.To))
            {
                int from = entranceIndices[(crossing.From, crossing.Cell)], to = entranceIndices[(crossing.To, crossing.Edge.ToCell)];
                edge = edge with { Cost = crossingCosts.GetValueOrDefault((from, to), float.PositiveInfinity), IncludesPolicyCost = true };
            }
            outgoing[mapping[(crossing.From, crossing.Cell)]].Add(edge);
        }
        float minimumCost = policy.DefaultAreaCost;
        foreach (NavigationAreaCost cost in policy.AreaCosts.Span) minimumCost = MathF.Min(minimumCost, cost.Multiplier);
        foreach ((int cell, int goal) in goalCells)
        {
            NavigationFlowGoal target = goals.Span[goal];
            foreach (NavigationGridEntrance entrance in graph.Entrances.Span)
                if (entrance.Tile == cells[cell].Tile)
                    outgoing[mapping[(entrance.Tile, entrance.Cell)]].Add(new(cell,
                        MathF.Max(0, Vector3.Distance(entrance.Position, target.Position) - target.ArrivalRange) * minimumCost,
                        IncludesPolicyCost: true));
        }
        var allEdges = new List<NavigationGridEdge>();
        for (int cell = 0; cell < cells.Count; cell++)
        {
            cells[cell] = cells[cell] with { EdgeStart = allEdges.Count, EdgeCount = outgoing[cell].Count };
            allEdges.AddRange(outgoing[cell]);
        }
        return new(graph.Bounds, graph.CellSize, cells.ToArray(), allEdges.ToArray(), graph.Shape,
            graph.ContentVersion, graph, tiles, ranges.ToArray());
    }

    internal void AddReadAccesses(Scheduler scheduler, List<JobResourceAccess> accesses)
    {
        accesses.Add(NavigationJobAccess.Read(scheduler, Cells));
        accesses.Add(NavigationJobAccess.Read(scheduler, Edges));
        accesses.Add(NavigationJobAccess.Read(scheduler, IncomingRanges));
        accesses.Add(NavigationJobAccess.Read(scheduler, IncomingEdges));
        accesses.Add(NavigationJobAccess.Read(scheduler, Shape.Footprint));
        accesses.Add(NavigationJobAccess.Read(scheduler, CellTiles));
        accesses.Add(NavigationJobAccess.Read(scheduler, TileRanges));
        RegionGraph?.AddReadAccesses(scheduler, accesses);
    }
}
