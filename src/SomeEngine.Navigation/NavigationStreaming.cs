using System.Numerics;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;

namespace SomeEngine.Navigation;

/// <summary>An ordinary application-owned demand for nearby and route-ahead detailed data.</summary>
public sealed class NavigationInterest(Vector3 position, float radius)
{
    public Vector3 Position { get; set; } = position;
    public float Radius { get; set; } = radius;
    public Vector3 Direction { get; set; }
    public float LookAheadDistance { get; set; }
    public NavigationPath? Path { get; set; }
}

public readonly record struct NavigationStreamingRead(NavigationSceneTileId Tile, JobHandle Completion);

/// <summary>Application-driven background reads and short jobs that install complete detailed meshes.</summary>
public sealed class NavigationStreaming(NavigationScene scene, Scheduler? scheduler = null) : IDisposable
{
    private readonly Scheduler _scheduler = scheduler ?? JobSystem.Default;
    private readonly Dictionary<int, NavigationTileSource<NavigationMesh>> _sources = [];
    private readonly Dictionary<NavigationSceneTileId, NavigationTileRead<NavigationMesh>> _reads = [];
    private readonly Dictionary<NavigationSceneTileId, long> _lastUsed = [];
    private long _update;
    public IList<NavigationInterest> Interests { get; } = new List<NavigationInterest>();
    public int ResidentTileBudget { get; set; } = int.MaxValue;
    /// <summary>Only data already decoded at Update participates in this handle.</summary>
    public JobHandle Applied { get; private set; }
    public IReadOnlyList<NavigationStreamingRead> Reads => _reads.Select(pair => new NavigationStreamingRead(pair.Key, pair.Value.Completion)).ToArray();

    public void AddSource(int region, NavigationTileSource<NavigationMesh> source) => _sources.Add(region, source.Retain());

    public void SetSource(int region, NavigationTileSource<NavigationMesh> source)
    {
        NavigationTileSource<NavigationMesh> retained = source.Retain();
        if (_sources.TryGetValue(region, out NavigationTileSource<NavigationMesh>? previous)) previous.Dispose();
        _sources[region] = retained;
    }

    public void Update()
    {
        if (!Applied.IsCompleted) return;
        Applied.Complete();
        NavigationSceneContent content = scene.Content;
        NavigationInterestData[] interests = NavigationStreamingRanges.CaptureInterests(Interests);
        NavigationSceneRegion[] regions = content.Regions.ToArray();
        var nearby = NavigationStreamingRanges.CollectTiles(_scheduler,
            regions.Select(region => region.Graph.Tiles).ToArray(), interests);
        var routes = new List<NavigationSceneTileId>[interests.Length];
        _scheduler.ExecuteParallel(new CollectRoutes(interests, routes), interests.Length,
            JobScheduleOptions.AutomaticBatchSize, NavigationStreamingRanges.CollectPathReads(_scheduler, interests).ToArray());
        var demanded = new HashSet<NavigationSceneTileId>();
        for (int interest = 0; interest < interests.Length; interest++)
        {
            foreach (NavigationInterestTileRange range in nearby)
                foreach (NavigationTileCoordinate tile in range.TilesByInterest[interest]) demanded.Add(new(regions[range.DirectoryIndex].Id, tile));
            foreach (NavigationSceneTileId tile in routes[interest]) demanded.Add(tile);
        }
        NavigationSceneTileId[] candidates = demanded.Concat(content.Tiles.ToArray().Select(tile => tile.Id))
            .Concat(_reads.Keys).Distinct().ToArray();
        var distances = NavigationStreamingRanges.CalculateDistances(_scheduler, candidates, interests, new SceneTileBounds(content));
        var selected = demanded.OrderBy(tile => distances[tile]).Take(ResidentTileBudget).ToHashSet();
        _update++;
        foreach (NavigationSceneTileId tile in selected)
        {
            _lastUsed[tile] = _update;
            if (!content.TryGetTile(tile, out _) && !_reads.ContainsKey(tile) && _sources.TryGetValue(tile.Region, out var source))
                _reads.Add(tile, source.ScheduleRead(tile.Tile, _scheduler));
        }

        var completed = new List<NavigationSceneTile>();
        foreach (var pair in _reads.ToArray())
        {
            if (!pair.Value.Completion.IsCompleted) continue;
            _reads.Remove(pair.Key);
            completed.Add(new(pair.Key.Region, pair.Key.Tile, pair.Value.Complete()));
        }
        HashSet<NavigationSceneTileId> keep = content.Tiles.ToArray().Select(tile => tile.Id).Concat(completed.Select(tile => tile.Id))
            .Distinct().OrderByDescending(selected.Contains).ThenBy(tile => distances[tile])
            .ThenByDescending(tile => _lastUsed.GetValueOrDefault(tile)).Take(ResidentTileBudget).ToHashSet();
        NavigationSceneTileId[] removed = content.Tiles.ToArray().Where(tile => !keep.Contains(tile.Id)).Select(tile => tile.Id).ToArray();
        completed.RemoveAll(tile => !keep.Contains(tile.Id));
        foreach (NavigationSceneTileId tile in removed) _lastUsed.Remove(tile);
        if (completed.Count == 0 && removed.Length == 0) return;
        Applied = _scheduler.Schedule(new ScheduleApplyTiles(scene, completed.ToArray(), removed, _scheduler));
    }

    private readonly struct CollectRoutes(NavigationInterestData[] interests, List<NavigationSceneTileId>[] output) : IJobParallelFor
    {
        public void Execute(int item)
        {
            NavigationInterestData interest = interests[item];
            List<NavigationSceneTileId> demanded = output[item] = [];
            if (interest.Path is not { } path) return;
            if (!path.RequiredTiles.IsEmpty) demanded.Add(path.RequiredTiles.Span[0]);
            if (path.RegionRoute is not { } route) return;
            ReadOnlySpan<NavigationRouteEntrance> entrances = route.Entrances.Span;
            int closest = 0;
            float closestDistance = float.PositiveInfinity;
            for (int index = 0; index < entrances.Length; index++)
            {
                float distance = Vector3.DistanceSquared(interest.Position, entrances[index].Position);
                if (distance <= closestDistance) { closest = index; closestDistance = distance; }
            }
            Vector3 previous = interest.Position;
            float ahead = 0;
            for (int index = closest; index < entrances.Length; index++)
            {
                ahead += Vector3.Distance(previous, entrances[index].Position);
                if (ahead > interest.LookAheadDistance) break;
                demanded.Add(new(entrances[index].Region, entrances[index].Tile));
                previous = entrances[index].Position;
            }
        }
    }

    private readonly struct SceneTileBounds(NavigationSceneContent content) : INavigationTileBounds<NavigationSceneTileId>
    {
        public NavigationBounds GetBounds(NavigationSceneTileId id)
        {
            foreach (NavigationRegionTile tile in content.GetRegion(id.Region).Graph.Tiles.Span)
                if (tile.Tile == id.Tile) return tile.Bounds;
            return default;
        }
        public void AddReadAccesses(Scheduler scheduler, List<JobResourceAccess> accesses)
        {
            accesses.Add(NavigationJobAccess.Read(scheduler, content.Regions));
            foreach (NavigationSceneRegion region in content.Regions.Span)
                accesses.Add(NavigationJobAccess.Read(scheduler, region.Graph.Tiles));
        }
    }

    public void Dispose()
    {
        try { NavigationTileRead<NavigationMesh>.CompleteAll(_reads.Values, Applied); }
        finally
        {
            foreach (NavigationTileSource<NavigationMesh> source in _sources.Values) source.Dispose();
            _sources.Clear();
            _reads.Clear();
        }
    }

    private sealed class TileConversions(NavigationScene scene, NavigationSceneTile[] loaded, NavigationSceneTileId[] removed)
    {
        internal NavigationScene Scene { get; } = scene;
        internal NavigationSceneTile[] Loaded { get; } = loaded;
        internal NavigationSceneTileId[] Removed { get; } = removed;
        internal NavigationSceneTile?[] Converted { get; } = new NavigationSceneTile?[loaded.Length];
        internal NavigationSceneContent Content = null!;
        internal bool ContentChanged;
    }

    private readonly struct ScheduleApplyTiles(NavigationScene scene, NavigationSceneTile[] loaded,
        NavigationSceneTileId[] removed, Scheduler scheduler) : IJob
    {
        public void Execute()
        {
            var data = new TileConversions(scene, loaded, removed);
            JobResourceToken sceneResource = scheduler.GetResourceToken(scene);
            JobHandle converted = scheduler.Schedule(new ReadTileInputs(data, scheduler), JobResourceAccess.Read(sceneResource));
            JobHandle installed = scheduler.Schedule(new InstallTiles(data, scheduler), JobResourceAccess.Write(sceneResource), converted);
            _ = scheduler.Schedule(new RetryTiles(data, scheduler), installed);
        }
    }

    private readonly struct ReadTileInputs(TileConversions data, Scheduler scheduler) : IJob
    {
        public void Execute()
        {
            NavigationSceneContent current = data.Content = data.Scene.Content;
            var accesses = new List<JobResourceAccess>
            {
                NavigationJobAccess.Read(scheduler, current.Regions),
            };
            foreach (NavigationSceneRegion region in current.Regions.Span)
                accesses.Add(NavigationJobAccess.Read(scheduler, region.Graph.Tiles));
            foreach (NavigationSceneTile tile in data.Loaded) tile.Mesh.AddReadAccesses(scheduler, accesses);
            _ = scheduler.ScheduleParallel(new ConvertTiles(data), data.Loaded.Length,
                JobScheduleOptions.AutomaticBatchSize, accesses.ToArray());
        }
    }

    private readonly struct ConvertTiles(TileConversions data) : IJobParallelFor
    {
        public void Execute(int index)
        {
            NavigationSceneTile tile = data.Loaded[index];
            NavigationSceneRegion region = data.Content.GetRegion(tile.Region);
            foreach (NavigationRegionTile description in region.Graph.Tiles.Span)
                if (description.Tile == tile.Tile && description.ContentVersion == tile.Mesh.ContentVersion)
                {
                    data.Converted[index] = tile with { Mesh = tile.Mesh.Transform(region.Transform) };
                    return;
                }
        }
    }

    private readonly struct InstallTiles(TileConversions data, Scheduler scheduler) : IJob
    {
        public void Execute()
        {
            NavigationSceneContent current = data.Content;
            if (!ReferenceEquals(data.Scene.Content, current))
            {
                data.ContentChanged = true;
                return;
            }
            var accesses = new List<JobResourceAccess>
            {
                NavigationJobAccess.Read(scheduler, current.Regions), NavigationJobAccess.Read(scheduler, current.Connections),
                NavigationJobAccess.Read(scheduler, current.Tiles),
            };
            foreach (NavigationSceneRegion region in current.Regions.Span)
                accesses.Add(NavigationJobAccess.Read(scheduler, region.Graph.Tiles));
            _ = scheduler.Schedule(new ApplyConvertedTiles(data), accesses.ToArray());
        }
    }

    private readonly struct ApplyConvertedTiles(TileConversions data) : IJob
    {
        public void Execute()
        {
            NavigationSceneContent current = data.Content;
            var tiles = current.Tiles.ToArray().ToDictionary(tile => tile.Id);
            bool changed = false;
            foreach (NavigationSceneTileId tile in data.Removed) changed |= tiles.Remove(tile);
            foreach (NavigationSceneTile? tile in data.Converted)
            {
                if (tile is null) continue;
                tiles[tile.Id] = tile;
                changed = true;
            }
            if (changed) data.Scene.ApplyCompleted(new(current.Regions, current.Connections, tiles.Values.ToArray()), residenceOnly: true);
        }
    }

    private readonly struct RetryTiles(TileConversions data, Scheduler scheduler) : IJob
    {
        public void Execute()
        {
            if (data.ContentChanged)
                _ = scheduler.Schedule(new ScheduleApplyTiles(data.Scene, data.Loaded, data.Removed, scheduler));
        }
    }
}

/// <summary>Place this system in an application-owned background group and drive it independently of the frame group.</summary>
public readonly struct NavigationStreamingSystem(NavigationStreaming streaming) : ISystem<ImmediateSystemContext>
{
    public void OnUpdate(ref ImmediateSystemContext context) => streaming.Update();
}
