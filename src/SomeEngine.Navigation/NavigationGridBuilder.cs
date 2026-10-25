using System.Numerics;
using SomeEngine.Job;

namespace SomeEngine.Navigation;

/// <summary>An explicit layer/action connection; a zero Cost selects endpoint distance.</summary>
public readonly record struct NavigationGridConnection(Vector3 From, Vector3 To, float Cost = 0,
    ulong RequiredCapabilities = 0, int Action = 0, bool Bidirectional = false);

public sealed class NavigationGridBuildState(NavigationSurface surface, NavigationProfile profile,
    ReadOnlyMemory<NavigationGridConnection> connections, NavigationGridFootprintMode footprintMode, ulong contentVersion)
{
    public NavigationSurface Surface { get; } = surface;
    public NavigationProfile Profile { get; } = profile;
    public ReadOnlyMemory<NavigationGridConnection> Connections { get; } = connections;
    public NavigationGridFootprintMode FootprintMode { get; } = footprintMode;
    public ulong ContentVersion { get; } = contentVersion;
    public NavigationGrid Grid { get; internal set; } = null!;
}

public static class NavigationGridBuilder
{
    public static NavigationBuild<NavigationGridBuildState> Schedule(NavigationSurface surface, NavigationProfile profile,
        NavigationGridConnection[] connections, ulong contentVersion = 1,
        Scheduler? scheduler = null, JobHandle dependency = default,
        NavigationGridFootprintMode footprintMode = NavigationGridFootprintMode.AllHeadings) =>
        Schedule(surface, profile, (ReadOnlyMemory<NavigationGridConnection>)connections, contentVersion, scheduler, dependency, footprintMode);

    public static NavigationBuild<NavigationGridBuildState> Schedule(NavigationSurface surface, NavigationProfile profile,
        ReadOnlyMemory<NavigationGridConnection> connections = default, ulong contentVersion = 1,
        Scheduler? scheduler = null, JobHandle dependency = default,
        NavigationGridFootprintMode footprintMode = NavigationGridFootprintMode.AllHeadings)
    {
        scheduler ??= JobSystem.Default;
        var state = new NavigationGridBuildState(surface, profile, connections, footprintMode, contentVersion);
        var accesses = new List<JobResourceAccess>
        {
            NavigationJobAccess.Read(scheduler, surface.Tiles), NavigationJobAccess.Read(scheduler, profile.Footprint),
            NavigationJobAccess.Read(scheduler, connections), JobResourceAccess.Exclusive(scheduler.GetResourceToken(state)),
        };
        foreach (NavigationSurfaceTile tile in surface.Tiles.Span)
        {
            accesses.Add(NavigationJobAccess.Read(scheduler, tile.Columns));
            accesses.Add(NavigationJobAccess.Read(scheduler, tile.Spans));
        }
        return new(state, scheduler.Schedule(new BuildGrid(state), accesses.ToArray(), dependency));
    }

    private readonly record struct CellSupport(Vector3 Position, NavigationSurfaceSpan Span,
        float Ceiling, int Layer, NavigationTileCoordinate Tile);

    private readonly struct BuildGrid(NavigationGridBuildState state) : IJob
    {
        public void Execute()
        {
            NavigationSurface surface = state.Surface;
            NavigationProfile profile = state.Profile;
            var policy = new NavigationPolicy();
            float cellSize = surface.Settings.CellSize;
            NavigationCellOffset[] footprint = state.FootprintMode switch
            {
                NavigationGridFootprintMode.Point => [new(0, 0)],
                NavigationGridFootprintMode.HeadingZero => NavigationSurfaceTraversal.RasterizeFootprint(profile.Footprint.Span, cellSize, cellSize * 0.5f),
                _ => NavigationSurfaceTraversal.RasterizeFootprintSweep(profile.Footprint.Span, cellSize),
            };
            var tileSupports = new List<CellSupport>[surface.Tiles.Length];
            JobSystem.ExecuteParallel(new CollectSupports(surface, profile, policy, footprint, tileSupports),
                tileSupports.Length, batchSize: 1);
            var tileStarts = new int[tileSupports.Length + 1];
            for (int tile = 0; tile < tileSupports.Length; tile++)
                tileStarts[tile + 1] = tileStarts[tile] + tileSupports[tile].Count;
            var supports = new CellSupport[tileStarts[^1]];
            JobSystem.ExecuteParallel(new WriteSupports(tileSupports, tileStarts, supports), tileSupports.Length,
                batchSize: 1);
            var columns = new Dictionary<NavigationCellOffset, List<int>>();
            for (int cell = 0; cell < supports.Length; cell++)
            {
                NavigationCellOffset coordinate = Coordinate(surface, supports[cell].Position);
                if (!columns.TryGetValue(coordinate, out List<int>? column))
                    columns.Add(coordinate, column = []);
                column.Add(cell);
            }
            var edges = new List<NavigationGridEdge>[supports.Length];
            JobSystem.ExecuteParallel(new ConnectCells(surface, profile, supports, columns, edges), supports.Length,
                batchSize: JobScheduleOptions.AutomaticBatchSize);
            foreach (NavigationGridConnection connection in state.Connections.Span)
            {
                int from = FindCell(surface, supports, columns, connection.From);
                int to = FindCell(surface, supports, columns, connection.To);
                if (from < 0 || to < 0) continue;
                float cost = connection.Cost == 0 ? Vector3.Distance(connection.From, connection.To) : connection.Cost;
                edges[from].Add(new(to, cost, connection.RequiredCapabilities, connection.Action));
                if (connection.Bidirectional)
                    edges[to].Add(new(from, cost, connection.RequiredCapabilities, connection.Action));
            }
            var cells = new NavigationGridCell[supports.Length];
            var edgeStarts = new int[cells.Length + 1];
            for (int cell = 0; cell < cells.Length; cell++)
                edgeStarts[cell + 1] = edgeStarts[cell] + edges[cell].Count;
            var transitions = new NavigationGridEdge[edgeStarts[^1]];
            JobSystem.ExecuteParallel(new WriteCells(supports, edges, edgeStarts, cells, transitions), cells.Length,
                batchSize: JobScheduleOptions.AutomaticBatchSize);
            state.Grid = new(surface.Bounds, cellSize, cells, transitions,
                NavigationGridShape.FromProfile(profile, state.FootprintMode), state.ContentVersion);
        }
    }

    private readonly struct CollectSupports(NavigationSurface surface, NavigationProfile profile,
        NavigationPolicy policy, NavigationCellOffset[] footprint, List<CellSupport>[] output) : IJobParallelFor
    {
        public void Execute(int index)
        {
            NavigationSurfaceTile tile = surface.Tiles.Span[index];
            float cellSize = surface.Settings.CellSize;
            var supports = new List<CellSupport>();
            for (int z = 0; z < tile.Depth; z++)
            for (int x = 0; x < tile.Width; x++)
            {
                Vector2 center = tile.Origin + new Vector2(x + 0.5f, z + 0.5f) * cellSize;
                if (!tile.InteriorBounds.Contains(center)) continue;
                ReadOnlySpan<NavigationSurfaceSpan> spans = tile.GetColumn(x, z);
                for (int layer = 0; layer < spans.Length; layer++)
                {
                    NavigationSurfaceSpan span = spans[layer];
                    float ceiling = layer + 1 < spans.Length ? spans[layer + 1].MinimumHeight : float.PositiveInfinity;
                    if (!NavigationSurfaceTraversal.IsWalkable(in span, ceiling, profile, policy) ||
                        !FitsFootprint(surface, center, in span, ceiling, profile, policy, footprint)) continue;
                    supports.Add(new(new(center.X, span.Height, center.Y), span, ceiling, layer, tile.Coordinate));
                }
            }
            output[index] = supports;
        }
    }

    private readonly struct WriteSupports(List<CellSupport>[] tiles, int[] starts, CellSupport[] supports) : IJobParallelFor
    {
        public void Execute(int index) => tiles[index].CopyTo(supports, starts[index]);
    }

    private readonly struct ConnectCells(NavigationSurface surface, NavigationProfile profile, CellSupport[] supports,
        Dictionary<NavigationCellOffset, List<int>> columns, List<NavigationGridEdge>[] edges) : IJobParallelFor
    {
        public void Execute(int cell)
        {
            var connections = new List<NavigationGridEdge>();
            CellSupport from = supports[cell];
            NavigationCellOffset coordinate = Coordinate(surface, from.Position);
            ReadOnlySpan<NavigationCellOffset> neighbors = [new(-1, 0), new(1, 0), new(0, -1), new(0, 1)];
            foreach (NavigationCellOffset offset in neighbors)
            {
                if (!columns.TryGetValue(new(coordinate.X + offset.X, coordinate.Z + offset.Z), out List<int>? column)) continue;
                foreach (int toCell in column)
                {
                    CellSupport to = supports[toCell];
                    if (NavigationSurfaceTraversal.CanTraverse(from.Span, from.Ceiling, to.Span, to.Ceiling, profile))
                        connections.Add(new(toCell, Vector3.Distance(from.Position, to.Position)));
                }
            }
            edges[cell] = connections;
        }
    }

    private readonly struct WriteCells(CellSupport[] supports, List<NavigationGridEdge>[] edges, int[] starts,
        NavigationGridCell[] cells, NavigationGridEdge[] transitions) : IJobParallelFor
    {
        public void Execute(int cell)
        {
            CellSupport support = supports[cell];
            cells[cell] = new(support.Position, starts[cell], edges[cell].Count,
                support.Span.Area, Layer: support.Layer, CeilingHeight: support.Ceiling, Tile: support.Tile);
            edges[cell].CopyTo(transitions, starts[cell]);
        }
    }

    private static bool FitsFootprint(NavigationSurface surface, Vector2 center, in NavigationSurfaceSpan span,
        float ceiling, NavigationProfile profile, NavigationPolicy policy, ReadOnlySpan<NavigationCellOffset> footprint)
    {
        foreach (NavigationCellOffset offset in footprint)
        {
            Vector2 point = center + new Vector2(offset.X, offset.Z) * surface.Settings.CellSize;
            ReadOnlySpan<NavigationSurfaceSpan> neighbors = surface.GetColumn(point);
            bool supported = false;
            for (int index = 0; index < neighbors.Length; index++)
            {
                NavigationSurfaceSpan neighbor = neighbors[index];
                float top = index + 1 < neighbors.Length ? neighbors[index + 1].MinimumHeight : float.PositiveInfinity;
                if (NavigationSurfaceTraversal.IsWalkable(in neighbor, top, profile, policy) &&
                    NavigationSurfaceTraversal.CanTraverse(in span, ceiling, in neighbor, top, profile))
                {
                    supported = true;
                    break;
                }
            }
            if (!supported) return false;
        }
        return true;
    }

    private static NavigationCellOffset Coordinate(NavigationSurface surface, Vector3 position) => new(
        (int)MathF.Floor((position.X - surface.Bounds.Minimum.X) / surface.Settings.CellSize),
        (int)MathF.Floor((position.Z - surface.Bounds.Minimum.Z) / surface.Settings.CellSize));

    private static int FindCell(NavigationSurface surface, CellSupport[] supports,
        Dictionary<NavigationCellOffset, List<int>> columns, Vector3 position)
    {
        if (!columns.TryGetValue(Coordinate(surface, position), out List<int>? column)) return -1;
        int closest = -1;
        float distance = float.PositiveInfinity;
        foreach (int cell in column)
        {
            float vertical = MathF.Abs(supports[cell].Position.Y - position.Y);
            if (vertical >= distance) continue;
            closest = cell;
            distance = vertical;
        }
        return closest;
    }
}
