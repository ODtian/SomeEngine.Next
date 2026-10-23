using System.Numerics;
using SomeEngine.ECS;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;

namespace SomeEngine.Navigation.Tests;

public sealed class NavigationTiledFlowTests
{
    [Fact]
    public void EikonalFieldCrossesSuccessiveDetailedTilesAndMatchesTheWholeGridAtTheirBoundaries()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 3 });
        using World world = WorldJobs.CreateWorld(scheduler: scheduler);
        var profile = new NavigationProfile([new(-0.1f, -0.1f), new(0.1f, -0.1f), new(0.1f, 0.1f), new(-0.1f, 0.1f)], 1);
        var policy = new NavigationPolicy();
        NavigationFlowGoal[] goals = [new(new(8.5f, 0, 3.5f), 0.05f)];
        NavigationGrid original = OpenGrid();
        var reference = new NavigationFlow(NavigationCostSystem.Schedule(original, policy, scheduler: scheduler).Complete().Costs, profile, goals);
        new NavigationFlowSystem<FastIterativeFlow, FastIterativeFlowState>(reference, scheduler).Schedule().Complete();
        NavigationGrid tiled = NavigationGridRegionBuilder.Schedule(original, profile, policy, scheduler: scheduler).Complete().Grid;
        NavigationFlow flow = NavigationGridFlowBuilder.Schedule(tiled.RegionGraph!, tiled.Tiles.Slice(0, 1),
            profile, policy, goals, scheduler: scheduler).Complete().Flow;
        var solver = new NavigationFlowSystem<FastIterativeFlow, FastIterativeFlowState>(flow, scheduler);
        var navigation = new NavigationSystems<Motion, MotionReader>(world);
        var actor = world.CreateEntity(new Motion(new(0.5f, 0, 0.5f), default, 0));
        navigation.AddFlowAgent(actor, flow, 2);
        for (int count = 1; count <= 3; count++)
        {
            if (count > 1)
            {
                NavigationFlow acquired = NavigationGridFlowBuilder.Schedule(tiled.RegionGraph!, tiled.Tiles.Slice(0, count),
                    profile, policy, goals, scheduler: scheduler).Complete().Flow;
                flow.SetCosts(acquired.Costs);
            }
            for (int frame = 0; frame < 150; frame++)
            {
                navigation.Schedule(0.1f, solver.Schedule(64).Completion).Steering.Complete();
                Motion motion = world.Read<Motion>(actor);
                NavigationSteering steering = world.Read<NavigationSteering>(actor);
                world.Replace(actor, new Motion(motion.Position + steering.Velocity * 0.1f, steering.Velocity, steering.Heading));
                navigation.ScheduleProgress(default).Complete();
                if (ReferenceEquals(flow.Field?.Input, flow.Input) &&
                    world.Read<NavigationProgress>(actor).Status is NavigationStatus.WaitingForTiles or NavigationStatus.Arrived) break;
            }
            if (count < 3) Assert.Equal(count * 3 - 0.5f, world.Read<Motion>(actor).Position.X, 3);
        }
        Assert.Equal(NavigationStatus.Arrived, world.Read<NavigationProgress>(actor).Status);
        Assert.Equal(3, flow.Field!.Tiles.Length);
        foreach (Vector3 position in new Vector3[] { new(2.5f, 0, 1.5f), new(3.5f, 0, 2.5f), new(6.5f, 0, 1.5f) })
        {
            int expected = reference.Field!.Input.Costs.Grid.FindCell(position);
            int actual = flow.Field.Input.Costs.Grid.FindCell(position);
            Assert.Equal(reference.Field.CumulativeCosts.Span[expected], flow.Field.CumulativeCosts.Span[actual], 4);
        }
        Assert.All(flow.Field.BoundaryDependencies.ToArray(), boundary => Assert.Equal(flow.Field.Round, boundary.Round));
    }

    // This open rectangle is an example whose two-axis gradients cross both tile boundaries.
    private static NavigationGrid OpenGrid()
    {
        var cells = new List<NavigationGridCell>();
        var edges = new List<NavigationGridEdge>();
        for (int x = 0; x < 9; x++)
        for (int z = 0; z < 5; z++)
        {
            int first = edges.Count;
            if (x > 0) edges.Add(new((x - 1) * 5 + z, 1));
            if (x < 8) edges.Add(new((x + 1) * 5 + z, 1));
            if (z > 0) edges.Add(new(x * 5 + z - 1, 1));
            if (z < 4) edges.Add(new(x * 5 + z + 1, 1));
            cells.Add(new(new(x + 0.5f, 0, z + 0.5f), first, edges.Count - first, Tile: new(x / 3, 0)));
        }
        return new(new(new(0, 0, 0), new(9, 1, 5)), 1, cells.ToArray(), edges.ToArray());
    }
    private readonly record struct Motion(Vector3 Position, Vector3 Velocity, float Heading) : IComponent;
    private readonly struct MotionReader : INavigationMotionReader<Motion>
    {
        public static NavigationMotion Read(in Motion value) => new(value.Position, value.Velocity, value.Heading, true);
    }
}
