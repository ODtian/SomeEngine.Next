using System.Numerics;

namespace SomeEngine.Navigation;

public sealed record HeadingNavigationMeshSettings
{
    public int ChannelCount { get; init; } = 32;
    public int ChannelsPerJob { get; init; } = 4;
    public float MaximumSweepError { get; init; } = 0.02f;
    public int MaximumPolygonVertices { get; init; } = 6;
    public float DetailSampleDistance { get; init; } = 0.4f;
    public float DetailMaximumError { get; init; } = 0.05f;
    public ulong ContentVersion { get; init; } = 1;
}

public sealed class HeadingNavigationMeshBuildState
{
    public NavigationSurface Surface => Geometry.Surface;
    public NavigationProfile Profile => Geometry.Profile;
    public NavigationPolicy Policy => Geometry.Policy;
    public HeadingNavigationMeshSettings Settings { get; }
    public HeadingNavigationMesh Mesh { get; internal set; } = null!;
    public ReadOnlyMemory<NavigationMeshTileRange> Tiles => Geometry.Tiles;
    internal NavigationMeshBuildState Geometry { get; }
    internal NavigationRecastField[] Fields { get; set; } = [];
    internal NavigationCellOffset[][] Masks { get; set; } = [];
    internal bool[][] Feasibility { get; set; } = [];
    internal BigInteger[][] TileHeadings { get; set; } = [];

    internal HeadingNavigationMeshBuildState(NavigationProfile profile, NavigationPolicy policy, HeadingNavigationMeshSettings settings)
    {
        Settings = settings;
        Geometry = new(profile, policy, new()
        {
            MaximumContourError = 0,
            MinimumRegionCells = 0,
            MergeRegionCells = 0,
            MaximumPolygonVertices = settings.MaximumPolygonVertices,
            DetailSampleDistance = settings.DetailSampleDistance,
            DetailMaximumError = settings.DetailMaximumError,
            ContentVersion = settings.ContentVersion,
        });
    }
}
