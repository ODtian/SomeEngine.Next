using System.Numerics;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;

namespace SomeEngine.Navigation.Tests;

public sealed class NavigationDynamicTests
{
    [Fact]
    public void SourceInputVersionsSelectGeometryFromCompletedTransforms()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 3, ManagedPayloadPolicy = ManagedPayloadPolicy.Allow });
        using World world = WorldJobs.CreateWorld(scheduler: scheduler);
        var scene = new NavigationScene(new(default, default, default));
        var profile = new NavigationProfile([new Vector2(-0.1f), new(0.1f, -0.1f), new(0.1f), new(-0.1f, 0.1f)], 1.5f);
        var region = new NavigationRegionSources(scene, 7, new(new(0, -1, 0), new(12, 4, 6)),
            profile, new NavigationPolicy(), new() { CellSize = 0.25f, CellHeight = 0.05f, TileSize = 24 });
        var source = world.CreateEntity(new NavigationSource(Floor(0, 0, 6, 6)));
        world.Add(source, new SourcePlacement(Matrix4x4.Identity));
        var sources = new NavigationSourceSystem<SourcePlacement, SourcePlacementReader>(world, region);

        JobHandle transformed = new TranslateSources(new(6, 0, 0)).ScheduleParallel(world);
        NavigationBuild<NavigationSourceChanges> collected = sources.Schedule(transformed);
        NavigationBuild<NavigationRegionUpdate> built = NavigationBuildSystem.Schedule(collected, scheduler);
        NavigationApplySystem.Schedule(built, scheduler).Complete();

        Assert.True(NavigationScene.TryGetHeight(scene, new(9, 0, 3), out float height));
        Assert.Equal(0, height);
        Assert.Contains(new NavigationTileCoordinate(1, 0), built.State.RebuiltTiles.ToArray());

        NavigationSceneContent installed = scene.Content;
        world.Replace(source, new SourcePlacement(Matrix4x4.Identity));
        NavigationBuild<NavigationRegionUpdate> earlier = NavigationBuildSystem.Schedule(sources.Schedule(), scheduler);
        earlier.Complete();
        world.Replace(source, new SourcePlacement(Matrix4x4.CreateTranslation(6, 0, 0)));
        NavigationBuild<NavigationSourceChanges> latest = sources.Schedule();
        latest.Complete();
        NavigationApplySystem.Schedule(earlier, scheduler).Complete();
        Assert.Same(installed, scene.Content);

        NavigationApplySystem.Schedule(NavigationBuildSystem.Schedule(latest, scheduler), scheduler).Complete();
        Assert.True(NavigationScene.TryGetHeight(scene, new(9, 0, 3), out _));
        Assert.Equal(latest.State.Input.Version, scene.Content.GetTile(new(7, new(1, 0))).Mesh.ContentVersion);
        using NavigationTileSource<NavigationMesh> completedSource = region.CreateTileSource();
        NavigationMesh acquired = completedSource.ScheduleRead(new(1, 0), scheduler).Complete();
        Assert.True(NavigationMesh.TryGetHeight(acquired, new(9, 0, 3), out _));
    }

    [Fact]
    public void AddingAnObstacleRepairsItsMovingRouteAndRemovingItRestoresThePassage()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 4, ManagedPayloadPolicy = ManagedPayloadPolicy.Allow });
        using World world = WorldJobs.CreateWorld(scheduler: scheduler);
        var scene = new NavigationScene(new(default, default, default));
        var profile = new NavigationProfile([new Vector2(-0.15f), new(0.15f, -0.15f), new(0.15f), new(-0.15f, 0.15f)], 1.5f);
        var policy = new NavigationPolicy();
        var region = new NavigationRegionSources(scene, 7, new(new(0, -1, 0), new(24, 4, 12)),
            profile, policy, new() { CellSize = 0.25f, CellHeight = 0.05f, TileSize = 24 });
        Entity floor = world.CreateEntity(new NavigationSource(Floor(-1, -1, 25, 13)));
        world.Add(floor, new SourcePlacement(Matrix4x4.Identity));
        var sources = new NavigationSourceSystem<SourcePlacement, SourcePlacementReader>(world, region, scheduler);
        NavigationApplySystem.Schedule(NavigationBuildSystem.Schedule(sources.Schedule(), scheduler), scheduler).Complete();
        var navigation = new NavigationSystems<NavigationScene, SceneNavigationSearch, SceneSearchState, TestMotion, TestMotionReader>(world);
        Entity moving = world.CreateEntity(new TestMotion { Position = new(1.5f, 0, 2) });
        Entity unrelated = world.CreateEntity(new TestMotion { Position = new(1.5f, 0, 9) });
        navigation.AddAgent(moving, scene, profile, policy, new(new Vector3(22.5f, 0, 2), 0.05f), 2);
        navigation.AddAgent(unrelated, scene, profile, policy, new(new Vector3(4.5f, 0, 9), 0.05f), 2);
        JobHandle moved = new ConsumeSteering(0.1f).ScheduleParallel(world, navigation.Schedule(0.1f, 10000).Steering);
        navigation.ScheduleProgress(moved).Complete();
        NavigationPath oldPath = Assert.IsType<NavigationPath>(world.Read<NavigationPathResult>(moving).Path);
        NavigationPath unrelatedPath = Assert.IsType<NavigationPath>(world.Read<NavigationPathResult>(unrelated).Path);
        NavigationSceneTile remoteTile = scene.Content.GetTile(new(7, new(0, 1)));
        long contentVersion = scene.Content.ContentVersion, residenceVersion = scene.Content.ResidenceVersion;

        Entity obstacle = world.CreateEntity(new NavigationObstacle(Box(new(-0.5f, 0, -2.5f), new(0.5f, 3, 2.5f))));
        world.Add(obstacle, new SourcePlacement(Matrix4x4.CreateTranslation(11, 0, 1.5f)));
        NavigationBuild<NavigationRegionUpdate> blocked = NavigationBuildSystem.Schedule(sources.Schedule(), scheduler);
        NavigationApplySystem.Schedule(blocked, scheduler).Complete();
        navigation.Schedule(0.1f, 10000).Steering.Complete();
        NavigationPath detour = Assert.IsType<NavigationPath>(world.Read<NavigationPathResult>(moving).Path);

        Assert.NotSame(oldPath, detour);
        Assert.Same(unrelatedPath, world.Read<NavigationPathResult>(unrelated).Path);
        Assert.Same(remoteTile.Mesh, scene.Content.GetTile(remoteTile.Id).Mesh);
        Assert.True(scene.Content.ContentVersion > contentVersion);
        Assert.Equal(residenceVersion, scene.Content.ResidenceVersion);
        Assert.True(oldPath.TrySampleHeight(new(11, 0, 1.5f), out _));
        Assert.Null(scene.Content.FindTile(new(11, 0, 1.5f), out _));
        var positions = new List<Vector3>();
        for (int step = 0; step < 200 && world.Read<NavigationProgress>(moving).Status != NavigationStatus.Arrived; step++)
        {
            moved = new ConsumeSteering(0.1f).ScheduleParallel(world, navigation.Schedule(0.1f, 128).Steering);
            navigation.ScheduleProgress(moved).Complete();
            positions.Add(world.Read<TestMotion>(moving).Position);
        }
        Assert.Contains(positions, position => position.X > 10 && position.X < 12 && position.Z > 4);
        Assert.Equal(NavigationStatus.Arrived, world.Read<NavigationProgress>(moving).Status);

        world.DestroyEntity(obstacle);
        NavigationApplySystem.Schedule(NavigationBuildSystem.Schedule(sources.Schedule(), scheduler), scheduler).Complete();
        Entity fresh = world.CreateEntity(new TestMotion { Position = new(1.5f, 0, 2) });
        navigation.AddAgent(fresh, scene, profile, policy, new(new Vector3(22.5f, 0, 2), 0.05f), 2);
        navigation.Schedule(0.1f, 10000).Steering.Complete();
        NavigationPath restored = Assert.IsType<NavigationPath>(world.Read<NavigationPathResult>(fresh).Path);
        Assert.True(restored.Cost < detour.Cost);
        Assert.All(restored.Corners.ToArray(), corner => Assert.InRange(corner.Z, 1.9f, 3.1f));
        Assert.Same(unrelatedPath, world.Read<NavigationPathResult>(unrelated).Path);
    }

    [Fact]
    public void ManySourcesMergeMovedAndRemovedTileVersionsIntoOrderedInput()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 3, ManagedPayloadPolicy = ManagedPayloadPolicy.Allow });
        using World world = WorldJobs.CreateWorld(scheduler: scheduler);
        var scene = new NavigationScene(new(default, default, default));
        var profile = new NavigationProfile([new Vector2(-0.1f), new(0.1f, -0.1f), new(0.1f), new(-0.1f, 0.1f)], 1);
        var region = new NavigationRegionSources(scene, 7, new(new(0, -1, 0), new(64, 4, 8)),
            profile, new NavigationPolicy(), new() { CellSize = 0.25f, TileSize = 32 });
        NavigationGeometry geometry = Floor(2, 2, 3, 3);
        Entity[] entities = new Entity[260];
        for (int index = 0; index < entities.Length; index++)
        {
            entities[index] = world.CreateEntity(new NavigationSource(geometry, (ulong)index + 1));
            world.Add(entities[index], new SourcePlacement(Matrix4x4.Identity));
        }
        world.Replace(entities[^1], new SourcePlacement(Matrix4x4.CreateTranslation(48, 0, 0)));
        var sources = new NavigationSourceSystem<SourcePlacement, SourcePlacementReader>(world, region);
        NavigationSourceInput first = sources.Schedule().Complete().Input;
        world.Replace(entities[0], new SourcePlacement(Matrix4x4.CreateTranslation(32, 0, 0)));
        world.DestroyEntity(entities[^1]);

        NavigationSourceChanges changes = sources.Schedule().Complete();

        Assert.Equal([new NavigationTileCoordinate(0, 0), new(4, 0), new(6, 0)], changes.ChangedTiles.ToArray());
        Assert.Equal(first.Version + 1, changes.Input.Version);
        Assert.Equal(259, changes.Input.Geometry.Sources.Length);
        Assert.Equal(Matrix4x4.CreateTranslation(32, 0, 0), changes.Input.Geometry.Sources.Span[0].Transform);
        Assert.Equal(Enumerable.Range(1, 259).Select(value => (ulong)value),
            changes.Input.Geometry.Sources.ToArray().Select(source => source.Version));
        foreach (int tile in new[] { 0, 4, 6 }) Assert.Equal(changes.Input.Version, changes.Input.TileVersions.Span[tile]);
        Assert.Equal(first.TileVersions.Span[2], changes.Input.TileVersions.Span[2]);
        Assert.Same(changes.Input, sources.Schedule().Complete().Input);
    }

    // The floor rectangle is an example input supplied through NavigationGeometry.
    private static NavigationGeometry Floor(float left, float back, float right, float front) => new(
        [new(left, 0, back), new(left, 0, front), new(right, 0, front), new(right, 0, back)],
        [new(0, 1, 2, 0), new(0, 2, 3, 0)]);

    // This closed box is one obstacle geometry example; the source accepts oriented triangle meshes.
    private static NavigationGeometry Box(Vector3 minimum, Vector3 maximum) => new(
        [new(minimum.X, minimum.Y, minimum.Z), new(minimum.X, minimum.Y, maximum.Z),
         new(maximum.X, minimum.Y, maximum.Z), new(maximum.X, minimum.Y, minimum.Z),
         new(minimum.X, maximum.Y, minimum.Z), new(minimum.X, maximum.Y, maximum.Z),
         new(maximum.X, maximum.Y, maximum.Z), new(maximum.X, maximum.Y, minimum.Z)],
        [new(0, 2, 1, 0), new(0, 3, 2, 0), new(4, 5, 6, 0), new(4, 6, 7, 0),
         new(0, 4, 7, 0), new(0, 7, 3, 0), new(1, 2, 6, 0), new(1, 6, 5, 0),
         new(0, 1, 5, 0), new(0, 5, 4, 0), new(3, 7, 6, 0), new(3, 6, 2, 0)]);
}

internal readonly record struct SourcePlacement(Matrix4x4 Transform) : IComponent;
internal readonly struct SourcePlacementReader : INavigationTransformReader<SourcePlacement>
{
    public static Matrix4x4 Read(in SourcePlacement transform) => transform.Transform;
}

internal partial struct TranslateSources(Vector3 translation) : IJobEntity
{
    private readonly Vector3 _translation = translation;
    public void Execute(ref SourcePlacement placement) => placement = new(Matrix4x4.CreateTranslation(_translation));
}
