namespace SomeEngine.Navigation;

public static partial class NavigationCostSystem
{
    // Residence and goal projections keep the current cost input as their shared source.
    // Tile content versions distinguish newly built geometry from the same acquired cells.
    internal static NavigationCosts Regrid(NavigationCosts current, NavigationGrid grid)
    {
        NavigationCosts source = current.Source ?? current;
        var cells = new float[grid.Cells.Length];
        var edges = new float[grid.Edges.Length];
        Fill(grid, current.Policy, cells, edges);
        CostCell?[] sourceCells = IdentifyCells(source.Grid), targetCells = IdentifyCells(grid);
        var values = new Dictionary<CostCell, (float Cost, bool Detailed)>();
        var transitions = new Dictionary<CostEdge, float>();
        for (int cell = 0; cell < sourceCells.Length; cell++)
        {
            if (sourceCells[cell] is not { } identity) continue;
            bool detailed = source.Grid.FlowRanges.Span[source.Grid.CellTiles.Span[cell]].Detailed;
            values[identity] = (source.CellCosts.Span[cell], detailed);
            NavigationGridCell from = source.Grid.Cells.Span[cell];
            for (int edge = from.EdgeStart; edge < from.EdgeStart + from.EdgeCount; edge++)
            {
                NavigationGridEdge transition = source.Grid.Edges.Span[edge];
                if (sourceCells[transition.ToCell] is not { } to) continue;
                transitions[new(identity, to, transition.Action, transition.RequiredCapabilities,
                    !detailed && identity.Tile == to.Tile)] = source.EdgeCosts.Span[edge];
            }
        }
        for (int cell = 0; cell < targetCells.Length; cell++)
        {
            if (targetCells[cell] is not { } identity) continue;
            bool detailed = grid.FlowRanges.Span[grid.CellTiles.Span[cell]].Detailed;
            if (values.TryGetValue(identity, out var value) && (value.Detailed || !detailed)) cells[cell] = value.Cost;
            NavigationGridCell from = grid.Cells.Span[cell];
            for (int edge = from.EdgeStart; edge < from.EdgeStart + from.EdgeCount; edge++)
            {
                NavigationGridEdge transition = grid.Edges.Span[edge];
                if (targetCells[transition.ToCell] is not { } to) continue;
                if (transitions.TryGetValue(new(identity, to, transition.Action, transition.RequiredCapabilities,
                    !detailed && identity.Tile == to.Tile), out float cost)) edges[edge] = cost;
            }
        }
        return new(grid, current.Policy, cells, edges, current.Version) { Source = source };
    }

    private static CostCell?[] IdentifyCells(NavigationGrid grid)
    {
        var cells = new CostCell?[grid.Cells.Length];
        foreach (NavigationGridTileRange tile in grid.FlowRanges.Span)
        {
            if (tile.Detailed)
            {
                for (int cell = 0; cell < tile.CellCount; cell++)
                    cells[tile.CellStart + cell] = new(tile.Tile, cell, tile.ContentVersion);
            }
            else
            {
                NavigationRegionTile directory = grid.RegionGraph!.Tiles.Span.Find(tile.Tile);
                for (int entrance = 0; entrance < directory.EntranceCount; entrance++)
                    cells[tile.CellStart + entrance] = new(tile.Tile,
                        grid.RegionGraph.Entrances.Span[directory.EntranceStart + entrance].Cell, tile.ContentVersion);
            }
        }
        return cells;
    }

    private readonly record struct CostCell(NavigationTileCoordinate Tile, int Cell, ulong ContentVersion);
    private readonly record struct CostEdge(CostCell From, CostCell To, int Action, ulong Capabilities, bool Coarse);
}
