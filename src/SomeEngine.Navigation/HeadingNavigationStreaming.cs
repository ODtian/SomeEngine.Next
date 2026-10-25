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
        NavigationInterestData[] interests = NavigationStreamingRanges.CaptureInterests(Interests);
        NavigationRegionTile[] directory = current.Graph.Tiles.ToArray();
        var nearby = NavigationStreamingRanges.CollectTiles(_scheduler, [directory], interests);
        var routes = new List<NavigationTileCoordinate>[interests.Length];
        _scheduler.ExecuteParallel(new CollectRoutes(interests, routes), interests.Length,
            JobScheduleOptions.AutomaticBatchSize, NavigationStreamingRanges.CollectPathReads(_scheduler, interests).ToArray());
        var demanded = new HashSet<NavigationTileCoordinate>();
        for (int item = 0; item < interests.Length; item++)
        {
            foreach (NavigationInterestTileRange range in nearby)
                foreach (NavigationTileCoordinate tile in range.TilesByInterest[item]) demanded.Add(tile);
            foreach (NavigationTileCoordinate tile in routes[item]) demanded.Add(tile);
        }
        NavigationTileCoordinate[] candidates = demanded.Concat(current.Tiles.ToArray().Select(tile => tile.Tile))
            .Concat(_reads.Keys).Distinct().ToArray();
        var distances = NavigationStreamingRanges.CalculateDistances(_scheduler, candidates, interests, new HeadingTileBounds(directory),
            emptyInterestDistance: 0);
        var selected = demanded.OrderBy(tile => distances[tile]).Take(ResidentTileBudget).ToHashSet();
        foreach (NavigationTileCoordinate tile in selected)
            if (!current.TryGetTile(tile, out _) && !_reads.ContainsKey(tile)) _reads.Add(tile, _source.ScheduleRead(tile, _scheduler));
        var ready = new List<HeadingNavigationMeshTile>();
        foreach (var pair in _reads.ToArray())
        {
            if (!pair.Value.Completion.IsCompleted) continue;
            _reads.Remove(pair.Key);
            HeadingNavigationMesh mesh = pair.Value.Complete();
            ready.Add(new(pair.Key, mesh, directory.First(tile => tile.Tile == pair.Key).Bounds));
        }
        var keep = current.Tiles.ToArray().Concat(ready).Select(tile => tile.Tile).Distinct()
            .OrderByDescending(selected.Contains).ThenBy(tile => distances[tile]).Take(ResidentTileBudget).ToHashSet();
        NavigationTileCoordinate[] removed = current.Tiles.ToArray().Where(tile => !keep.Contains(tile.Tile)).Select(tile => tile.Tile).ToArray();
        ready.RemoveAll(tile => !keep.Contains(tile.Tile));
        if (ready.Count == 0 && removed.Length == 0) return;
        var resources = new List<JobResourceAccess> { JobResourceAccess.Write(_scheduler.GetResourceToken(scene)), NavigationJobAccess.Read(_scheduler, current.Tiles) };
        foreach (HeadingNavigationMeshTile tile in ready) tile.Mesh.AddReadAccesses(_scheduler, resources);
        Applied = _scheduler.Schedule(new ApplyTiles(scene, ready.ToArray(), removed), resources.ToArray());
    }

    private readonly struct CollectRoutes(NavigationInterestData[] interests, List<NavigationTileCoordinate>[] output) : IJobParallelFor
    {
        public void Execute(int item)
        {
            NavigationInterestData interest = interests[item];
            List<NavigationTileCoordinate> demanded = output[item] = [];
            if (interest.Path is not { } path) return;
            foreach (NavigationSceneTileId required in path.RequiredTiles.Span) demanded.Add(required.Tile);
            if (path.RegionRoute is not { } route) return;
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
    }

    private readonly struct HeadingTileBounds(NavigationRegionTile[] directory) : INavigationTileBounds<NavigationTileCoordinate>
    {
        public NavigationBounds GetBounds(NavigationTileCoordinate tile) => directory.First(value => value.Tile == tile).Bounds;
        public void AddReadAccesses(Scheduler scheduler, List<JobResourceAccess> accesses) =>
            accesses.Add(NavigationJobAccess.Read(scheduler, (ReadOnlyMemory<NavigationRegionTile>)directory));
    }
    public void Dispose()
    {
        try { NavigationTileRead<HeadingNavigationMesh>.CompleteAll(_reads.Values, Applied); }
        finally { _source.Dispose(); _reads.Clear(); }
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
