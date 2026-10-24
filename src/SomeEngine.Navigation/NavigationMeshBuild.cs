namespace SomeEngine.Navigation;

public sealed record NavigationMeshSettings
{
    public float MaximumContourError { get; init; } = 0.5f;
    public float MaximumEdgeLength { get; init; } = 12f;
    public int MaximumPolygonVertices { get; init; } = 6;
    public int MinimumRegionCells { get; init; }
    public int MergeRegionCells { get; init; } = 20;
    public float DetailSampleDistance { get; init; } = 0.4f;
    public float DetailMaximumError { get; init; } = 0.05f;
    public ulong ContentVersion { get; init; } = 1;
}

/// <summary>A tile's contiguous polygon range in the completed mesh; polygons own their detail and portal ranges.</summary>
public readonly record struct NavigationMeshTileRange(NavigationTileCoordinate Tile, int PolygonStart, int PolygonCount);

public sealed class NavigationMeshBuildState
{
    public NavigationSurface Surface { get; internal set; } = null!;
    public NavigationProfile Profile { get; }
    public NavigationPolicy Policy { get; }
    public NavigationMeshSettings Settings { get; }
    public NavigationMesh Mesh { get; internal set; } = null!;
    public ReadOnlyMemory<NavigationMeshTileRange> Tiles { get; internal set; }
    internal NavigationMeshTileData[] TileData { get; set; } = [];
    internal NavigationMeshBuilder.TileConnection[][] Connections { get; set; } = [];

    internal NavigationMeshBuildState(NavigationProfile profile, NavigationPolicy policy, NavigationMeshSettings settings)
        => (Profile, Policy, Settings) = (profile, policy, settings);
}

internal sealed record NavigationMeshTileData(NavigationMesh Mesh, NavigationMeshBorder[] Borders);
internal readonly record struct NavigationMeshBorder(int Polygon, System.Numerics.Vector3 Start, System.Numerics.Vector3 End);
