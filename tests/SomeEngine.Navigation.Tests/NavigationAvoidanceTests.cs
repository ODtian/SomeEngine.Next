using System.Numerics;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;

namespace SomeEngine.Navigation.Tests;

public sealed class NavigationAvoidanceTests
{
    [Fact]
    public void PathAndFlowActorsShareNeighborsAndPassThroughTheCorridor()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 3, ManagedPayloadPolicy = ManagedPayloadPolicy.Allow });
        using World world = WorldJobs.CreateWorld(scheduler: scheduler);
        var profile = new NavigationProfile([new Vector2(-0.2f), new(0.2f, -0.2f), new(0.2f), new(-0.2f, 0.2f)],
            1, maximumSpeed: 1.5f);
        var policy = new NavigationPolicy();
        NavigationMesh mesh = CorridorMesh();
        NavigationGrid grid = CorridorGrid();
        NavigationCosts costs = NavigationCostSystem.Schedule(grid, policy, scheduler: scheduler).Complete().Costs;
        var flow = new NavigationFlow(costs, profile, [new(new(0.5f, 0, 1.5f), 0.2f)]);
        new NavigationFlowSystem<ReverseDijkstraFlow, ReverseDijkstraFlowState>(flow, scheduler).Schedule().Complete();
        var paths = new NavigationSystems<NavigationMesh, PolygonNavigationSearch, PolygonSearchState, TestMotion, TestMotionReader>(world);
        var flows = new NavigationSystems<TestMotion, TestMotionReader>(world);
        var neighbors = new NavigationNeighborSystem<TestMotion, TestMotionReader>(world, scheduler);
        var avoidance = new NavigationAvoidanceSystem<OrcaAvoidance, TestMotion, TestMotionReader>(world, neighbors);
        Entity path = world.CreateEntity(new TestMotion { Position = new(0.5f, 0, 1.4f) });
        paths.AddAgent(path, mesh, profile, policy, new(new Vector3(9.5f, 0, 1.4f), 0.15f), 1.5f);
        Entity first = world.CreateEntity(new TestMotion { Position = new(8.5f, 0, 1.5f) });
        Entity second = world.CreateEntity(new TestMotion { Position = new(9.5f, 0, 1.5f) });
        flows.AddFlowAgent(first, flow, 1.5f);
        flows.AddFlowAgent(second, flow, 1.5f);
        Entity[] actors = [path, first, second];
        foreach (Entity actor in actors) NavigationComponents.AddAvoidance(world, actor, new(0.3f, Spacing: 0.05f));
        float minimumSeparation = float.PositiveInfinity;

        for (int step = 0; step < 180; step++)
        {
            JobHandle pathGuidance = paths.ScheduleGuidance(0.1f, 32).Guidance;
            JobHandle flowGuidance = flows.ScheduleGuidance(0.1f).Guidance;
            JobHandle neighborReady = neighbors.Schedule();
            JobHandle pathBoundary = avoidance.SchedulePathBoundaries<NavigationMesh>();
            JobHandle flowBoundary = avoidance.ScheduleFlowBoundaries();
            JobHandle steering = avoidance.Schedule(0.1f,
                WorldJobs.CombineDependencies(world, [pathGuidance, flowGuidance]), neighborReady,
                WorldJobs.CombineDependencies(world, [pathBoundary, flowBoundary]));
            JobHandle moved = new ConsumeSteering(0.1f).ScheduleParallel(world, steering);
            WorldJobs.CombineDependencies(world, [paths.ScheduleProgress(moved), flows.ScheduleProgress(moved)]).Complete();
            for (int a = 0; a < actors.Length; a++)
            {
                Vector3 position = world.Read<TestMotion>(actors[a]).Position;
                Assert.InRange(position.Z, 0.29f, 2.71f);
                for (int b = a + 1; b < actors.Length; b++)
                    minimumSeparation = MathF.Min(minimumSeparation, Vector3.Distance(position, world.Read<TestMotion>(actors[b]).Position));
            }
        }

        Assert.Equal(3, neighbors.Data.Agents.Length);
        Assert.True(minimumSeparation >= 0.69f, $"minimum separation: {minimumSeparation}");
        Assert.True(world.Read<TestMotion>(path).Position.X > 8.5f);
        Assert.True(world.Read<TestMotion>(first).Position.X < 1.5f);
        Assert.True(world.Read<TestMotion>(second).Position.X < 2.5f);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(0.3f)]
    public void BoundaryClearanceIncludesTheAlreadyBakedDisk(float bakedRadius)
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 2, ManagedPayloadPolicy = ManagedPayloadPolicy.Allow });
        using World world = WorldJobs.CreateWorld(scheduler: scheduler);
        var profile = new NavigationProfile([new Vector2(-0.2f), new(0.2f, -0.2f), new(0.2f), new(-0.2f, 0.2f)], 1);
        var mesh = new NavigationMesh(
            [new(bakedRadius, 0, bakedRadius), new(10 - bakedRadius, 0, bakedRadius),
             new(10 - bakedRadius, 0, 3 - bakedRadius), new(bakedRadius, 0, 3 - bakedRadius)],
            [0, 1, 2, 3], [new(0, 4, 0, 0)], [], bakedRadius: bakedRadius);
        Entity entity = world.CreateEntity(new TestMotion { Position = new(9.4f, 0, 0.8f) });
        world.Add(entity, new NavigationAgent(profile, new(), 1));
        world.Add(entity, new PathNavigation<NavigationMesh>(mesh));
        world.Add(entity, new NavigationGuidance(new(1, 0, -1), 0, 1, true));
        world.Add(entity, default(NavigationSteering));
        NavigationComponents.AddAvoidance(world, entity, new(0.3f, Spacing: 0.05f));
        var neighbors = new NavigationNeighborSystem<TestMotion, TestMotionReader>(world, scheduler);
        var avoidance = new NavigationAvoidanceSystem<OrcaAvoidance, TestMotion, TestMotionReader>(world, neighbors);

        for (int step = 0; step < 160; step++)
        {
            JobHandle steering = avoidance.Schedule(0.05f, default, neighbors.Schedule(), avoidance.SchedulePathBoundaries<NavigationMesh>());
            new ConsumeSteering(0.05f).ScheduleParallel(world, steering).Complete();
        }

        Vector3 position = world.Read<TestMotion>(entity).Position;
        Assert.InRange(position.X, 9.64f, 9.66f);
        Assert.InRange(position.Z, 0.34f, 0.36f);
    }

    [Fact]
    public void SharedIndexUsesActualLayersHeightAndInteractionRange()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 2, ManagedPayloadPolicy = ManagedPayloadPolicy.Allow });
        using World world = WorldJobs.CreateWorld(scheduler: scheduler);
        var profile = new NavigationProfile([new Vector2(-0.2f), new(0.2f, -0.2f), new(0.2f), new(-0.2f, 0.2f)], 1);
        Entity source = Add(new(0, 0, 0), 0, 1);
        Entity close = Add(new(1, 0, 0), 0, 1);
        Add(new(1, 0, 0), 1, 1);
        Add(new(1, 2, 0), 0, 1);
        Add(new(4, 0, 0), 0, 1);
        Add(new(1, 0, 0), 0, 2);
        var neighbors = new NavigationNeighborSystem<LayeredMotion, LayeredMotionReader>(world, scheduler);

        neighbors.Schedule().Complete();

        int index = Assert.Single(neighbors.Data.GetNeighbors(source).ToArray());
        Assert.Equal(close, neighbors.Data.Agents.Span[index].Entity);
        Assert.Equal(new Vector3(0.5f, 0, 0), neighbors.Data.Agents.Span[index].Motion.Velocity);

        Entity Add(Vector3 position, int layer, ulong mask)
        {
            Entity entity = world.CreateEntity(new LayeredMotion { Position = position, Velocity = new(0.5f, 0, 0), Layer = layer });
            world.Add(entity, new NavigationAgent(profile, new(), 1));
            NavigationComponents.AddAvoidance(world, entity, new(0.3f, NeighborDistance: 2, InteractionMask: mask));
            return entity;
        }
    }

    [Fact]
    public void ScenePlacementPreservesBakedClearanceThroughTileExtraction()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 2, ManagedPayloadPolicy = ManagedPayloadPolicy.Allow });
        var profile = new NavigationProfile([new Vector2(-0.2f), new(0.2f, -0.2f), new(0.2f), new(-0.2f, 0.2f)], 1);
        NavigationMesh geometry = CorridorMesh();
        var mesh = new NavigationMesh(geometry.Positions, geometry.PolygonVertices, geometry.Polygons, geometry.Portals, bakedRadius: 0.3f);
        Matrix4x4 transform = Matrix4x4.CreateScale(2, 1, 3) * Matrix4x4.CreateRotationY(0.4f);

        NavigationScene scene = NavigationSceneBuilder.Schedule([new NavigationRegionPlacement(4, mesh, transform)],
            [], profile, new NavigationPolicy(), scheduler: scheduler).Complete().Scene;

        NavigationSceneTile tile = Assert.Single(scene.Content.Tiles.ToArray());
        Assert.Equal(0.6f, tile.Mesh.BakedRadius, 5);
    }

    // This corridor is an acceptance example expressed through the public completed-data models.
    private static NavigationMesh CorridorMesh() => new(
        [new(0, 0, 0), new(10, 0, 0), new(10, 0, 3), new(0, 0, 3)],
        [0, 1, 2, 3], [new(0, 4, 0, 0)], []);

    private static NavigationGrid CorridorGrid()
    {
        var cells = new List<NavigationGridCell>();
        var edges = new List<NavigationGridEdge>();
        for (int z = 0; z < 3; z++)
        for (int x = 0; x < 10; x++)
        {
            int start = edges.Count;
            if (x > 0) edges.Add(new(z * 10 + x - 1, 1));
            if (x < 9) edges.Add(new(z * 10 + x + 1, 1));
            if (z > 0) edges.Add(new((z - 1) * 10 + x, 1));
            if (z < 2) edges.Add(new((z + 1) * 10 + x, 1));
            cells.Add(new(new(x + 0.5f, 0, z + 0.5f), start, edges.Count - start));
        }
        return new(new(Vector3.Zero, new(10, 1, 3)), 1, cells.ToArray(), edges.ToArray());
    }
}

public struct LayeredMotion : IComponent
{
    public Vector3 Position;
    public Vector3 Velocity;
    public int Layer;
}

public readonly struct LayeredMotionReader : INavigationMotionReader<LayeredMotion>
{
    public static NavigationMotion Read(in LayeredMotion motion) => new(motion.Position, motion.Velocity, 0, true, motion.Layer);
}
