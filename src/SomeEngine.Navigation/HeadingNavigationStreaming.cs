using System.Numerics;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;

namespace SomeEngine.Navigation;

/// <summary>Application-owned demand, async acquisition and completed-tile publication for heading navigation.</summary>
public sealed class HeadingNavigationStreaming(HeadingNavigationScene scene, NavigationTileSource<HeadingNavigationMesh> source,
    Scheduler? scheduler = null) : IDisposable
{
    private readonly Scheduler _scheduler = scheduler ?? JobSystem.Default;
    private readonly NavigationTileSource<HeadingNavigationMesh> _source = source.Retain();
    private readonly Dictionary<NavigationTileCoordinate, NavigationTileRead<HeadingNavigationMesh>> _reads = [];
    public IList<NavigationInterest> Interests { get; } = new List<NavigationInterest>();
    public int ResidentTileBudget { get; set; } = int.MaxValue;
    public JobHandle Applied { get; private set; }
    public IReadOnlyList<NavigationStreamingRead> Reads => _reads.Select(pair => new NavigationStreamingRead(new(0, pair.Key), pair.Value.Completion)).ToArray();

    public void Update()
    {
        if (!Applied.IsCompleted) return;
        Applied.Complete();
        HeadingNavigationSceneContent current = scene.Content;
        var demanded = new HashSet<NavigationTileCoordinate>();
        foreach (NavigationInterest interest in Interests)
        {
            foreach (NavigationRegionTile tile in current.Graph.Tiles.Span)
                if (Distance(interest.Position, tile.Bounds) <= interest.Radius ||
                    Distance(interest.Position + interest.Direction * interest.LookAheadDistance, tile.Bounds) <= interest.Radius)
                    demanded.Add(tile.Tile);
            if (interest.Path is not { } path) continue;
            foreach (NavigationSceneTileId required in path.RequiredTiles.Span) demanded.Add(required.Tile);
            if (path.RegionRoute is not { } route) continue;
            int closest = 0;
            float distance = float.PositiveInfinity;
            for (int index = 0; index < route.Entrances.Length; index++)
            {
                float candidate = Vector3.DistanceSquared(interest.Position, route.Entrances.Span[index].Position);
                if (candidate <= distance) { closest = index; distance = candidate; }
            }
            Vector3 previous = interest.Position;
            float ahead = 0;
            for (int index = closest; index < route.Entrances.Length; index++)
            {
                NavigationRouteEntrance entrance = route.Entrances.Span[index];
                ahead += Vector3.Distance(previous, entrance.Position);
                if (ahead > interest.LookAheadDistance) break;
                demanded.Add(entrance.Tile);
                previous = entrance.Position;
            }
        }
        NavigationRegionTile[] directory = current.Graph.Tiles.ToArray();
        float DemandDistance(NavigationTileCoordinate tile) => Interests.Count == 0 ? 0 :
            Interests.Min(interest => Distance(interest.Position, directory.First(description => description.Tile == tile).Bounds));
        var selected = demanded.OrderBy(DemandDistance).Take(ResidentTileBudget).ToHashSet();
        foreach (NavigationTileCoordinate tile in selected)
            if (!current.TryGetTile(tile, out _) && !_reads.ContainsKey(tile)) _reads.Add(tile, _source.ScheduleRead(tile, _scheduler));
        var ready = new List<HeadingNavigationMeshTile>();
        foreach (var pair in _reads.ToArray())
        {
            if (!pair.Value.Completion.IsCompleted) continue;
            HeadingNavigationMesh mesh = pair.Value.Complete();
            ready.Add(new(pair.Key, mesh, directory.First(tile => tile.Tile == pair.Key).Bounds));
            _reads.Remove(pair.Key);
        }
        var keep = current.Tiles.ToArray().Concat(ready).Select(tile => tile.Tile).Distinct()
            .OrderByDescending(selected.Contains).ThenBy(DemandDistance).Take(ResidentTileBudget).ToHashSet();
        NavigationTileCoordinate[] removed = current.Tiles.ToArray().Where(tile => !keep.Contains(tile.Tile)).Select(tile => tile.Tile).ToArray();
        ready.RemoveAll(tile => !keep.Contains(tile.Tile));
        if (ready.Count == 0 && removed.Length == 0) return;
        var resources = new List<JobResourceAccess> { JobResourceAccess.Write(_scheduler.GetResourceToken(scene)), NavigationJobAccess.Read(_scheduler, current.Tiles) };
        foreach (HeadingNavigationMeshTile tile in ready) tile.Mesh.AddReadAccesses(_scheduler, resources);
        Applied = _scheduler.Schedule(new ApplyTiles(scene, ready.ToArray(), removed), resources.ToArray());
    }

    private static float Distance(Vector3 point, NavigationBounds bounds) => Vector3.Distance(point, Vector3.Clamp(point, bounds.Minimum, bounds.Maximum));
    public void Dispose()
    {
        foreach (NavigationTileRead<HeadingNavigationMesh> read in _reads.Values) read.Completion.Complete();
        Applied.Complete(); _source.Dispose(); _reads.Clear();
    }

    private readonly struct ApplyTiles(HeadingNavigationScene scene, HeadingNavigationMeshTile[] loaded, NavigationTileCoordinate[] removed) : IJob
    {
        public void Execute()
        {
            var tiles = scene.Content.Tiles.ToArray().ToDictionary(tile => tile.Tile);
            foreach (NavigationTileCoordinate tile in removed) tiles.Remove(tile);
            foreach (HeadingNavigationMeshTile tile in loaded) tiles[tile.Tile] = tile;
            scene.Apply(tiles.Values.ToArray());
        }
    }
}

public readonly struct HeadingNavigationStreamingSystem(HeadingNavigationStreaming streaming) : ISystem<ImmediateSystemContext>
{
    public void OnUpdate(ref ImmediateSystemContext context) => streaming.Update();
}
