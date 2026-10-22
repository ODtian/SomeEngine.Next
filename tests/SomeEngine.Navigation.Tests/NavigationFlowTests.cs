using System.Numerics;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;

namespace SomeEngine.Navigation.Tests;

public sealed class NavigationFlowTests
{
    [Fact]
    public void BudgetedDijkstraFollowsDirectedCostsAcrossLayersIntoTheGoalRegion()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 2, ManagedPayloadPolicy = ManagedPayloadPolicy.Allow });
        NavigationGrid grid = DirectedGrid();
        var profile = new NavigationProfile([new Vector2(-0.1f), new(0.1f, -0.1f), new(0.1f), new(-0.1f, 0.1f)], 1);
        var policy = new NavigationPolicy([new(7, 10)], [new(5, 3)]);
        NavigationCosts costs = NavigationCostSystem.Schedule(grid, policy, scheduler: scheduler).Complete().Costs;
        var flow = new NavigationFlow(costs, profile, [new(new(2.5f, 2, 1), 0.51f)]);
        var solver = new NavigationFlowSystem<ReverseDijkstraFlow, ReverseDijkstraFlowState>(flow, scheduler);

        NavigationFlowBuildState first = solver.Schedule(1).Complete();
        NavigationFlowStatus firstStatus = first.Status;
        NavigationFlowBuildState completed = solver.Schedule(32).Complete();

        Assert.Same(first, completed);
        Assert.Equal(NavigationFlowStatus.Searching, firstStatus);
        Assert.Equal(6f, flow.Field!.CumulativeCosts.Span[0]);
        Assert.Equal(1, flow.Field.SuccessorEdges.Span[0]);
        Assert.Equal(0, flow.Field.CumulativeCosts.Span[3]);
        Assert.Equal(0, flow.Field.CumulativeCosts.Span[5]);
        Assert.True(float.IsPositiveInfinity(flow.Field.CumulativeCosts.Span[4]));
    }

    [Fact]
    public void SurfaceGridFollowersShareOneFieldAndReachItsGoalFromDifferentLayers()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 3, ManagedPayloadPolicy = ManagedPayloadPolicy.Allow });
        using World world = WorldJobs.CreateWorld(scheduler: scheduler);
        var profile = new NavigationProfile([new Vector2(-0.1f), new(0.1f, -0.1f), new(0.1f), new(-0.1f, 0.1f)],
            0.8f, maximumStepHeight: 1.1f, maximumSpeed: 2);
        NavigationGrid grid = NavigationGridBuilder.Schedule(LayeredSurface(), profile,
            [new(new(0.5f, 3, 1.5f), new(1.5f, 0, 1.5f), 2, Action: 4)], scheduler: scheduler).Complete().Grid;
        NavigationCosts costs = NavigationCostSystem.Schedule(grid, new NavigationPolicy(), scheduler: scheduler).Complete().Costs;
        var flow = new NavigationFlow(costs, profile, [new(new(3.5f, 1, 0.5f), 0.2f)]);
        new NavigationFlowSystem<ReverseDijkstraFlow, ReverseDijkstraFlowState>(flow, scheduler).Schedule().Complete();
        var navigation = new NavigationSystems<TestMotion, TestMotionReader>(world);
        Entity lower = world.CreateEntity(new TestMotion { Position = new(0.5f, 0, 0.5f) });
        Entity upper = world.CreateEntity(new TestMotion { Position = new(0.5f, 3, 1.5f) });
        navigation.AddFlowAgent(lower, flow, desiredSpeed: 2);
        navigation.AddFlowAgent(upper, flow, desiredSpeed: 2);

        for (int step = 0; step < 100; step++)
        {
            NavigationFlowStep scheduled = navigation.Schedule(0.1f);
            JobHandle moved = new ConsumeSteering(0.1f).ScheduleParallel(world, scheduled.Steering);
            navigation.ScheduleProgress(moved).Complete();
            if (world.Read<NavigationProgress>(lower).Status == NavigationStatus.Arrived &&
                world.Read<NavigationProgress>(upper).Status == NavigationStatus.Arrived)
                break;
        }

        Assert.Same(world.Read<FlowFollower>(lower).Flow, world.Read<FlowFollower>(upper).Flow);
        Assert.Equal(NavigationStatus.Arrived, world.Read<NavigationProgress>(lower).Status);
        Assert.Equal(NavigationStatus.Arrived, world.Read<NavigationProgress>(upper).Status);
    }

    // These two support layers and stair heights are an example supplied through the public surface model.
    private static NavigationSurface LayeredSurface()
    {
        var columns = new List<NavigationSurfaceColumn>();
        var spans = new List<NavigationSurfaceSpan>();
        for (int z = 0; z < 2; z++)
        for (int x = 0; x < 4; x++)
        {
            int start = spans.Count;
            float height = x >= 2 ? 1 : 0;
            spans.Add(new(height - 0.1f, height, height, Vector3.UnitY, 0));
            if (x == 0 && z == 1)
                spans.Add(new(2.9f, 3, 3, Vector3.UnitY, 0));
            columns.Add(new(start, spans.Count - start));
        }
        var bounds = new NavigationBounds(new(0, -1, 0), new(4, 4, 2));
        var tile = new NavigationSurfaceTile(default, bounds, Vector2.Zero, 1, 4, 2, columns.ToArray(), spans.ToArray());
        return new(bounds, new() { CellSize = 1, TileSize = 4 }, new[] { tile });
    }

    // The six cells are an example with a costly lower route and one directed upper connection.
    private static NavigationGrid DirectedGrid() => new(
        new(new(0, 0, 0), new(3, 3, 2)), 1,
        [new(new(0.5f, 0, 0.5f), 0, 2),
         new(new(0.5f, 0, 1.5f), 2, 1, Area: 7),
         new(new(1.5f, 2, 0.5f), 3, 1, Layer: 1),
         new(new(2.5f, 2, 0.5f), 4, 1, Layer: 1),
         new(new(2.5f, 0, 0.5f), 5, 0),
         new(new(2.5f, 2, 1.5f), 5, 0, Layer: 1)],
        [new(1, 1), new(2, 1), new(3, 1), new(3, 2, RequiredCapabilities: 2, Action: 5), new(4, 1)]);
}
