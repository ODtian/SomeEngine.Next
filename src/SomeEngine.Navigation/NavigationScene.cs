using System.Numerics;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;

namespace SomeEngine.Navigation;

public readonly record struct NavigationRegionPlacement(int Id, NavigationMesh Mesh, Matrix4x4 Transform);

/// <summary>A placed entrance graph; positions and costs in Graph are in world coordinates.</summary>
public sealed record NavigationSceneRegion(int Id, Matrix4x4 Transform, NavigationRegionGraph Graph);

public readonly record struct NavigationSceneTileId(int Region, NavigationTileCoordinate Tile);

/// <summary>One placed detailed tile. Ordinary references retain its arrays independently of residence.</summary>
public sealed record NavigationSceneTile(int Region, NavigationTileCoordinate Tile, NavigationMesh Mesh)
{
    public NavigationSceneTileId Id => new(Region, Tile);
}

public readonly record struct NavigationSceneConnection(int Id, int FromRegion, int FromPort, int ToRegion, int ToPort,
    float Cost = 0, ulong RequiredCapabilities = 0, int Action = 0, bool Bidirectional = true, ulong ContentVersion = 1);

/// <summary>One complete set of placed graphs, connections and resident detailed references.</summary>
public sealed class NavigationSceneContent
{
    private readonly Dictionary<int, int> _regionIndices;
    private readonly Dictionary<NavigationSceneTileId, int> _tileIndices;
    private readonly int[] _polygonStarts;

    public NavigationSceneContent(ReadOnlyMemory<NavigationSceneRegion> regions,
        ReadOnlyMemory<NavigationSceneConnection> connections, ReadOnlyMemory<NavigationSceneTile> tiles, long version = 1,
        long contentVersion = 1, long residenceVersion = 1)
    {
        Regions = regions;
        Connections = connections;
        Tiles = tiles;
        Version = version;
        ContentVersion = contentVersion;
        ResidenceVersion = residenceVersion;
        _regionIndices = new Dictionary<int, int>();
        _tileIndices = new Dictionary<NavigationSceneTileId, int>();
        _polygonStarts = new int[tiles.Length + 1];
        for (int index = 0; index < regions.Length; index++) _regionIndices.Add(regions.Span[index].Id, index);
        for (int index = 0; index < tiles.Length; index++)
        {
            _tileIndices.Add(tiles.Span[index].Id, index);
            _polygonStarts[index + 1] = _polygonStarts[index] + tiles.Span[index].Mesh.Polygons.Length;
        }
    }

    public ReadOnlyMemory<NavigationSceneRegion> Regions { get; }
    public ReadOnlyMemory<NavigationSceneConnection> Connections { get; }
    public ReadOnlyMemory<NavigationSceneTile> Tiles { get; }
    public long Version { get; }
    public long ContentVersion { get; }
    public long ResidenceVersion { get; }
    internal ReadOnlyMemory<int> PolygonStarts => _polygonStarts;
    internal int GetPolygonId(NavigationSceneTileId tile, int polygon) => _polygonStarts[_tileIndices[tile]] + polygon;
    internal NavigationSceneTile GetPolygon(int id, out int polygon)
    {
        int lower = 0, upper = _polygonStarts.Length;
        while (lower < upper)
        {
            int middle = (lower + upper) / 2;
            if (_polygonStarts[middle] <= id) lower = middle + 1;
            else upper = middle;
        }
        int index = lower - 1;
        polygon = id - _polygonStarts[index];
        return Tiles.Span[index];
    }

    public NavigationSceneRegion GetRegion(int region) => Regions.Span[_regionIndices[region]];
    public NavigationSceneTile GetTile(NavigationSceneTileId tile) => Tiles.Span[_tileIndices[tile]];

    public bool TryGetTile(NavigationSceneTileId tile, out NavigationSceneTile? data)
    {
        data = _tileIndices.TryGetValue(tile, out int index) ? Tiles.Span[index] : null;
        return data is not null;
    }

    public NavigationSceneTile? FindTile(Vector3 position, out Vector3 surfacePoint)
    {
        surfacePoint = position;
        NavigationSceneTile? closest = null;
        float distance = float.PositiveInfinity;
        foreach (NavigationSceneTile tile in Tiles.Span)
        {
            if (!NavigationMesh.TryGetHeight(tile.Mesh, position, out float height)) continue;
            float vertical = MathF.Abs(height - position.Y);
            if (vertical >= distance) continue;
            closest = tile;
            distance = vertical;
            surfacePoint = new(position.X, height, position.Z);
        }
        return closest;
    }

    public NavigationSceneTileId? FindTileInDirectory(Vector3 position)
    {
        NavigationSceneTileId? result = null;
        float closest = float.PositiveInfinity;
        foreach (NavigationSceneRegion region in Regions.Span)
        foreach (NavigationRegionTile tile in region.Graph.Tiles.Span)
        {
            NavigationBounds bounds = tile.Bounds;
            if (position.X < bounds.Minimum.X || position.X > bounds.Maximum.X ||
                position.Z < bounds.Minimum.Z || position.Z > bounds.Maximum.Z) continue;
            float vertical = MathF.Abs(position.Y - Math.Clamp(position.Y, bounds.Minimum.Y, bounds.Maximum.Y));
            if (vertical >= closest) continue;
            closest = vertical;
            result = new(region.Id, tile.Tile);
        }
        return result;
    }
}

/// <summary>Holds the current complete region and residence data used by scene navigation.</summary>
public sealed partial class NavigationScene(NavigationSceneContent content) : INavigationSpace<NavigationScene>
{
    private NavigationSceneContent _content = content;
    public NavigationSceneContent Content => Volatile.Read(ref _content);

    public JobHandle ScheduleApply(NavigationSceneContent content, JobHandle dependency = default, Scheduler? scheduler = null)
    {
        scheduler ??= JobSystem.Default;
        return scheduler.Schedule(new ApplyContent(this, content),
            [JobResourceAccess.Write(scheduler.GetResourceToken(this)), NavigationJobAccess.Read(scheduler, content.Regions),
             NavigationJobAccess.Read(scheduler, content.Connections), NavigationJobAccess.Read(scheduler, content.Tiles)], dependency);
    }

    public static long GetVersion(NavigationScene scene) => scene.Content.Version;

    internal void ApplyCompleted(NavigationSceneContent content, bool residenceOnly = false)
    {
        NavigationSceneContent previous = Content;
        Volatile.Write(ref _content, new(content.Regions, content.Connections, content.Tiles, previous.Version + 1,
            previous.ContentVersion + (residenceOnly ? 0 : 1), previous.ResidenceVersion + (residenceOnly ? 1 : 0)));
    }

    public static void CollectResources(NavigationScene scene, ref JobResourceAccessBuilder resources)
    {
        resources.Read(scene);
        NavigationSceneContent current = scene.Content;
        resources.Read(current.Regions);
        resources.Read(current.Connections);
        resources.Read(current.Tiles);
        resources.Read(current.PolygonStarts);
        foreach (NavigationSceneRegion region in current.Regions.Span)
            NavigationRegionGraph.CollectResources(region.Graph, ref resources);
        foreach (NavigationSceneTile tile in current.Tiles.Span)
            NavigationMesh.CollectResources(tile.Mesh, ref resources);
    }

    public static bool TryGetHeight(NavigationScene scene, Vector3 position, out float height)
    {
        NavigationSceneTile? tile = scene.Content.FindTile(position, out Vector3 point);
        height = point.Y;
        return tile is not null;
    }

    private readonly struct ApplyContent(NavigationScene scene, NavigationSceneContent content) : IJob
    {
        public void Execute() => scene.ApplyCompleted(content);
    }
}
