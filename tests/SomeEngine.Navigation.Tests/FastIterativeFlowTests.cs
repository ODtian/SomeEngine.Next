using System.Numerics;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;

namespace SomeEngine.Navigation.Tests;

public sealed class FastIterativeFlowTests
{
    [Fact]
    public void BudgetedIsotropicFieldGuidesAlongTheGeometricDirectionInUniformTerrain()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 3, ManagedPayloadPolicy = ManagedPayloadPolicy.Allow });
        using World world = WorldJobs.CreateWorld(scheduler: scheduler);
        NavigationGrid grid = OpenGrid(24, 20, 0.5f);
        var profile = new NavigationProfile([new Vector2(-0.1f), new(0.1f, -0.1f), new(0.1f), new(-0.1f, 0.1f)], 1);
        NavigationCosts costs = NavigationCostSystem.Schedule(grid, new NavigationPolicy(), scheduler: scheduler).Complete().Costs;
        Vector3 goal = new(10.25f, 0, 8.25f), start = new(2.37f, 0, 1.64f);
        var flow = new NavigationFlow(costs, profile, [new(goal, 0.15f)]);
        var integration = new NavigationFlowSystem<FastIterativeFlow, FastIterativeFlowState>(flow, scheduler);

        FastIterativeFlowState first = integration.Schedule(3).Complete();
        NavigationFlowStatus firstStatus = first.Status;
        FastIterativeFlowState completed = integration.Schedule().Complete();
        var navigation = new NavigationSystems<TestMotion, TestMotionReader>(world);
        Entity entity = world.CreateEntity(new TestMotion { Position = start });
        navigation.AddFlowAgent(entity, flow, 2);
        navigation.Schedule(0.1f).Steering.Complete();

        Vector3 direction = Vector3.Normalize(world.Read<NavigationGuidance>(entity).Velocity);
        Assert.Same(first, completed);
        Assert.Equal(NavigationFlowStatus.Searching, firstStatus);
        Assert.Equal(NavigationFlowStatus.Ready, completed.Status);
        Assert.True(Vector3.Dot(direction, Vector3.Normalize(goal - start)) > 0.99f);
        int cell = grid.FindCell(start);
        float geometricDistance = Vector3.Distance(grid.Cells.Span[cell].Position, goal);
        Assert.InRange(flow.Field!.CumulativeCosts.Span[cell] - geometricDistance, 0, 1.2f * grid.CellSize);
    }

    [Fact]
    public void FollowerGoesAroundScalarHighCostTerrainAndReportsArrivalFromActualMovement()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 3, ManagedPayloadPolicy = ManagedPayloadPolicy.Allow });
        using World world = WorldJobs.CreateWorld(scheduler: scheduler);
        NavigationGrid open = OpenGrid(26, 20, 0.5f);
        NavigationGridCell[] cells = open.Cells.ToArray();
        for (int cell = 0; cell < cells.Length; cell++)
        {
            Vector3 position = cells[cell].Position;
            if (position.X >= 4 && position.X < 8 && position.Z >= 3 && position.Z < 8)
                cells[cell] = cells[cell] with { Area = 7 };
        }
        var grid = new NavigationGrid(open.Bounds, open.CellSize, cells, open.Edges);
        var profile = new NavigationProfile([new Vector2(-0.1f), new(0.1f, -0.1f), new(0.1f), new(-0.1f, 0.1f)], 1);
        NavigationCosts costs = NavigationCostSystem.Schedule(grid, new NavigationPolicy([new(7, 25)]), scheduler: scheduler).Complete().Costs;
        Vector3 goal = new(10.75f, 0, 5.25f);
        var flow = new NavigationFlow(costs, profile, [new(goal, 0.15f)]);
        new NavigationFlowSystem<FastIterativeFlow, FastIterativeFlowState>(flow, scheduler).Schedule().Complete();
        var navigation = new NavigationSystems<TestMotion, TestMotionReader>(world);
        Entity entity = world.CreateEntity(new TestMotion { Position = new(1.25f, 0, 5.25f) });
        navigation.AddFlowAgent(entity, flow, 2);
        var positions = new List<Vector3>();

        for (int step = 0; step < 200 && world.Read<NavigationProgress>(entity).Status != NavigationStatus.Arrived; step++)
        {
            NavigationFlowStep scheduled = navigation.Schedule(0.1f);
            JobHandle moved = new ConsumeSteering(0.1f).ScheduleParallel(world, scheduled.Steering);
            navigation.ScheduleProgress(moved).Complete();
            positions.Add(world.Read<TestMotion>(entity).Position);
        }

        Assert.Contains(positions, position => position.X >= 4 && position.X < 8 && position.Z < 3);
        Assert.All(positions, position => Assert.Equal(0, grid.Cells.Span[grid.FindCell(position)].Area));
        Assert.Equal(NavigationStatus.Arrived, world.Read<NavigationProgress>(entity).Status);
        Assert.InRange(Vector3.Distance(world.Read<TestMotion>(entity).Position, goal), 0, 0.1501f);
    }

    // This regular open support is an example of the public scalar terrain input.
    private static NavigationGrid OpenGrid(int width, int height, float cellSize)
    {
        var cells = new NavigationGridCell[width * height];
        var edges = new List<NavigationGridEdge>();
        for (int z = 0; z < height; z++)
        for (int x = 0; x < width; x++)
        {
            int first = edges.Count;
            if (x > 0) edges.Add(new(z * width + x - 1, cellSize));
            if (x + 1 < width) edges.Add(new(z * width + x + 1, cellSize));
            if (z > 0) edges.Add(new((z - 1) * width + x, cellSize));
            if (z + 1 < height) edges.Add(new((z + 1) * width + x, cellSize));
            cells[z * width + x] = new(new((x + 0.5f) * cellSize, 0, (z + 0.5f) * cellSize), first, edges.Count - first);
        }
        return new(new(Vector3.Zero, new(width * cellSize, 1, height * cellSize)), cellSize, cells, edges.ToArray());
    }
}
