using System.Numerics;
using SomeEngine.ECS.Systems;

namespace SomeEngine.Navigation;

/// <summary>A support layer in one XZ cell. CeilingHeight - Position.Y is its geometric headroom.</summary>
public readonly record struct NavigationGridCell(
    Vector3 Position, int EdgeStart, int EdgeCount, int Area = 0, float BaseCost = 1,
    int Layer = 0, float CeilingHeight = float.PositiveInfinity, NavigationTileCoordinate Tile = default);

/// <summary>A directed forward transition. Cost is its base traversal cost before area and action policy.</summary>
public readonly record struct NavigationGridEdge(int ToCell, float Cost, ulong RequiredCapabilities = 0, int Action = 0);

public readonly record struct NavigationGridIncoming(int FromCell, int Edge);
public readonly record struct NavigationGridRange(int Start, int Count);
public enum NavigationGridFootprintMode : byte { AllHeadings, HeadingZero, Point }

/// <summary>The profile shape and traversal limits already applied while deriving this grid.</summary>
public readonly record struct NavigationGridShape(
    ReadOnlyMemory<Vector2> Footprint, float Height, float MaximumSlopeRadians, float MaximumStepHeight,
    NavigationGridFootprintMode FootprintMode = NavigationGridFootprintMode.AllHeadings)
{
    public static NavigationGridShape FromProfile(NavigationProfile profile,
        NavigationGridFootprintMode footprintMode = NavigationGridFootprintMode.AllHeadings) =>
        new(profile.Footprint, profile.Height, profile.MaximumSlopeRadians, profile.MaximumStepHeight, footprintMode);
}

/// <summary>Completed multi-layer cells and explicit forward and incoming traversal graphs.</summary>
public sealed partial class NavigationGrid : INavigationSpace<NavigationGrid>
{
    private readonly Dictionary<NavigationCellOffset, int[]> _columns;

    public NavigationGrid(NavigationBounds bounds, float cellSize,
        NavigationGridCell[] cells, NavigationGridEdge[] edges, NavigationGridShape shape = default, ulong contentVersion = 1)
        : this(bounds, cellSize, (ReadOnlyMemory<NavigationGridCell>)cells, edges, shape, contentVersion) { }

    public NavigationGrid(NavigationBounds bounds, float cellSize,
        ReadOnlyMemory<NavigationGridCell> cells, ReadOnlyMemory<NavigationGridEdge> edges,
        NavigationGridShape shape = default, ulong contentVersion = 1)
    {
        Bounds = bounds;
        CellSize = cellSize;
        Cells = cells;
        Edges = edges;
        Shape = shape;
        ContentVersion = contentVersion;
        var columns = new Dictionary<NavigationCellOffset, List<int>>();
        var counts = new int[cells.Length];
        for (int cell = 0; cell < cells.Length; cell++)
        {
            NavigationCellOffset coordinate = GetCoordinate(cells.Span[cell].Position);
            if (!columns.TryGetValue(coordinate, out List<int>? column))
                columns.Add(coordinate, column = []);
            column.Add(cell);
            NavigationGridCell from = cells.Span[cell];
            foreach (NavigationGridEdge edge in edges.Span.Slice(from.EdgeStart, from.EdgeCount))
                counts[edge.ToCell]++;
        }
        _columns = columns.ToDictionary(pair => pair.Key, pair => pair.Value.ToArray());
        var ranges = new NavigationGridRange[cells.Length];
        int count = 0;
        for (int cell = 0; cell < ranges.Length; cell++)
        {
            ranges[cell] = new(count, counts[cell]);
            count += counts[cell];
            counts[cell] = 0;
        }
        var incoming = new NavigationGridIncoming[count];
        for (int cell = 0; cell < cells.Length; cell++)
        {
            NavigationGridCell from = cells.Span[cell];
            for (int edge = from.EdgeStart; edge < from.EdgeStart + from.EdgeCount; edge++)
            {
                int to = edges.Span[edge].ToCell;
                incoming[ranges[to].Start + counts[to]++] = new(cell, edge);
            }
        }
        IncomingRanges = ranges;
        IncomingEdges = incoming;
    }

    public NavigationBounds Bounds { get; }
    public float CellSize { get; }
    public NavigationGridShape Shape { get; }
    public ulong ContentVersion { get; }
    public ReadOnlyMemory<NavigationGridCell> Cells { get; }
    public ReadOnlyMemory<NavigationGridEdge> Edges { get; }
    public ReadOnlyMemory<NavigationGridRange> IncomingRanges { get; }
    public ReadOnlyMemory<NavigationGridIncoming> IncomingEdges { get; }

    public static void CollectResources(NavigationGrid space, ref JobResourceAccessBuilder resources)
    {
        resources.Read(space.Cells);
        resources.Read(space.Edges);
        resources.Read(space.IncomingRanges);
        resources.Read(space.IncomingEdges);
        if (!space.Shape.Footprint.IsEmpty) resources.Read(space.Shape.Footprint);
    }

    public static bool TryGetHeight(NavigationGrid space, Vector3 position, out float height)
    {
        int cell = space.FindCell(position);
        height = cell >= 0 ? space.Cells.Span[cell].Position.Y : position.Y;
        return cell >= 0;
    }

    public NavigationCellOffset GetCoordinate(Vector3 position) => new(
        (int)MathF.Floor((position.X - Bounds.Minimum.X) / CellSize),
        (int)MathF.Floor((position.Z - Bounds.Minimum.Z) / CellSize));

    public ReadOnlySpan<int> GetColumn(NavigationCellOffset coordinate) =>
        _columns.TryGetValue(coordinate, out int[]? cells) ? cells : [];

    public int FindCell(Vector3 position)
    {
        int closest = -1;
        float verticalDistance = float.PositiveInfinity;
        foreach (int cell in GetColumn(GetCoordinate(position)))
        {
            float distance = MathF.Abs(Cells.Span[cell].Position.Y - position.Y);
            if (distance >= verticalDistance) continue;
            closest = cell;
            verticalDistance = distance;
        }
        return closest;
    }

    /// <summary>Returns the closest-height ordinary transition in the requested XZ axis direction.</summary>
    public int FindAxisNeighbor(int cell, int x, int z)
    {
        NavigationGridCell from = Cells.Span[cell];
        NavigationCellOffset coordinate = GetCoordinate(from.Position);
        int closest = -1;
        float distance = float.PositiveInfinity;
        foreach (NavigationGridEdge edge in Edges.Span.Slice(from.EdgeStart, from.EdgeCount))
        {
            NavigationGridCell to = Cells.Span[edge.ToCell];
            if (edge.Action != 0 || GetCoordinate(to.Position) != new NavigationCellOffset(coordinate.X + x, coordinate.Z + z))
                continue;
            float verticalDistance = MathF.Abs(to.Position.Y - from.Position.Y);
            if (verticalDistance >= distance) continue;
            closest = edge.ToCell;
            distance = verticalDistance;
        }
        return closest;
    }
}
