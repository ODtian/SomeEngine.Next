using System.Numerics;

namespace SomeEngine.Navigation;

public readonly record struct NavigationTileCoordinate(int X, int Z);
public readonly record struct NavigationSurfaceColumn(int Start, int Count);
public readonly record struct NavigationSurfaceSpan(
    float MinimumHeight, float MaximumHeight, float Height, Vector3 Normal, int Area);
public readonly record struct NavigationSupport(Vector3 Position, Vector3 Normal, int Area, float CeilingHeight);

/// <summary>A completed tile, including border columns used by contour and adjacency builders.</summary>
public sealed class NavigationSurfaceTile(
    NavigationTileCoordinate coordinate,
    NavigationBounds interiorBounds,
    Vector2 origin,
    float cellSize,
    int width,
    int depth,
    ReadOnlyMemory<NavigationSurfaceColumn> columns,
    ReadOnlyMemory<NavigationSurfaceSpan> spans)
{
    public NavigationTileCoordinate Coordinate { get; } = coordinate;
    public NavigationBounds InteriorBounds { get; } = interiorBounds;
    public Vector2 Origin { get; } = origin;
    public float CellSize { get; } = cellSize;
    public int Width { get; } = width;
    public int Depth { get; } = depth;
    public ReadOnlyMemory<NavigationSurfaceColumn> Columns { get; } = columns;
    public ReadOnlyMemory<NavigationSurfaceSpan> Spans { get; } = spans;

    public ReadOnlySpan<NavigationSurfaceSpan> GetColumn(int x, int z)
    {
        NavigationSurfaceColumn column = Columns.Span[z * Width + x];
        return Spans.Span.Slice(column.Start, column.Count);
    }

    public ReadOnlySpan<NavigationSurfaceSpan> GetColumn(Vector2 position)
    {
        int x = (int)MathF.Floor((position.X - Origin.X) / CellSize);
        int z = (int)MathF.Floor((position.Y - Origin.Y) / CellSize);
        return x >= 0 && z >= 0 && x < Width && z < Depth ? GetColumn(x, z) : [];
    }
}

/// <summary>Shared multi-layer geometry, independent of the profiles that derive walkable space.</summary>
public sealed class NavigationSurface(
    NavigationBounds bounds,
    NavigationSurfaceSettings settings,
    ReadOnlyMemory<NavigationSurfaceTile> tiles)
{
    public NavigationBounds Bounds { get; } = bounds;
    public NavigationSurfaceSettings Settings { get; } = settings;
    public ReadOnlyMemory<NavigationSurfaceTile> Tiles { get; } = tiles;
    private readonly Dictionary<NavigationTileCoordinate, NavigationSurfaceTile> _tiles =
        tiles.Span.ToArray().ToDictionary(tile => tile.Coordinate);

    public ReadOnlySpan<NavigationSurfaceSpan> GetColumn(Vector2 position)
    {
        if (!Bounds.Contains(position)) return [];
        float tileWidth = Settings.TileSize * Settings.CellSize;
        var coordinate = new NavigationTileCoordinate(
            (int)MathF.Floor((position.X - Bounds.Minimum.X) / tileWidth),
            (int)MathF.Floor((position.Y - Bounds.Minimum.Z) / tileWidth));
        return _tiles.TryGetValue(coordinate, out var tile) ? tile.GetColumn(position) : [];
    }

    public bool TryGetSupport(Vector3 position, float maximumVerticalDistance, out NavigationSupport support)
    {
        ReadOnlySpan<NavigationSurfaceSpan> column = GetColumn(new(position.X, position.Z));
        float closest = maximumVerticalDistance;
        bool found = false;
        support = default;
        for (int index = 0; index < column.Length; index++)
        {
            NavigationSurfaceSpan span = column[index];
            float distance = MathF.Abs(position.Y - span.Height);
            if (span.Normal.Y <= 0 || distance > closest) continue;
            float ceiling = index + 1 < column.Length ? column[index + 1].MinimumHeight : float.PositiveInfinity;
            support = new(new(position.X, span.Height, position.Z), span.Normal, span.Area, ceiling);
            closest = distance;
            found = true;
        }
        return found;
    }
}
