using System.Numerics;
using SomeEngine.ECS.Systems;

namespace SomeEngine.Navigation;

/// <summary>An external connection point supplied when building a mesh's entrance graph.</summary>
public readonly record struct NavigationRegionPort(int Id, NavigationTileCoordinate Tile, Vector3 Position);

/// <summary>Small tile metadata retained with the entrance graph.</summary>
public readonly record struct NavigationRegionTile(NavigationTileCoordinate Tile, NavigationBounds Bounds,
    ulong ContentVersion, int EntranceStart, int EntranceCount);

/// <summary>One side of a tile crossing or an explicitly named region port.</summary>
public readonly record struct NavigationRegionEntrance(NavigationTileCoordinate Tile, int Polygon,
    Vector3 Position, Vector3 Left, Vector3 Right, int EdgeStart = 0, int EdgeCount = 0, int Port = -1);

/// <summary>An actual local route or directed boundary crossing, with its accumulated traversal cost.</summary>
public readonly record struct NavigationRegionEdge(int From, int To, float Cost,
    ulong RequiredCapabilities = 0, int Action = 0);

/// <summary>Completed entrance topology and costs, held independently of detailed tile arrays.</summary>
public sealed class NavigationRegionGraph(ReadOnlyMemory<NavigationRegionTile> tiles,
    ReadOnlyMemory<NavigationRegionEntrance> entrances, ReadOnlyMemory<NavigationRegionEdge> edges,
    ReadOnlyMemory<NavigationRegionPort> ports, ulong contentVersion)
{
    public ReadOnlyMemory<NavigationRegionTile> Tiles { get; } = tiles;
    public ReadOnlyMemory<NavigationRegionEntrance> Entrances { get; } = entrances;
    public ReadOnlyMemory<NavigationRegionEdge> Edges { get; } = edges;
    public ReadOnlyMemory<NavigationRegionPort> Ports { get; } = ports;
    public ulong ContentVersion { get; } = contentVersion;

    public int FindPort(int id)
    {
        for (int index = 0; index < Entrances.Length; index++)
            if (Entrances.Span[index].Port == id) return index;
        return -1;
    }

    public static void CollectResources(NavigationRegionGraph graph, ref JobResourceAccessBuilder resources)
    {
        resources.Read(graph.Tiles);
        resources.Read(graph.Entrances);
        resources.Read(graph.Edges);
        if (!graph.Ports.IsEmpty) resources.Read(graph.Ports);
    }
}

/// <summary>One independently held detailed tile in mesh-local coordinates.</summary>
public sealed record NavigationMeshTile(NavigationTileCoordinate Tile, NavigationMesh Mesh, NavigationBounds? Bounds = null);

public readonly record struct NavigationRouteEntrance(int Region, NavigationTileCoordinate Tile,
    int Entrance, Vector3 Position);

public sealed class NavigationRegionRoute(ReadOnlyMemory<NavigationRouteEntrance> entrances, float cost)
{
    public ReadOnlyMemory<NavigationRouteEntrance> Entrances { get; } = entrances;
    public float Cost { get; } = cost;
}

/// <summary>Identifies the detailed mesh that gives a range of local polygon indices its meaning.</summary>
public readonly record struct NavigationPathSection(int Region, NavigationTileCoordinate Tile,
    NavigationMesh Mesh, int CorridorStart, int CorridorCount);

public readonly record struct NavigationConnectionDependency(int Connection, ulong ContentVersion);
