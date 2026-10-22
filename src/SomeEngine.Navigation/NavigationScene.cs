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

    public NavigationSceneContent(ReadOnlyMemory<NavigationSceneRegion> regions,
        ReadOnlyMemory<NavigationSceneConnection> connections, ReadOnlyMemory<NavigationSceneTile> tiles, long version = 1)
    {
        Regions = regions;
        Connections = connections;
        Tiles = tiles;
        Version = version;
        _regionIndices = new Dictionary<int, int>();
        _tileIndices = new Dictionary<NavigationSceneTileId, int>();
        for (int index = 0; index < regions.Length; index++) _regionIndices.Add(regions.Span[index].Id, index);
        for (int index = 0; index < tiles.Length; index++) _tileIndices.Add(tiles.Span[index].Id, index);
    }

    public ReadOnlyMemory<NavigationSceneRegion> Regions { get; }
    public ReadOnlyMemory<NavigationSceneConnection> Connections { get; }
    public ReadOnlyMemory<NavigationSceneTile> Tiles { get; }
    public long Version { get; }

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
}

/// <summary>Holds the current complete region and residence data used by scene navigation.</summary>
public sealed class NavigationScene(NavigationSceneContent content) : INavigationSpace<NavigationScene>
{
    private NavigationSceneContent _content = content;
    public NavigationSceneContent Content => Volatile.Read(ref _content);

    public JobHandle ScheduleApply(NavigationSceneContent content, JobHandle dependency = default, Scheduler? scheduler = null)
    {
        scheduler ??= JobSystem.Default;
        return scheduler.Schedule(new ApplyContent(this, content),
            [JobResourceAccess.Write(scheduler.GetResourceToken(this))], dependency);
    }

    public static long GetVersion(NavigationScene scene) => scene.Content.Version;

    public static void CollectResources(NavigationScene scene, ref JobResourceAccessBuilder resources)
    {
        resources.Read(scene);
        NavigationSceneContent current = scene.Content;
        resources.Read(current.Regions);
        resources.Read(current.Connections);
        resources.Read(current.Tiles);
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
        public void Execute() => Volatile.Write(ref scene._content,
            new(content.Regions, content.Connections, content.Tiles, scene.Content.Version + 1));
    }
}
