using System.Numerics;
using SomeEngine.Job;

namespace SomeEngine.Navigation.Tests;

public sealed class NavigationGridBuildTests
{
    [Fact]
    public void CompletedGridConnectsTileBordersAndPreservesLayerActions()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 3, ManagedPayloadPolicy = ManagedPayloadPolicy.Allow });
        var tiles = new NavigationSurfaceTile[2];
        for (int tile = 0; tile < tiles.Length; tile++)
        {
            var columns = new NavigationSurfaceColumn[4];
            var spans = new NavigationSurfaceSpan[8];
            for (int column = 0; column < columns.Length; column++)
            {
                columns[column] = new(column * 2, 2);
                spans[column * 2] = new(-0.1f, 0, 0, Vector3.UnitY, 2);
                spans[column * 2 + 1] = new(2.9f, 3, 3, Vector3.UnitY, 7);
            }
            tiles[tile] = new(new(tile, 0), new(new(tile * 2, -1, 0), new(tile * 2 + 2, 4, 2)),
                new(tile * 2, 0), 1, 2, 2, columns, spans);
        }
        var surface = new NavigationSurface(new(new(0, -1, 0), new(4, 4, 2)),
            new() { CellSize = 1, TileSize = 2 }, tiles);
        var profile = new NavigationProfile([new Vector2(-0.1f), new(0.1f, -0.1f), new(0.1f), new(-0.1f, 0.1f)], 1);
        NavigationGrid grid = NavigationGridBuilder.Schedule(surface, profile,
            [new(new(0.5f, 0, 0.5f), new(3.5f, 3, 1.5f), 5, Action: 4, Bidirectional: true)],
            contentVersion: 9, scheduler: scheduler, footprintMode: NavigationGridFootprintMode.Point).Complete().Grid;

        Assert.Equal(16, grid.Cells.Length);
        Assert.Equal(9UL, grid.ContentVersion);
        int lower = grid.FindCell(new(1.5f, 0, 0.5f));
        int neighbor = grid.FindCell(new(2.5f, 0, 0.5f));
        NavigationGridCell cell = grid.Cells.Span[lower];
        Assert.Contains(grid.Edges.Slice(cell.EdgeStart, cell.EdgeCount).ToArray(), edge => edge.ToCell == neighbor && edge.Cost == 1);
        Assert.Equal(2, cell.Area);
        Assert.Equal(0, cell.Layer);
        int from = grid.FindCell(new(0.5f, 0, 0.5f));
        int to = grid.FindCell(new(3.5f, 3, 1.5f));
        NavigationGridCell upper = grid.Cells.Span[to];
        Assert.Equal(7, upper.Area);
        Assert.Equal(1, upper.Layer);
        cell = grid.Cells.Span[from];
        Assert.Contains(grid.Edges.Slice(cell.EdgeStart, cell.EdgeCount).ToArray(), edge => edge.ToCell == to && edge.Cost == 5 && edge.Action == 4);
        Assert.Contains(grid.Edges.Slice(upper.EdgeStart, upper.EdgeCount).ToArray(), edge => edge.ToCell == from && edge.Action == 4);
    }
}