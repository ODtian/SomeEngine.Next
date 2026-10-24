using System.Numerics;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;

namespace SomeEngine.Navigation.Tests;

public sealed class PolyanyaPathTests
{
    [Fact]
    public void SelectedPolyanyaSearchDrivesIndependentAgentsAlongGeometricShortestPaths()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig
        {
            WorkerCount = 4,
            ManagedPayloadPolicy = ManagedPayloadPolicy.Allow,
        });
        using World world = WorldJobs.CreateWorld(scheduler: scheduler);
        var navigation = new NavigationSystems<NavigationMesh, PolyanyaNavigationSearch,
            PolyanyaSearchState, TestMotion, TestMotionReader>(world);
        var profile = new NavigationProfile(
            [new(-0.2f, -0.2f), new(0.2f, -0.2f), new(0.2f, 0.2f), new(-0.2f, 0.2f)],
            1.8f, maximumSpeed: 2f);
        NavigationMesh mesh = TwoRoutesAroundAnObstacle();
        Entity eastbound = world.CreateEntity(new TestMotion { Position = new(1, 0, 5) });
        Entity westbound = world.CreateEntity(new TestMotion { Position = new(9, 0, 5) });
        navigation.AddAgent(eastbound, mesh, profile, new NavigationPolicy(),
            new NavigationGoal(new Vector3(9, 0, 5), 0.05f), desiredSpeed: 2f);
        navigation.AddAgent(westbound, mesh, profile, new NavigationPolicy(),
            new NavigationGoal(new Vector3(1, 0, 5), 0.05f), desiredSpeed: 2f);

        NavigationStep first = navigation.Schedule(0.1f, expansionBudget: 1);
        navigation.ScheduleProgress(new ConsumeSteering(0.1f).ScheduleParallel(world, first.Steering)).Complete();
        Assert.Equal(NavigationSearchStatus.Searching, world.Read<NavigationPathResult>(eastbound).Status);
        Assert.Equal(NavigationSearchStatus.Searching, world.Read<NavigationPathResult>(westbound).Status);

        for (int step = 0; step < 100 &&
             (world.Read<NavigationProgress>(eastbound).Status != NavigationStatus.Arrived ||
              world.Read<NavigationProgress>(westbound).Status != NavigationStatus.Arrived); step++)
        {
            NavigationStep scheduled = navigation.Schedule(0.1f, expansionBudget: 1);
            JobHandle moved = new ConsumeSteering(0.1f).ScheduleParallel(world, scheduled.Steering);
            navigation.ScheduleProgress(moved).Complete();
        }

        // This map is an example: the upper obstacle corners give 2 + 2*sqrt(10) metres.
        float shortestLength = 2 + 2 * MathF.Sqrt(10);
        foreach (Entity entity in new[] { eastbound, westbound })
        {
            NavigationPathResult result = world.Read<NavigationPathResult>(entity);
            Assert.Equal(NavigationSearchStatus.Found, result.Status);
            NavigationPath path = Assert.IsType<NavigationPath>(result.Path);
            float length = 0;
            for (int corner = 1; corner < path.Corners.Length; corner++)
                length += Vector3.Distance(path.Corners.Span[corner - 1], path.Corners.Span[corner]);
            Assert.InRange(length, shortestLength - 0.001f, shortestLength + 0.001f);
            Assert.InRange(path.Cost, shortestLength - 0.001f, shortestLength + 0.001f);
            Assert.Equal(NavigationStatus.Arrived, world.Read<NavigationProgress>(entity).Status);
        }
    }

    // The broad upper portals and narrow lower portals make this example's shorter geometry
    // distinct from the route favored by portal-center distances.
    private static NavigationMesh TwoRoutesAroundAnObstacle() => new(
        [new(0, 0, 0), new(4, 0, 0), new(4, 0, 20), new(0, 0, 20),
         new(4, 0, 6), new(6, 0, 6), new(6, 0, 20), new(4, 0, 20),
         new(6, 0, 0), new(10, 0, 0), new(10, 0, 20), new(6, 0, 20),
         new(4, 0, 0), new(6, 0, 0), new(6, 0, 2), new(4, 0, 2)],
        [0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15],
        [new(0, 4, 0, 2), new(4, 4, 2, 2), new(8, 4, 4, 2), new(12, 4, 6, 2)],
        [new(1, new(4, 0, 20), new(4, 0, 6)), new(3, new(4, 0, 2), new(4, 0, 0)),
         new(0, new(4, 0, 6), new(4, 0, 20)), new(2, new(6, 0, 20), new(6, 0, 6)),
         new(1, new(6, 0, 6), new(6, 0, 20)), new(3, new(6, 0, 0), new(6, 0, 2)),
         new(0, new(4, 0, 0), new(4, 0, 2)), new(2, new(6, 0, 2), new(6, 0, 0))]);
}
