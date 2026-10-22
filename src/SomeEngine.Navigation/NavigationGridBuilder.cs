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
                NavigationGridFootprintMode.HeadingZero => NavigationSurfaceTraversal.RasterizeFootprint(profile.Footprint.Span, cellSize),
                _ => NavigationSurfaceTraversal.RasterizeFootprintSweep(profile.Footprint.Span, cellSize),
            };
            var supports = new List<CellSupport>();
            var columns = new Dictionary<NavigationCellOffset, List<int>>();
            foreach (NavigationSurfaceTile tile in surface.Tiles.Span)
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
                    Vector3 position = new(center.X, span.Height, center.Y);
                    NavigationCellOffset coordinate = Coordinate(surface, position);
                    if (!columns.TryGetValue(coordinate, out List<int>? column))
                        columns.Add(coordinate, column = []);
                    column.Add(supports.Count);
                    supports.Add(new(position, span, ceiling, layer, tile.Coordinate));
                }
            }
            var edges = new List<NavigationGridEdge>[supports.Count];
            ReadOnlySpan<NavigationCellOffset> neighbors = [new(-1, 0), new(1, 0), new(0, -1), new(0, 1)];
            for (int cell = 0; cell < supports.Count; cell++)
            {
                edges[cell] = [];
                CellSupport from = supports[cell];
                NavigationCellOffset coordinate = Coordinate(surface, from.Position);
                foreach (NavigationCellOffset offset in neighbors)
                {
                    if (!columns.TryGetValue(new(coordinate.X + offset.X, coordinate.Z + offset.Z), out List<int>? column)) continue;
                    foreach (int toCell in column)
                    {
                        CellSupport to = supports[toCell];
                        if (NavigationSurfaceTraversal.CanTraverse(from.Span, from.Ceiling, to.Span, to.Ceiling, profile))
                            edges[cell].Add(new(toCell, Vector3.Distance(from.Position, to.Position)));
                    }
                }
            }
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
            var cells = new NavigationGridCell[supports.Count];
            var transitions = new List<NavigationGridEdge>();
            for (int cell = 0; cell < cells.Length; cell++)
            {
                CellSupport support = supports[cell];
                cells[cell] = new(support.Position, transitions.Count, edges[cell].Count,
                    support.Span.Area, Layer: support.Layer, CeilingHeight: support.Ceiling, Tile: support.Tile);
                transitions.AddRange(edges[cell]);
            }
            state.Grid = new(surface.Bounds, cellSize, cells, transitions.ToArray(),
                NavigationGridShape.FromProfile(profile, state.FootprintMode), state.ContentVersion);
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

    private static int FindCell(NavigationSurface surface, List<CellSupport> supports,
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
