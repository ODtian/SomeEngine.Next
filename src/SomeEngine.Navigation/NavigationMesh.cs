using System.Numerics;
using SomeEngine.ECS.Systems;

namespace SomeEngine.Navigation;

/// <summary>A convex XZ polygon and its contiguous index, directed portal and detail ranges.</summary>
public readonly record struct NavigationPolygon(
    int VertexStart,
    int VertexCount,
    int PortalStart,
    int PortalCount,
    int Area = 0,
    ulong RequiredCapabilities = 0,
    int DetailStart = 0,
    int DetailCount = 0,
    NavigationTileCoordinate Tile = default);

/// <summary>
/// An outgoing portal in its source polygon's range. Left and Right follow travel direction:
/// in XZ, cross(toCenter - fromCenter, Left - midpoint) is positive. Reverse portals swap endpoints.
/// Cost is an additional traversal cost.
/// </summary>
public readonly record struct NavigationPortal(
    int ToPolygon,
    Vector3 Left,
    Vector3 Right,
    float Cost = 0,
    ulong RequiredCapabilities = 0,
    int Action = 0);

public readonly record struct NavigationDetailTriangle(int A, int B, int C);

/// <summary>Supplies the actual readonly data used by one strongly typed navigation representation.</summary>
public interface INavigationSpace<TSpace> where TSpace : class
{
    static abstract void CollectResources(TSpace space, ref JobResourceAccessBuilder resources);

    static virtual void CollectBoundaries(TSpace space, Vector3 position, float radius,
        NavigationProfile profile, NavigationPolicy policy, ref NavigationBoundaryBuilder builder) { }

    static virtual long GetVersion(TSpace space) => 0;

    static virtual bool TryGetHeight(TSpace space, Vector3 position, out float height)
    {
        height = position.Y;
        return false;
    }
}

/// <summary>Completed polygon and detail arrays, shared directly by paths, jobs and callers.</summary>
public sealed partial class NavigationMesh : INavigationSpace<NavigationMesh>
{
    public NavigationMesh(Vector3[] positions, int[] polygonVertices, NavigationPolygon[] polygons,
        NavigationPortal[] portals, Vector3[]? detailPositions = null,
        NavigationDetailTriangle[]? detailTriangles = null, ulong contentVersion = 1,
        NavigationRegionGraph? regionGraph = null, ReadOnlyMemory<NavigationMeshTile> tiles = default, float bakedRadius = 0)
        : this((ReadOnlyMemory<Vector3>)positions, polygonVertices, polygons, portals,
            detailPositions is null ? ReadOnlyMemory<Vector3>.Empty : detailPositions,
            detailTriangles is null ? ReadOnlyMemory<NavigationDetailTriangle>.Empty : detailTriangles,
            contentVersion, regionGraph, tiles, bakedRadius) { }

    public NavigationMesh(ReadOnlyMemory<Vector3> positions, ReadOnlyMemory<int> polygonVertices,
        ReadOnlyMemory<NavigationPolygon> polygons, ReadOnlyMemory<NavigationPortal> portals,
        ReadOnlyMemory<Vector3> detailPositions = default,
        ReadOnlyMemory<NavigationDetailTriangle> detailTriangles = default, ulong contentVersion = 1,
        NavigationRegionGraph? regionGraph = null, ReadOnlyMemory<NavigationMeshTile> tiles = default, float bakedRadius = 0)
    {
        Positions = positions;
        PolygonVertices = polygonVertices;
        Polygons = polygons;
        Portals = portals;
        DetailPositions = detailPositions;
        DetailTriangles = detailTriangles;
        ContentVersion = contentVersion;
        BakedRadius = bakedRadius;
        RegionGraph = regionGraph;
        Tiles = tiles;
        Vector3 minimum = new(float.PositiveInfinity), maximum = new(float.NegativeInfinity);
        foreach (Vector3 position in positions.Span)
        {
            minimum = Vector3.Min(minimum, position);
            maximum = Vector3.Max(maximum, position);
        }
        Bounds = new NavigationBounds(minimum, maximum);
    }

    public ReadOnlyMemory<Vector3> Positions { get; }
    public ReadOnlyMemory<int> PolygonVertices { get; }
    public ReadOnlyMemory<NavigationPolygon> Polygons { get; }
    public ReadOnlyMemory<NavigationPortal> Portals { get; }
    public ReadOnlyMemory<Vector3> DetailPositions { get; }
    public ReadOnlyMemory<NavigationDetailTriangle> DetailTriangles { get; }
    public ulong ContentVersion { get; }
    /// <summary>World-space disk radius already applied to this mesh's walkable boundaries.</summary>
    public float BakedRadius { get; }
    public NavigationBounds Bounds { get; }
    public NavigationRegionGraph? RegionGraph { get; }
    public ReadOnlyMemory<NavigationMeshTile> Tiles { get; }

    public static void CollectResources(NavigationMesh space, ref JobResourceAccessBuilder resources)
    {
        resources.Read(space.Positions);
        resources.Read(space.PolygonVertices);
        resources.Read(space.Polygons);
        resources.Read(space.Portals);
        if (!space.DetailPositions.IsEmpty)
            resources.Read(space.DetailPositions);
        if (!space.DetailTriangles.IsEmpty)
            resources.Read(space.DetailTriangles);
    }
}
