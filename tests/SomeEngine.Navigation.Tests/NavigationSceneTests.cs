using System.Numerics;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;

namespace SomeEngine.Navigation.Tests;

public sealed class NavigationSceneTests
{
    [Fact]
    public void AgentFollowsTheEntranceRouteAcrossTilesAndPlacedRegions()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig
        {
            WorkerCount = 4,
            ManagedPayloadPolicy = ManagedPayloadPolicy.Allow,
        });
        using World world = WorldJobs.CreateWorld(scheduler: scheduler);
        var profile = new NavigationProfile(
            [new(-0.1f, -0.1f), new(0.1f, -0.1f), new(0.1f, 0.1f), new(-0.1f, 0.1f)],
            1.8f, maximumSpeed: 2f);
        var policy = new NavigationPolicy();
        NavigationMesh mesh = NavigationRegionBuilder.Schedule(TwoTileBend(), profile, policy,
            [new NavigationRegionPort(7, new(1, 0), new(6, 0, 1))], scheduler: scheduler).Complete().Mesh;
        Matrix4x4 placement = Matrix4x4.CreateRotationY(MathF.PI) * Matrix4x4.CreateTranslation(12, 0, 2);
        NavigationScene scene = NavigationSceneBuilder.Schedule(
            [new NavigationRegionPlacement(10, mesh, Matrix4x4.Identity), new(20, mesh, placement)],
            [new NavigationSceneConnection(90, 10, 7, 20, 7)], profile, policy, scheduler: scheduler).Complete().Scene;
        var navigation = new NavigationSystems<NavigationScene, SceneNavigationSearch,
            SceneSearchState, TestMotion, TestMotionReader>(world);
        Entity entity = world.CreateEntity(new TestMotion { Position = new(1, 0, 1) });
        navigation.AddAgent(entity, scene, profile, policy,
            new NavigationGoal(new Vector3(11, 0, 1), 0.05f), desiredSpeed: 2f);

        for (int step = 0; step < 200 && world.Read<NavigationProgress>(entity).Status != NavigationStatus.Arrived; step++)
        {
            NavigationStep scheduled = navigation.Schedule(0.1f, expansionBudget: 2);
            JobHandle moved = new ConsumeSteering(0.1f).ScheduleParallel(world, scheduled.Steering);
            navigation.ScheduleProgress(moved).Complete();
            Vector3 position = world.Read<TestMotion>(entity).Position;
            Assert.False(position.X > 2.001f && position.X < 3.999f && position.Z < 1.999f);
            Assert.False(position.X > 8.001f && position.X < 9.999f && position.Z > 0.001f);
        }

        NavigationPathResult result = world.Read<NavigationPathResult>(entity);
        Assert.Equal(NavigationSearchStatus.Found, result.Status);
        NavigationPath path = Assert.IsType<NavigationPath>(result.Path);
        NavigationRegionRoute route = Assert.IsType<NavigationRegionRoute>(path.RegionRoute);
        // This example's entrance graph uses sqrt(5), 2 and sqrt(8) in each placed region.
        float expectedCost = 2 * (MathF.Sqrt(5) + 2 + MathF.Sqrt(8));
        Assert.InRange(route.Cost, expectedCost - 0.001f, expectedCost + 0.001f);
        Assert.InRange(path.Cost, expectedCost - 0.001f, expectedCost + 0.001f);
        Assert.Equal([10, 20], route.Entrances.Span.ToArray().Select(entrance => entrance.Region).Distinct());
        Assert.Equal(4, path.Dependencies.Length);
        foreach (int region in new[] { 10, 20 })
        foreach (NavigationTileCoordinate tile in new[] { new NavigationTileCoordinate(0, 0), new(1, 0) })
            Assert.Contains(new NavigationPathDependency(tile, 17, region), path.Dependencies.Span.ToArray());
        Assert.Contains(new NavigationConnectionDependency(90, 1), path.ConnectionDependencies.Span.ToArray());
        Assert.Equal([10, 20], path.Sections.Span.ToArray().Select(section => section.Region).Distinct());
        Assert.Equal(NavigationStatus.Arrived, world.Read<NavigationProgress>(entity).Status);
    }

    // This programmatic mesh is an example of a two-tile region with a bent detailed corridor.
    private static NavigationMesh TwoTileBend() => new(
        [new(0, 0, 0), new(2, 0, 0), new(2, 0, 4), new(0, 0, 4),
         new(2, 0, 2), new(4, 0, 2), new(4, 0, 4), new(2, 0, 4),
         new(4, 0, 0), new(6, 0, 0), new(6, 0, 4), new(4, 0, 4)],
        [0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11],
        [new(0, 4, 0, 1, Tile: new(0, 0)), new(4, 4, 1, 2, Tile: new(1, 0)),
         new(8, 4, 3, 1, Tile: new(1, 0))],
        [new(1, new(2, 0, 4), new(2, 0, 2)),
         new(0, new(2, 0, 2), new(2, 0, 4)), new(2, new(4, 0, 4), new(4, 0, 2)),
         new(1, new(4, 0, 2), new(4, 0, 4))], contentVersion: 17);
}
