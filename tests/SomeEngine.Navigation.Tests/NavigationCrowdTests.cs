using System.Numerics;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;

namespace SomeEngine.Navigation.Tests;

public sealed class NavigationCrowdTests
{
    [Fact]
    public void PolicyScalarFeedbackGuidesEikonalFollowersThroughTheAvailableChannel()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 3 });
        using World world = WorldJobs.CreateWorld(scheduler: scheduler);
        var profile = new NavigationProfile([new(-0.1f, -0.1f), new(0.1f, -0.1f), new(0.1f, 0.1f), new(-0.1f, 0.1f)], 1);
        var policy = new NavigationPolicy([new(8, 1.5f)], crowd: new NavigationCrowdPolicy
        {
            Sampling = NavigationCrowdCostSampling.Scalar, FreeSpeed = 2, MinimumSpeed = 0.1f,
            LowDensity = 0.25f, HighDensity = 0.75f, DiscomfortWeight = 2,
        });
        NavigationGrid grid = NavigationGridRegionBuilder.Schedule(TwoChannels(), profile, policy, scheduler: scheduler).Complete().Grid;
        NavigationCosts baseCosts = NavigationCostSystem.Schedule(grid, policy, scheduler: scheduler).Complete().Costs;
        var flow = new NavigationFlow(baseCosts, profile, [new(new(11.5f, 0, 1.5f), 0.25f)]);
        var solver = new NavigationFlowSystem<FastIterativeFlow, FastIterativeFlowState>(flow, scheduler);
        FastIterativeFlowState retained = solver.Schedule().Complete();
        var navigation = new NavigationSystems<TestMotion, TestMotionReader>(world);
        var crowd = new NavigationCrowdSystem<TestMotion, TestMotionReader>(world, scheduler);
        for (int x = 2; x < 10; x++)
        {
            Entity actor = world.CreateEntity(new TestMotion { Position = new(x + 0.5f, 0, 0.5f) });
            navigation.AddFlowAgent(actor, flow, 0.25f);
            world.Add(actor, new NavigationCrowdAgent(1));
        }
        Vector3 fork = new(1.5f, 0, 1.5f);
        Assert.True(flow.Field!.SampleDirection(fork).Z < 0);
        JobHandle moved = new ConsumeSteering(0.1f).ScheduleParallel(world, navigation.Schedule(0.1f).Steering);
        NavigationBuild<NavigationCrowdBuildState> measured = crowd.Schedule(grid, moved);
        NavigationBuild<NavigationCostBuildState> adjusted = NavigationCostSystem.Schedule(baseCosts, measured, scheduler: scheduler);
        JobHandle applied = NavigationCostSystem.ScheduleApply(adjusted, new[] { flow }, scheduler);
        Assert.Same(retained, solver.Schedule(dependency: applied).Complete());
        Assert.True(flow.Field!.SampleDirection(fork).Z > 0);
        Assert.True(flow.Costs.CellCosts.Span[grid.FindCell(new(5.5f, 0, 0.5f))] >
            flow.Costs.CellCosts.Span[grid.FindCell(new(5.5f, 0, 2.5f))]);
        Entity fresh = world.CreateEntity(new TestMotion { Position = fork });
        navigation.AddFlowAgent(fresh, flow, 2);
        world.Add(fresh, new NavigationCrowdAgent(1));
        var positions = new List<Vector3>();
        for (int frame = 0; frame < 100; frame++)
        {
            moved = new ConsumeSteering(0.1f).ScheduleParallel(world, navigation.Schedule(0.1f).Steering);
            measured = crowd.Schedule(grid, moved);
            adjusted = NavigationCostSystem.Schedule(baseCosts, measured, scheduler: scheduler);
            applied = NavigationCostSystem.ScheduleApply(adjusted, new[] { flow }, scheduler);
            JobHandle updated = solver.Schedule(dependency: applied).Completion;
            navigation.ScheduleProgress(scheduler.CombineDependencies([moved, updated])).Complete();
            positions.Add(world.Read<TestMotion>(fresh).Position);
            if (world.Read<NavigationProgress>(fresh).Status == NavigationStatus.Arrived) break;
        }
        Assert.Contains(positions, position => position.X > 4 && position.X < 9 && position.Z > 2);
        Assert.Equal(NavigationStatus.Arrived, world.Read<NavigationProgress>(fresh).Status);
    }

    [Fact]
    public void ActualSlowTrafficRaisesSharedChannelCostsAndNewFollowersReachTheGoalThroughTheOtherChannel()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 3 });
        using World world = WorldJobs.CreateWorld(scheduler: scheduler);
        var profile = new NavigationProfile([new(-0.1f, -0.1f), new(0.1f, -0.1f), new(0.1f, 0.1f), new(-0.1f, 0.1f)], 1);
        var policy = new NavigationPolicy([new(8, 1.5f)]);
        NavigationGrid grid = NavigationGridRegionBuilder.Schedule(TwoChannels(), profile, policy, scheduler: scheduler).Complete().Grid;
        NavigationCosts baseCosts = NavigationCostSystem.Schedule(grid, policy, scheduler: scheduler).Complete().Costs;
        var flow = new NavigationFlow(baseCosts, profile, [new(new(11.5f, 0, 1.5f), 0.25f)]);
        var other = new NavigationFlow(baseCosts, profile, [new(new(0.5f, 0, 1.5f), 0.25f)]);
        var solver = new NavigationFlowSystem<ReverseDijkstraFlow, ReverseDijkstraFlowState>(flow, scheduler);
        ReverseDijkstraFlowState retained = solver.Schedule().Complete();
        var navigation = new NavigationSystems<TestMotion, TestMotionReader>(world);
        var neighbors = new NavigationNeighborSystem<TestMotion, TestMotionReader>(world, scheduler);
        var avoidance = new NavigationAvoidanceSystem<OrcaAvoidance, TestMotion, TestMotionReader>(world, neighbors);
        var crowd = new NavigationCrowdSystem<TestMotion, TestMotionReader>(world, scheduler);
        var crowdPolicy = new NavigationCrowdPolicy
        {
            Sampling = NavigationCrowdCostSampling.Directional,
            FreeSpeed = 2, MinimumSpeed = 0.1f, LowDensity = 0.25f, HighDensity = 0.75f,
            DistanceWeight = 1, TimeWeight = 1, DiscomfortWeight = 2,
        };
        var traffic = new List<Entity>();
        for (int x = 2; x < 10; x++)
        {
            Entity actor = world.CreateEntity(new TestMotion { Position = new(x + 0.5f, 0, 0.5f) });
            navigation.AddFlowAgent(actor, flow, 0.25f);
            NavigationComponents.AddAvoidance(world, actor, new(0.15f, NeighborDistance: 1, MaximumNeighbors: 4));
            world.Add(actor, new NavigationCrowdAgent(1));
            traffic.Add(actor);
        }
        Vector3 fork = new(1.5f, 0, 1.5f);
        Assert.True(flow.Field!.SampleDirection(fork).Z < 0);

        JobHandle guidance = navigation.ScheduleGuidance(0.1f).Guidance;
        JobHandle steering = avoidance.Schedule(0.1f, guidance, neighbors.Schedule(), avoidance.ScheduleFlowBoundaries());
        JobHandle moved = new ConsumeSteering(0.1f).ScheduleParallel(world, steering);
        NavigationBuild<NavigationCrowdBuildState> measured = crowd.Schedule(grid, moved);
        NavigationBuild<NavigationCostBuildState> adjusted = NavigationCostSystem.Schedule(baseCosts, measured, crowdPolicy, scheduler: scheduler);
        JobHandle applied = NavigationCostSystem.ScheduleApply(adjusted, new[] { flow, other }, scheduler);
        Assert.Same(retained, solver.Schedule(dependency: applied).Complete());

        NavigationCrowdField first = measured.State.Field;
        NavigationCrowdCell sample = first.Sample(new(5.5f, 0, 0.5f));
        Assert.InRange(sample.Density, 0.9f, 1.1f);
        float actualSpeed = world.Read<TestMotion>(traffic[3]).Velocity.X;
        Assert.InRange(actualSpeed, 0.17f, 0.18f);
        Assert.Equal(actualSpeed, sample.AverageVelocity.X, 4);
        Assert.Equal(0f, first.Sample(new(5.5f, 0, 2.5f)).Density);
        Assert.True(flow.Field!.SampleDirection(fork).Z > 0);
        Assert.Same(flow.Costs, other.Costs);
        Entity fresh = world.CreateEntity(new TestMotion { Position = fork });
        navigation.AddFlowAgent(fresh, flow, 2);
        NavigationComponents.AddAvoidance(world, fresh, new(0.15f, NeighborDistance: 1, MaximumNeighbors: 4));
        world.Add(fresh, new NavigationCrowdAgent(1));
        var positions = new List<Vector3>();
        for (int frame = 0; frame < 100; frame++)
        {
            guidance = navigation.ScheduleGuidance(0.1f).Guidance;
            steering = avoidance.Schedule(0.1f, guidance, neighbors.Schedule(), avoidance.ScheduleFlowBoundaries());
            moved = new ConsumeSteering(0.1f).ScheduleParallel(world, steering);
            measured = crowd.Schedule(grid, moved);
            adjusted = NavigationCostSystem.Schedule(baseCosts, measured, crowdPolicy, scheduler: scheduler);
            applied = NavigationCostSystem.ScheduleApply(adjusted, new[] { flow, other }, scheduler);
            JobHandle updated = solver.Schedule(dependency: applied).Completion;
            navigation.ScheduleProgress(scheduler.CombineDependencies([moved, updated])).Complete();
            positions.Add(world.Read<TestMotion>(fresh).Position);
            if (world.Read<NavigationProgress>(fresh).Status == NavigationStatus.Arrived) break;
        }
        Assert.Contains(positions, position => position.X > 4 && position.X < 9 && position.Z > 2);
        Assert.Equal(NavigationStatus.Arrived, world.Read<NavigationProgress>(fresh).Status);
        Assert.Same(world.Read<FlowFollower>(traffic[0]).Flow, world.Read<FlowFollower>(fresh).Flow);
        Assert.Equal(actualSpeed, first.Sample(new(5.5f, 0, 0.5f)).AverageVelocity.X, 4);
    }

    // This pair of channels is an example with three independently processed source tiles.
    private static NavigationGrid TwoChannels()
    {
        var points = new List<(int X, int Z)>();
        for (int x = 0; x < 12; x++)
        for (int z = 0; z < 3; z++)
            if (z != 1 || x < 2 || x >= 10) points.Add((x, z));
        var indices = points.Select((point, index) => (point, index)).ToDictionary(item => item.point, item => item.index);
        var cells = new List<NavigationGridCell>();
        var edges = new List<NavigationGridEdge>();
        foreach ((int x, int z) in points)
        {
            int first = edges.Count;
            foreach ((int dx, int dz) in new (int, int)[] { (-1, 0), (1, 0), (0, -1), (0, 1) })
                if (indices.TryGetValue((x + dx, z + dz), out int neighbor)) edges.Add(new(neighbor, 1));
            cells.Add(new(new(x + 0.5f, 0, z + 0.5f), first, edges.Count - first,
                Area: z == 2 ? 8 : 0, Tile: new(x / 4, 0)));
        }
        return new(new(new(0, 0, 0), new(12, 1, 3)), 1, cells.ToArray(), edges.ToArray());
    }
}
