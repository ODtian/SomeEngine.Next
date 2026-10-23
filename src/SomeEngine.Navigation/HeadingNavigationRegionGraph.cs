using System.Numerics;
using SomeEngine.ECS.Systems;

namespace SomeEngine.Navigation;

public readonly record struct HeadingNavigationRegionEntrance(NavigationTileCoordinate Tile, int Polygon,
    Vector3 Position, int Channel, int EdgeStart = 0, int EdgeCount = 0);
public readonly record struct HeadingNavigationRegionEdge(int From, int To, float Cost);
public readonly record struct HeadingNavigationTilePortal(NavigationTileCoordinate FromTile, int FromPolygon,
    NavigationTileCoordinate ToTile, int ToPolygon, Vector3 Left, Vector3 Right, float Cost, ulong RequiredCapabilities, int Action);
public sealed record HeadingNavigationMeshTile(NavigationTileCoordinate Tile, HeadingNavigationMesh Mesh, NavigationBounds Bounds);

/// <summary>Tile entrances retain their heading channel and actual local action-graph costs.</summary>
public sealed class HeadingNavigationRegionGraph(ReadOnlyMemory<NavigationRegionTile> tiles,
    ReadOnlyMemory<HeadingNavigationRegionEntrance> entrances, ReadOnlyMemory<HeadingNavigationRegionEdge> edges,
    ReadOnlyMemory<HeadingNavigationTilePortal> portals, int channelCount, ulong contentVersion)
{
    public ReadOnlyMemory<NavigationRegionTile> Tiles { get; } = tiles;
    public ReadOnlyMemory<HeadingNavigationRegionEntrance> Entrances { get; } = entrances;
    public ReadOnlyMemory<HeadingNavigationRegionEdge> Edges { get; } = edges;
    public ReadOnlyMemory<HeadingNavigationTilePortal> Portals { get; } = portals;
    public int ChannelCount { get; } = channelCount;
    public ulong ContentVersion { get; } = contentVersion;

    public static void CollectResources(HeadingNavigationRegionGraph graph, ref JobResourceAccessBuilder resources)
    {
        resources.Read(graph.Tiles); resources.Read(graph.Entrances); resources.Read(graph.Edges); resources.Read(graph.Portals);
    }
}
