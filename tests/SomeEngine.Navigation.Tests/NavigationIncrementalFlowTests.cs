using System.Numerics;
using SomeEngine.ECS;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;

namespace SomeEngine.Navigation.Tests;

public sealed class NavigationIncrementalFlowTests
{
    [Fact]
    public void CompletedSourceChangesUpdateSharedGridCostsAndMoveFollowersAroundTheNewObstacle()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 3 });
        using World world = WorldJobs.CreateWorld(scheduler: scheduler);
        var profile = new NavigationProfile([new(-0.1f, -0.1f), new(0.1f, -0.1f), new(0.1f, 0.1f), new(-0.1f, 0.1f)], 1);
        var scene = new NavigationScene(new(default, default, default));
        var region = new NavigationRegionSources(scene, 3, new(new(0, -1, 0), new(12, 4, 6)), profile,
            new NavigationPolicy(), new() { CellSize = 0.5f, CellHeight = 0.1f, TileSize = 8 });
        var floor = world.CreateEntity(new NavigationSource(new NavigationGeometry(
            [new(-1, 0, -1), new(-1, 0, 7), new(13, 0, 7), new(13, 0, -1)], [new(0, 1, 2, 0), new(0, 2, 3, 0)])));
        world.Add(floor, new SourcePlacement(Matrix4x4.Identity));
        var sources = new NavigationSourceSystem<SourcePlacement, SourcePlacementReader>(world, region, scheduler);
        var gridUpdates = new NavigationGridBuildSystem(region, scheduler: scheduler);
        NavigationBuild<NavigationRegionUpdate> initial = NavigationBuildSystem.Schedule(sources.Schedule(), scheduler);
        NavigationGridUpdate first = gridUpdates.Schedule(initial, NavigationApplySystem.Schedule(initial, scheduler)).Complete();
        var flow = new NavigationFlow(first.Costs, profile, [new(new(10.25f, 0, 1.25f), 0.05f)]);
        var other = new NavigationFlow(first.Costs, profile, [new(new(10.25f, 0, 3.25f), 0.05f)]);
        var solver = new NavigationFlowSystem<ReverseDijkstraFlow, ReverseDijkstraFlowState>(flow, scheduler);
        ReverseDijkstraFlowState retained = solver.Schedule().Complete();
        var navigation = new NavigationSystems<Motion, MotionReader>(world);
        var actor = world.CreateEntity(new Motion(new(1.25f, 0, 1.25f), default, 0));
        navigation.AddFlowAgent(actor, flow, 2);

        // This box is an example source change that closes the lower part of the middle tile.
        var obstacle = world.CreateEntity(new NavigationObstacle(new NavigationGeometry(
            [new(5, 0, -1), new(5, 0, 2.5f), new(6, 0, 2.5f), new(6, 0, -1),
             new(5, 3, -1), new(5, 3, 2.5f), new(6, 3, 2.5f), new(6, 3, -1)],
            [new(0, 2, 1, 0), new(0, 3, 2, 0), new(4, 5, 6, 0), new(4, 6, 7, 0),
             new(0, 4, 7, 0), new(0, 7, 3, 0), new(1, 2, 6, 0), new(1, 6, 5, 0),
             new(0, 1, 5, 0), new(0, 5, 4, 0), new(3, 7, 6, 0), new(3, 6, 2, 0)])));
        world.Add(obstacle, new SourcePlacement(Matrix4x4.Identity));
        NavigationBuild<NavigationRegionUpdate> rebuilt = NavigationBuildSystem.Schedule(sources.Schedule(), scheduler);
        NavigationBuild<NavigationGridUpdate> grid = gridUpdates.Schedule(rebuilt, NavigationApplySystem.Schedule(rebuilt, scheduler));
        JobHandle applied = NavigationGridApplySystem.Schedule(grid, new[] { flow, other }, scheduler);
        NavigationBuild<ReverseDijkstraFlowState> repaired = solver.Schedule(dependency: applied);
        var positions = new List<Vector3>();
        for (int frame = 0; frame < 120; frame++)
        {
            navigation.Schedule(0.1f, repaired.Completion).Steering.Complete();
            Motion motion = world.Read<Motion>(actor);
            NavigationSteering steering = world.Read<NavigationSteering>(actor);
            Vector3 position = motion.Position + steering.Velocity * 0.1f;
            world.Replace(actor, new Motion(position, steering.Velocity, steering.Heading));
            positions.Add(position);
            navigation.ScheduleProgress(default).Complete();
            if (world.Read<NavigationProgress>(actor).Status == NavigationStatus.Arrived) break;
        }
        Assert.Same(retained, repaired.State);
        Assert.Same(flow.Costs, other.Costs);
        Assert.Equal(rebuilt.State.Input.Version, flow.Field!.Input.Costs.Grid.ContentVersion);
        Assert.Equal(first.Grid.ResidenceVersion, flow.Costs.Grid.ResidenceVersion);
        Assert.Same(first.Grid.Tiles.Span[2].Grid, flow.Costs.Grid.Tiles.Span[2].Grid);
        Assert.Contains(positions, position => position.X > 5 && position.X < 6 && position.Z > 2.5f);
        Assert.Equal(NavigationStatus.Arrived, world.Read<NavigationProgress>(actor).Status);
    }

    [Fact]
    public void EikonalFollowersRepairBothUpwindDirectionsAfterCostAndGoalChanges()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 3 });
        using World world = WorldJobs.CreateWorld(scheduler: scheduler);
        var profile = new NavigationProfile([new(-0.1f, -0.1f), new(0.1f, -0.1f), new(0.1f, 0.1f), new(-0.1f, 0.1f)], 1);
        var policy = new NavigationPolicy();
        NavigationGrid grid = NavigationGridRegionBuilder.Schedule(OpenArea(), profile, policy, scheduler: scheduler).Complete().Grid;
        var flow = new NavigationFlow(NavigationCostSystem.Schedule(grid, policy, scheduler: scheduler).Complete().Costs,
            profile, [new(new(8.5f, 0, 1.5f), 0.05f)]);
        var solver = new NavigationFlowSystem<FastIterativeFlow, FastIterativeFlowState>(flow, scheduler);
        FastIterativeFlowState retained = solver.Schedule().Complete();
        NavigationFlowField previous = flow.Field!;
        Vector3 probe = new(0.5f, 0, 1.5f);
        Assert.Equal(8f, previous.CumulativeCosts.Span[grid.FindCell(probe)], 4);
        var navigation = new NavigationSystems<Motion, MotionReader>(world);
        var actor = world.CreateEntity(new Motion(probe, default, 0));
        navigation.AddFlowAgent(actor, flow, 2);
        MoveOneFrame();

        flow.SetCosts(NavigationCostSystem.Schedule(grid, new NavigationPolicy([new(7, 10)]),
            version: 2, scheduler: scheduler).Complete().Costs);
        Assert.Same(retained, solver.Schedule(0).Complete());
        Assert.Contains(retained.TileStates.ToArray(), tile => tile.Status == NavigationFlowStatus.Searching);
        solver.Schedule().Complete();
        Assert.True(flow.Field!.SampleDirection(probe).Z > 0.1f);
        Assert.True(flow.Field.CumulativeCosts.Span[grid.FindCell(probe)] > 8);
        Assert.Equal(8f, previous.CumulativeCosts.Span[grid.FindCell(probe)], 4);
        var reference = new NavigationFlow(flow.Input);
        new NavigationFlowSystem<FastIterativeFlow, FastIterativeFlowState>(reference, scheduler).Schedule().Complete();
        foreach (Vector3 position in new Vector3[] { probe, new(2.5f, 0, 3.5f), new(5.5f, 0, 5.5f) })
        {
            int cell = grid.FindCell(position);
            Assert.Equal(reference.Field!.CumulativeCosts.Span[cell], flow.Field.CumulativeCosts.Span[cell], 4);
        }
        MoveOneFrame();

        flow.SetCosts(NavigationCostSystem.Schedule(grid, policy, version: 3, scheduler: scheduler).Complete().Costs);
        Assert.Same(retained, solver.Schedule().Complete());
        Assert.Equal(8f, flow.Field!.CumulativeCosts.Span[grid.FindCell(probe)], 4);
        MoveOneFrame();
        flow.SetGoals([new(new(0.5f, 0, 5.5f), 0.05f)]);
        Assert.Same(retained, solver.Schedule().Complete());
        for (int frame = 0; frame < 120; frame++)
        {
            MoveOneFrame();
            if (world.Read<NavigationProgress>(actor).Status == NavigationStatus.Arrived) break;
        }
        Assert.Equal(NavigationStatus.Arrived, world.Read<NavigationProgress>(actor).Status);
        Assert.Equal(flow.Input.GoalVersion, flow.Field!.Input.GoalVersion);
        Assert.All(retained.TileStates.ToArray(), tile => Assert.Equal(NavigationFlowStatus.Ready, tile.Status));

        void MoveOneFrame()
        {
            navigation.Schedule(0.1f).Steering.Complete();
            Motion motion = world.Read<Motion>(actor);
            NavigationSteering steering = world.Read<NavigationSteering>(actor);
            world.Replace(actor, new Motion(motion.Position + steering.Velocity * 0.1f, steering.Velocity, steering.Heading));
            navigation.ScheduleProgress(default).Complete();
        }
    }

    [Fact]
    public void SharedDijkstraFollowersUseRaisedThenLoweredChannelCostsAndReachTheirChangedGoal()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 3 });
        using World world = WorldJobs.CreateWorld(scheduler: scheduler);
        var profile = new NavigationProfile([new(-0.1f, -0.1f), new(0.1f, -0.1f), new(0.1f, 0.1f), new(-0.1f, 0.1f)], 1);
        var policy = new NavigationPolicy([new(7, 1), new(8, 2)]);
        NavigationGrid grid = NavigationGridRegionBuilder.Schedule(TwoChannels(), profile, policy, scheduler: scheduler).Complete().Grid;
        NavigationCosts costs = NavigationCostSystem.Schedule(grid, policy, scheduler: scheduler).Complete().Costs;
        var flow = new NavigationFlow(costs, profile, [new(new(10.5f, 0, 1.5f), 0.05f)]);
        var other = new NavigationFlow(costs, profile, [new(new(11.5f, 0, 1.5f), 0.05f)]);
        var solver = new NavigationFlowSystem<ReverseDijkstraFlow, ReverseDijkstraFlowState>(flow, scheduler);
        ReverseDijkstraFlowState retained = solver.Schedule().Complete();
        var navigation = new NavigationSystems<Motion, MotionReader>(world);
        var actors = new[] { world.CreateEntity(new Motion(new(1.5f, 0, 1.4f), default, 0)),
            world.CreateEntity(new Motion(new(1.5f, 0, 1.6f), default, 0)) };
        foreach (var actor in actors) navigation.AddFlowAgent(actor, flow, 2);
        Vector3 fork = new(1.5f, 0, 1.5f);
        NavigationFlowField previous = flow.Field!;
        float previousCost = previous.CumulativeCosts.Span[grid.FindCell(fork)];
        Assert.True(previous.SampleDirection(fork).Z < 0);
        MoveOneFrame();

        NavigationCosts raised = NavigationCostSystem.Schedule(grid, new NavigationPolicy([new(7, 10), new(8, 2)]),
            version: 2, scheduler: scheduler).Complete().Costs;
        flow.SetCosts(raised);
        other.SetCosts(raised);
        Assert.Same(retained, solver.Schedule(0).Complete());
        Assert.Equal(NavigationFlowStatus.Searching, retained.Status);
        MoveOneFrame();
        Assert.All(actors, actor => Assert.Equal(NavigationStatus.Searching, world.Read<NavigationProgress>(actor).Status));
        solver.Schedule().Complete();
        Assert.Same(flow.Costs, other.Costs);
        Assert.True(flow.Field!.SampleDirection(fork).Z > 0);
        Assert.Equal(previousCost, previous.CumulativeCosts.Span[grid.FindCell(fork)]);
        MoveOneFrame();

        flow.SetCosts(NavigationCostSystem.Schedule(grid, new NavigationPolicy([new(7, 0.5f), new(8, 2)]),
            version: 3, scheduler: scheduler).Complete().Costs);
        Assert.Same(retained, solver.Schedule().Complete());
        Assert.True(flow.Field!.SampleDirection(fork).Z < 0);
        MoveOneFrame();

        flow.SetGoals([new(new(0.5f, 0, 1.5f), 0.05f)]);
        Assert.Same(retained, solver.Schedule().Complete());
        for (int frame = 0; frame < 80; frame++)
        {
            MoveOneFrame();
            if (actors.All(actor => world.Read<NavigationProgress>(actor).Status == NavigationStatus.Arrived)) break;
        }
        Assert.All(actors, actor => Assert.Equal(NavigationStatus.Arrived, world.Read<NavigationProgress>(actor).Status));
        Assert.Equal(flow.Input.GoalVersion, flow.Field!.Input.GoalVersion);
        Assert.Equal(flow.Input.Version, world.Read<NavigationGuidance>(actors[0]).RequestVersion);

        void MoveOneFrame()
        {
            navigation.Schedule(0.1f).Steering.Complete();
            foreach (var actor in actors)
            {
                Motion motion = world.Read<Motion>(actor);
                NavigationSteering steering = world.Read<NavigationSteering>(actor);
                world.Replace(actor, new Motion(motion.Position + steering.Velocity * 0.1f, steering.Velocity, steering.Heading));
            }
            navigation.ScheduleProgress(default).Complete();
        }
    }

    // The two channels are an example with three tiles and independently priced upper and lower areas.
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
                Area: z == 0 ? 7 : z == 2 ? 8 : 0, Tile: new(x / 4, 0)));
        }
        return new(new(new(0, 0, 0), new(12, 1, 3)), 1, cells.ToArray(), edges.ToArray());
    }

    // This open area is an example where a priced patch changes both axes of the Eikonal gradient.
    private static NavigationGrid OpenArea()
    {
        var cells = new List<NavigationGridCell>();
        var edges = new List<NavigationGridEdge>();
        for (int x = 0; x < 9; x++)
        for (int z = 0; z < 7; z++)
        {
            int first = edges.Count;
            if (x > 0) edges.Add(new((x - 1) * 7 + z, 1));
            if (x < 8) edges.Add(new((x + 1) * 7 + z, 1));
            if (z > 0) edges.Add(new(x * 7 + z - 1, 1));
            if (z < 6) edges.Add(new(x * 7 + z + 1, 1));
            cells.Add(new(new(x + 0.5f, 0, z + 0.5f), first, edges.Count - first,
                Area: x >= 3 && x <= 5 && z < 5 ? 7 : 0, Tile: new(x / 3, 0)));
        }
        return new(new(new(0, 0, 0), new(9, 1, 7)), 1, cells.ToArray(), edges.ToArray());
    }

    private readonly record struct Motion(Vector3 Position, Vector3 Velocity, float Heading) : IComponent;
    private readonly struct MotionReader : INavigationMotionReader<Motion>
    {
        public static NavigationMotion Read(in Motion value) => new(value.Position, value.Velocity, value.Heading, true);
    }
}
