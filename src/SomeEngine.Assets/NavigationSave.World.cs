using System.Numerics;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using SomeEngine.Job;
using SomeEngine.Navigation;

namespace SomeEngine.Assets;

/// <summary>Strongly typed game transform readers used when saved source regions are rebuilt.</summary>
public sealed class NavigationSaveOptions
{
    internal Dictionary<string, Func<World, NavigationRegionSources, Scheduler, NavigationBuild<NavigationRegionUpdate>>> SourceReaders { get; } = [];
    public NavigationSaveOptions UseSources<TTransform, TReader>(string name = "default")
        where TTransform : unmanaged, IComponent where TReader : struct, INavigationTransformReader<TTransform>
    {
        SourceReaders[name] = static (world, region, scheduler) => NavigationBuildSystem.Schedule(
            new NavigationSourceSystem<TTransform, TReader>(world, region, scheduler).Schedule(), scheduler);
        return this;
    }
}

public sealed partial class NavigationSaveResources
{
    internal List<NavigationSaveRegion> RegionEntries { get; } = [];
    public IEnumerable<NavigationRegionSources> Regions => RegionEntries.Select(value => value.Region);

    public void Register(NavigationScene scene, ReadOnlyMemory<NavigationRegionPlacement> placements,
        NavigationProfile profile, NavigationPolicy policy) => Sources[scene] = new SceneSaveSource(placements, profile, policy);
    public void Register(HeadingNavigationScene scene, HeadingNavigationMesh mesh) => Sources[scene] = new HeadingSceneSaveSource(mesh);

    public void RegisterRegion(NavigationRegionSources region, NavigationGridBuildSystem? grids = null, string transformReader = "default")
    {
        RegionEntries.RemoveAll(entry => ReferenceEquals(entry.Region, region));
        RegionEntries.Add(new(region, grids, transformReader));
        Sources.TryAdd(region.Scene, new SceneSaveSource(default, region.Settings.Profile, region.Settings.Policy));
    }
}

internal sealed record NavigationSaveRegion(NavigationRegionSources Region, NavigationGridBuildSystem? Grids, string TransformReader);
internal sealed record SceneSaveSource(ReadOnlyMemory<NavigationRegionPlacement> Placements,
    NavigationProfile Profile, NavigationPolicy Policy) : NavigationSavedResource;
internal sealed record HeadingSceneSaveSource(HeadingNavigationMesh Mesh) : NavigationSavedResource;
internal readonly record struct SavedPlacement(int Id, int Mesh, Matrix4x4 Transform);
internal sealed record SavedScene(int Profile, int Policy, SavedPlacement[] Placements,
    NavigationSceneConnection[] Connections, int Links) : NavigationSavedResource;
internal sealed record SavedHeadingScene(int Mesh) : NavigationSavedResource;
internal sealed record SavedBuiltGrid(int Region) : NavigationSavedResource;
internal readonly record struct NavigationSavedSource(Entity Entity, int Geometry, bool Obstacle,
    NavigationObstacleParticipation Participation, ulong Version, bool Enabled);
internal readonly record struct NavigationSavedCrowd(Entity Entity, float Weight, bool Enabled);
internal sealed record NavigationSavedRegion(int Scene, int Region, NavigationBounds Bounds, int Profile, int Policy,
    NavigationSurfaceSettings Surface, NavigationMeshSettings Mesh, NavigationRegionPort[] Ports, string TransformReader,
    bool BuildGrid, NavigationGridConnection[] Connections, NavigationGridFootprintMode FootprintMode);

internal sealed partial class NavigationSaveResourceCapture
{
    private readonly Dictionary<object, NavigationSavedResource> _derived = new(ReferenceEqualityComparer.Instance);
    internal List<NavigationSavedRegion> Regions { get; } = [];

    internal void CaptureRegions()
    {
        foreach (NavigationFlow flow in sources.Sources.Keys.OfType<NavigationFlow>())
            if (flow.Input.HeadingGraph is { } graph) CaptureHeadingGraph(graph, flow.Profile);
        foreach (NavigationSaveRegion registration in sources.RegionEntries)
        {
            NavigationRegionSources region = registration.Region;
            if (registration.Grids is { Completed: { } update }) _derived[update.Grid] = new SavedBuiltGrid(Regions.Count);
            NavigationRegionSettings settings = region.Settings;
            Regions.Add(new(Add(region.Scene), region.Region, region.Bounds, Add(settings.Profile), Add(settings.Policy),
                settings.Surface, settings.Mesh, settings.Ports.ToArray(), registration.TransformReader, registration.Grids is not null,
                registration.Grids?.Connections.ToArray() ?? [], registration.Grids?.FootprintMode ?? NavigationGridFootprintMode.AllHeadings));
        }
    }

    private SavedScene CaptureScene(NavigationScene scene)
    {
        var source = (SceneSaveSource)sources.Sources[scene];
        NavigationSceneContent content = scene.Content;
        return new(Add(source.Profile), Add(source.Policy), source.Placements.ToArray()
            .Select(placement => new SavedPlacement(placement.Id, Add(placement.Mesh), placement.Transform)).ToArray(),
            content.Connections.ToArray().Where(connection => connection.Links is null).ToArray(), Add(content.Links));
    }
}

public static partial class NavigationSave
{
    private static NavigationSavedCrowd[] CaptureCrowd(World world)
    {
        var result = new List<NavigationSavedCrowd>();
        QueryHandle query = world.Query(world.QueryDefinition().Read<NavigationCrowdAgent>());
        try
        {
            world.ExecuteReadSnapshot(query, cursor =>
            {
                foreach (QueryRow row in cursor.Rows)
                    result.Add(new(row.Entity, row.Read<NavigationCrowdAgent>().Weight, world.IsEnabled<NavigationCrowdAgent>(row.Entity)));
            });
        }
        finally { world.ReleaseQuery(query); }
        return result.ToArray();
    }

    private static NavigationSavedSource[] CaptureSources(World world, NavigationSaveResourceCapture capture)
    {
        var result = new List<NavigationSavedSource>();
        QueryHandle sources = world.Query(world.QueryDefinition().Read<NavigationSource>());
        QueryHandle obstacles = world.Query(world.QueryDefinition().Read<NavigationObstacle>());
        try
        {
            world.ExecuteReadSnapshot(sources, cursor =>
            {
                foreach (QueryRow row in cursor.Rows)
                {
                    NavigationSource source = row.Read<NavigationSource>();
                    result.Add(new(row.Entity, capture.Add(source.Geometry), false, default, source.Version, world.IsEnabled<NavigationSource>(row.Entity)));
                }
            });
            world.ExecuteReadSnapshot(obstacles, cursor =>
            {
                foreach (QueryRow row in cursor.Rows)
                {
                    NavigationObstacle source = row.Read<NavigationObstacle>();
                    result.Add(new(row.Entity, capture.Add(source.Geometry), true, source.Participation, source.Version, world.IsEnabled<NavigationObstacle>(row.Entity)));
                }
            });
        }
        finally { world.ReleaseQuery(sources); world.ReleaseQuery(obstacles); }
        return result.ToArray();
    }
}

internal sealed partial class NavigationSaveResourceReader
{
    private readonly Dictionary<int, NavigationGrid> _builtGrids = [];

    internal async Task RestoreSources(World world, NavigationSavedTable table, NavigationSaveOptions options)
    {
        foreach (NavigationSavedCrowd crowd in table.Crowd)
        {
            world.Add(crowd.Entity, new NavigationCrowdAgent(crowd.Weight));
            if (!crowd.Enabled) world.Disable<NavigationCrowdAgent>(crowd.Entity);
        }
        foreach (NavigationSavedSource source in table.Sources)
        {
            var geometry = (NavigationGeometry)await Get(source.Geometry).ConfigureAwait(false);
            if (source.Obstacle)
            {
                world.Add(source.Entity, new NavigationObstacle(geometry, source.Participation, source.Version));
                if (!source.Enabled) world.Disable<NavigationObstacle>(source.Entity);
            }
            else
            {
                world.Add(source.Entity, new NavigationSource(geometry, source.Version));
                if (!source.Enabled) world.Disable<NavigationSource>(source.Entity);
            }
        }
        for (int index = 0; index < table.Regions.Length; index++)
        {
            NavigationSavedRegion saved = table.Regions[index];
            var scene = (NavigationScene)await Get(saved.Scene).ConfigureAwait(false);
            var region = new NavigationRegionSources(scene, saved.Region, saved.Bounds,
                (NavigationProfile)await Get(saved.Profile).ConfigureAwait(false), (NavigationPolicy)await Get(saved.Policy).ConfigureAwait(false),
                saved.Surface, saved.Mesh, saved.Ports);
            NavigationBuild<NavigationRegionUpdate> built = options.SourceReaders[saved.TransformReader](world, region, scheduler);
            JobHandle applied = NavigationApplySystem.Schedule(built, scheduler);
            NavigationGridBuildSystem? grids = null;
            if (saved.BuildGrid)
            {
                grids = new(region, saved.Connections, saved.FootprintMode, scheduler);
                _builtGrids[index] = grids.Schedule(built, applied).Complete().Grid;
            }
            else applied.Complete();
            restored.RegisterRegion(region, grids, saved.TransformReader);
        }
    }

    private async Task<NavigationScene> RestoreScene(SavedScene saved)
    {
        var profile = (NavigationProfile)await Get(saved.Profile).ConfigureAwait(false);
        var policy = (NavigationPolicy)await Get(saved.Policy).ConfigureAwait(false);
        var placements = new NavigationRegionPlacement[saved.Placements.Length];
        for (int index = 0; index < placements.Length; index++)
        {
            SavedPlacement placement = saved.Placements[index];
            placements[index] = new(placement.Id, (NavigationMesh)await Get(placement.Mesh).ConfigureAwait(false), placement.Transform);
        }
        NavigationScene scene = NavigationSceneBuilder.Schedule(placements, saved.Connections, profile, policy, scheduler: scheduler).Complete().Scene;
        if (saved.Links >= 0) scene.ScheduleLinks((NavigationLinkSet)await Get(saved.Links).ConfigureAwait(false), scheduler: scheduler).Complete();
        restored.Register(scene, placements, profile, policy);
        return scene;
    }
}
