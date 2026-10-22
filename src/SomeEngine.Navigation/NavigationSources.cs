using System.Collections.Concurrent;
using System.Numerics;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;

namespace SomeEngine.Navigation;

public readonly record struct NavigationSource(NavigationGeometry Geometry, ulong Version = 1) : IEnableableComponent;

public enum NavigationObstacleParticipation : byte { Obstruction, SupportAndObstruction }

public readonly record struct NavigationObstacle(NavigationGeometry Geometry,
    NavigationObstacleParticipation Participation = NavigationObstacleParticipation.Obstruction, ulong Version = 1) : IEnableableComponent;

public interface INavigationTransformReader<TTransform> where TTransform : unmanaged, IComponent
{
    static abstract Matrix4x4 Read(in TTransform transform);
}

public sealed record NavigationRegionSettings(NavigationProfile Profile, NavigationPolicy Policy,
    NavigationSurfaceSettings Surface, NavigationMeshSettings Mesh, ReadOnlyMemory<NavigationRegionPort> Ports);

/// <summary>The fixed geometry, placement and per-tile input versions of one region calculation.</summary>
public sealed class NavigationSourceInput(NavigationRegionSettings settings, NavigationBuildInput geometry,
    ReadOnlyMemory<ulong> tileVersions, ulong version)
{
    public NavigationRegionSettings Settings { get; } = settings;
    public NavigationBuildInput Geometry { get; } = geometry;
    public ReadOnlyMemory<ulong> TileVersions { get; } = tileVersions;
    public ulong Version { get; } = version;
}

/// <summary>Source configuration and retained completed build data for one world-space scene region.</summary>
public sealed class NavigationRegionSources
{
    internal Lock Gate { get; } = new();
    private NavigationRegionSettings _settings;
    private NavigationSourceInput? _input;
    internal NavigationRegionUpdate? Completed;
    internal ulong InitialVersion { get; }

    public NavigationRegionSources(NavigationScene scene, int region, NavigationBounds bounds,
        NavigationProfile profile, NavigationPolicy policy, NavigationSurfaceSettings surface,
        NavigationMeshSettings? mesh = null, ReadOnlyMemory<NavigationRegionPort> ports = default)
    {
        Scene = scene;
        Region = region;
        Bounds = bounds;
        _settings = new(profile, policy, surface, mesh ?? new(), ports);
        foreach (NavigationSceneRegion existing in scene.Content.Regions.Span)
        {
            if (existing.Id != region) continue;
            InitialVersion = existing.Graph.ContentVersion;
            foreach (NavigationRegionTile tile in existing.Graph.Tiles.Span)
                InitialVersion = Math.Max(InitialVersion, tile.ContentVersion);
        }
    }

    public NavigationScene Scene { get; }
    public int Region { get; }
    public NavigationBounds Bounds { get; }
    public NavigationRegionSettings Settings => Volatile.Read(ref _settings);
    public NavigationSourceInput? Input => Volatile.Read(ref _input);
    public void SetProfile(NavigationProfile profile) { lock (Gate) Volatile.Write(ref _settings, _settings with { Profile = profile }); }
    public void SetPolicy(NavigationPolicy policy) { lock (Gate) Volatile.Write(ref _settings, _settings with { Policy = policy }); }
    public void SetBuildSettings(NavigationSurfaceSettings surface, NavigationMeshSettings mesh)
    { lock (Gate) Volatile.Write(ref _settings, _settings with { Surface = surface, Mesh = mesh }); }
    public void SetPorts(ReadOnlyMemory<NavigationRegionPort> ports)
    { lock (Gate) Volatile.Write(ref _settings, _settings with { Ports = ports }); }
    internal void SetInput(NavigationSourceInput input) => Volatile.Write(ref _input, input);
}

public sealed class NavigationSourceChanges(NavigationRegionSources region, NavigationRegionSettings settings)
{
    public NavigationRegionSources Region { get; } = region;
    public NavigationSourceInput Input { get; internal set; } = null!;
    public ReadOnlyMemory<NavigationTileCoordinate> ChangedTiles { get; internal set; }
    internal ConcurrentQueue<NavigationSourceRecord> Records { get; } = new();
    internal NavigationRegionSettings Settings { get; } = settings;
}

internal readonly record struct NavigationSourceKey(Entity Entity, bool Obstacle);
internal readonly record struct NavigationSourceRecord(NavigationSourceKey Key, NavigationGeometry Geometry,
    Matrix4x4 Transform, ulong Version, bool Walkable, NavigationBounds Bounds)
{
    internal static NavigationSourceRecord Create(Entity entity, bool obstacle, NavigationGeometry geometry,
        Matrix4x4 transform, ulong version, bool walkable)
        => new(new(entity, obstacle), geometry, transform, version, walkable, geometry.Bounds.Transform(transform));
}

/// <summary>Reads actual transforms after their producer and merges inserted, moved and removed geometry in Jobs.</summary>
public sealed class NavigationSourceSystem<TTransform, TReader>
    where TTransform : unmanaged, IComponent
    where TReader : struct, INavigationTransformReader<TTransform>
{
    private readonly World _world;
    private readonly NavigationRegionSources _region;
    private readonly Scheduler _scheduler;
    private readonly QueryDefinition _sources, _obstacles;
    private readonly Dictionary<NavigationSourceKey, NavigationSourceRecord> _previous = [];
    private JobHandle _completion;

    public NavigationSourceSystem(World world, NavigationRegionSources region, Scheduler? scheduler = null)
    {
        _world = world;
        _region = region;
        _scheduler = scheduler ?? JobSystem.Default;
        _sources = world.QueryDefinition().All<TTransform>().Enabled<NavigationSource>().Build();
        _obstacles = world.QueryDefinition().All<TTransform>().Enabled<NavigationObstacle>().Build();
    }

    public NavigationBuild<NavigationSourceChanges> Schedule(JobHandle transformReady = default)
    {
        var state = new NavigationSourceChanges(_region, _region.Settings);
        JobHandle ready = _scheduler.CombineDependencies([_completion, transformReady]);
        JobHandle sources = new CollectNavigationSources<TTransform, TReader>(state.Records).ScheduleParallel(_world,
            new JobEntityScheduleOptions(filter: _sources), ready);
        JobHandle obstacles = new CollectNavigationObstacles<TTransform, TReader>(state.Records).ScheduleParallel(_world,
            new JobEntityScheduleOptions(filter: _obstacles), ready);
        _completion = _scheduler.Schedule(new MergeSources(state, _previous),
            [JobResourceAccess.Exclusive(_scheduler.GetResourceToken(_region)),
             NavigationJobAccess.Read(_scheduler, state.Settings.Profile.Footprint)], _scheduler.CombineDependencies([sources, obstacles]));
        return new(state, _completion);
    }

    private readonly struct MergeSources(NavigationSourceChanges state,
        Dictionary<NavigationSourceKey, NavigationSourceRecord> previous) : IJob
    {
        public void Execute()
        {
            NavigationRegionSources region = state.Region;
            lock (region.Gate)
            {
                NavigationRegionSettings settings = state.Settings;
                NavigationSourceInput? old = region.Input;
                var current = state.Records.ToDictionary(record => record.Key);
                float tileWidth = settings.Surface.TileSize * settings.Surface.CellSize;
                int width = (int)MathF.Ceiling((region.Bounds.Maximum.X - region.Bounds.Minimum.X) / tileWidth);
                int depth = (int)MathF.Ceiling((region.Bounds.Maximum.Z - region.Bounds.Minimum.Z) / tileWidth);
                var changed = new bool[width * depth];
                if (old is null || !ReferenceEquals(old.Settings, settings)) Array.Fill(changed, true);
                else
                {
                    foreach (NavigationSourceRecord record in current.Values)
                    {
                        if (previous.TryGetValue(record.Key, out NavigationSourceRecord before) && before == record) continue;
                        AddBounds(record.Bounds);
                        if (previous.TryGetValue(record.Key, out before)) AddBounds(before.Bounds);
                    }
                    foreach (NavigationSourceRecord before in previous.Values)
                        if (!current.ContainsKey(before.Key)) AddBounds(before.Bounds);
                }
                previous.Clear();
                foreach (var pair in current) previous.Add(pair.Key, pair.Value);
                var coordinates = new List<NavigationTileCoordinate>();
                for (int index = 0; index < changed.Length; index++)
                    if (changed[index]) coordinates.Add(new(index % width, index / width));
                state.ChangedTiles = coordinates.ToArray();
                if (coordinates.Count == 0) { state.Input = old!; return; }
                ulong version = (old?.Version ?? region.InitialVersion) + 1;
                ulong[] tileVersions = old is not null && old.TileVersions.Length == changed.Length ? old.TileVersions.ToArray() : new ulong[changed.Length];
                for (int index = 0; index < changed.Length; index++) if (changed[index]) tileVersions[index] = version;
                NavigationGeometrySource[] sources = current.Values.OrderBy(record => record.Key.Entity.Index).ThenBy(record => record.Key.Obstacle)
                    .Select(record => new NavigationGeometrySource(record.Geometry, record.Transform, record.Version,
                        record.Walkable, record.Key.Obstacle)).ToArray();
                float padding = NavigationSurfaceTraversal.GetRotationRadius(settings.Profile.Footprint.Span) + 2 * settings.Surface.CellSize;
                var surface = settings.Surface with { BorderSize = MathF.Max(settings.Surface.BorderSize, padding) };
                state.Input = new(settings, new(sources, region.Bounds, surface), tileVersions, version);
                region.SetInput(state.Input);

                void AddBounds(NavigationBounds bounds)
                {
                    float padding = NavigationSurfaceTraversal.GetRotationRadius(settings.Profile.Footprint.Span) + 3 * settings.Surface.CellSize;
                    int minX = Math.Max(0, (int)MathF.Floor((bounds.Minimum.X - padding - region.Bounds.Minimum.X) / tileWidth));
                    int maxX = Math.Min(width - 1, (int)MathF.Floor((bounds.Maximum.X + padding - region.Bounds.Minimum.X) / tileWidth));
                    int minZ = Math.Max(0, (int)MathF.Floor((bounds.Minimum.Z - padding - region.Bounds.Minimum.Z) / tileWidth));
                    int maxZ = Math.Min(depth - 1, (int)MathF.Floor((bounds.Maximum.Z + padding - region.Bounds.Minimum.Z) / tileWidth));
                    for (int z = minZ; z <= maxZ; z++)
                    for (int x = minX; x <= maxX; x++) changed[z * width + x] = true;
                }
            }
        }
    }
}

public readonly struct NavigationSourceResources : IJobComponentResources<NavigationSource>
{
    public static void Collect(in NavigationSource component, ref JobResourceAccessBuilder resources)
    { resources.Read(component.Geometry.Positions); resources.Read(component.Geometry.Triangles); }
}

public readonly struct NavigationObstacleResources : IJobComponentResources<NavigationObstacle>
{
    public static void Collect(in NavigationObstacle component, ref JobResourceAccessBuilder resources)
    { resources.Read(component.Geometry.Positions); resources.Read(component.Geometry.Triangles); }
}

internal partial struct CollectNavigationSources<TTransform, TReader>(ConcurrentQueue<NavigationSourceRecord> records) : IJobEntity
    where TTransform : unmanaged, IComponent
    where TReader : struct, INavigationTransformReader<TTransform>
{
    private readonly ConcurrentQueue<NavigationSourceRecord> _records = records;
    public void Execute(Entity entity, [JobResources<NavigationSourceResources>] in NavigationSource source, in TTransform transform) =>
        _records.Enqueue(NavigationSourceRecord.Create(entity, false, source.Geometry, TReader.Read(in transform), source.Version, true));
}

internal partial struct CollectNavigationObstacles<TTransform, TReader>(ConcurrentQueue<NavigationSourceRecord> records) : IJobEntity
    where TTransform : unmanaged, IComponent
    where TReader : struct, INavigationTransformReader<TTransform>
{
    private readonly ConcurrentQueue<NavigationSourceRecord> _records = records;
    public void Execute(Entity entity, [JobResources<NavigationObstacleResources>] in NavigationObstacle obstacle, in TTransform transform) =>
        _records.Enqueue(NavigationSourceRecord.Create(entity, true, obstacle.Geometry, TReader.Read(in transform), obstacle.Version,
            obstacle.Participation == NavigationObstacleParticipation.SupportAndObstruction));
}
