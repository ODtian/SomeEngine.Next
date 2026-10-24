using System.Numerics;
using SomeEngine.ECS;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;

namespace SomeEngine.Navigation.Tests;

public sealed class NavigationSceneBoundaryTests
{
    [Fact]
    public void AvoidanceFollowsPlacedTileWallsAndPassesTheirConnectedRegionBoundary()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
        using World world = WorldJobs.CreateWorld(scheduler: scheduler);
        var profile = new NavigationProfile([new(-0.1f, -0.1f), new(0.1f, -0.1f), new(0.1f, 0.1f), new(-0.1f, 0.1f)],
            1, maximumSpeed: 1.5f);
        var policy = new NavigationPolicy();
        // These adjacent two-tile regions are an example of walls and openings under placement.
        var mesh = new NavigationMesh(
            [new(0, 0, 0), new(0, 0, 2), new(4, 0, 2), new(4, 0, 0),
             new(4, 0, 0), new(4, 0, 2), new(8, 0, 2), new(8, 0, 0)],
            [0, 1, 2, 3, 4, 5, 6, 7],
            [new(0, 4, 0, 1, Tile: new(0, 0)), new(4, 4, 1, 1, Tile: new(1, 0))],
            [new(1, new(4, 0, 2), new(4, 0, 0)), new(0, new(4, 0, 0), new(4, 0, 2))]);
        NavigationMesh region = NavigationRegionBuilder.Schedule(mesh, profile, policy,
            new NavigationRegionPort[] { new(1, new(0, 0), new(0, 0, 1)), new(2, new(1, 0), new(8, 0, 1)) },
            scheduler: scheduler).Complete().Mesh;
        NavigationScene scene = NavigationSceneBuilder.Schedule(
            [new(10, region, Matrix4x4.Identity), new(20, region, Matrix4x4.CreateTranslation(8, 0, 0))],
            [new(90, 10, 2, 20, 1)], profile, policy, scheduler: scheduler).Complete().Scene;
        var entity = world.CreateEntity(new Motion(new(1, 0, 0.3f), default, 0));
        world.Add(entity, new NavigationAgent(profile, policy, 1));
        world.Add(entity, new PathNavigation<NavigationScene>(scene));
        world.Add(entity, new NavigationGuidance(new(1, 0, -0.3f), 0, 1, true));
        world.Add(entity, default(NavigationSteering));
        NavigationComponents.AddAvoidance(world, entity, new(0.1f, Spacing: 0.05f));
        var neighbors = new NavigationNeighborSystem<Motion, MotionReader>(world, scheduler);
        var avoidance = new NavigationAvoidanceSystem<OrcaAvoidance, Motion, MotionReader>(world, neighbors);
        for (int frame = 0; frame < 160; frame++)
        {
            avoidance.Schedule(0.1f, default, neighbors.Schedule(), avoidance.SchedulePathBoundaries<NavigationScene>()).Complete();
            Motion motion = world.Read<Motion>(entity);
            NavigationSteering steering = world.Read<NavigationSteering>(entity);
            world.Replace(entity, new Motion(motion.Position + steering.Velocity * 0.1f, steering.Velocity, steering.Heading));
        }
        Vector3 position = world.Read<Motion>(entity).Position;
        Assert.InRange(position.X, 14f, 15.86f);
        Assert.InRange(position.Z, 0.149f, 0.16f);
    }

    private readonly record struct Motion(Vector3 Position, Vector3 Velocity, float Heading) : IComponent;
    private readonly struct MotionReader : INavigationMotionReader<Motion>
    {
        public static NavigationMotion Read(in Motion value) => new(value.Position, value.Velocity, value.Heading, true);
    }
}
