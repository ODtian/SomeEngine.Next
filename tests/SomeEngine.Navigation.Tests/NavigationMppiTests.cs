using System.Numerics;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;

namespace SomeEngine.Navigation.Tests;

public sealed class NavigationMppiTests
{
    [Fact]
    public void VehicleSelectsFeasibleBendTrajectoriesAroundAPredictedMovingNeighbor()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 3, ManagedPayloadPolicy = ManagedPayloadPolicy.Allow });
        using World world = WorldJobs.CreateWorld(scheduler: scheduler);
        var model = new NavigationVehicleModel(1.5f, allowReverse: true);
        var profile = new NavigationProfile(
            [new(-0.3f, -0.8f), new(0.3f, -0.8f), new(0.3f, 0.8f), new(-0.3f, 0.8f)],
            1, maximumSpeed: 1.2f, maximumAcceleration: 2, maximumAngularSpeed: 1, vehicleModel: model);
        var policy = new NavigationPolicy();
        NavigationGrid grid = BendGrid(profile);
        NavigationCosts costs = NavigationCostSystem.Schedule(grid, policy, scheduler: scheduler).Complete().Costs;
        var space = new NavigationVehicleSpace(grid, NavigationMotionSet.CreateCarLattice(profile, 0.5f, 1.2f), costs);
        var paths = new NavigationSystems<NavigationVehicleSpace, StateLatticeSearch, StateLatticeSearchState, TestMotion, TestMotionReader>(world);
        var neighbors = new NavigationNeighborSystem<TestMotion, TestMotionReader>(world, scheduler);
        var mppi = new NavigationAvoidanceSystem<NavigationVehicleSpace, NavigationVehicleMppiModel, TestMotion, TestMotionReader>(world, neighbors);
        var prediction = new NavigationPredictionSystem<NavigationVelocityPrediction, TestMotion, TestMotionReader>(world);
        Entity actor = world.CreateEntity(new TestMotion { Position = new(1.75f, 0, 1.25f) });
        world.Add(actor, new MppiVehicleTag());
        paths.AddAgent(actor, space, profile, policy, new(new Vector3(7.75f, 0, 5.25f), 0.3f, MathF.PI / 2, 0.2f), 1.2f);
        NavigationComponents.AddPrediction<NavigationVelocityPrediction>(world, actor, 0.3f, 0.1f);
        NavigationComponents.AddMppi<NavigationVehicleSpace, NavigationVehicleMppiModel>(world, actor, space,
            new() { CandidateCount = 64, HorizonSteps = 24, TimeStep = 0.1f, Iterations = 1, Seed = 12 },
            new(0.86f, NeighborDistance: 6, Spacing: 0.05f));

        Entity walker = world.CreateEntity(new TestMotion { Position = new(4.75f, 0, 6.5f), Velocity = new(0, 0, -0.25f), Heading = MathF.PI });
        world.Add(walker, new MppiWalkerTag());
        var walkerProfile = new NavigationProfile([new Vector2(-0.15f), new(0.15f, -0.15f), new(0.15f), new(-0.15f, 0.15f)], 1);
        world.Add(walker, new NavigationAgent(walkerProfile, policy, 0.25f));
        world.Add(walker, new NavigationSteering(new(0, 0, -0.25f), MathF.PI, 1, true));
        NavigationComponents.AddAvoidance(world, walker, new(0.25f));
        NavigationComponents.AddPrediction<NavigationVelocityPrediction>(world, walker, 3, 0.1f);
        float firstDistance = Vector3.Distance(world.Read<TestMotion>(actor).Position, new Vector3(7.75f, 0, 5.25f));
        bool turned = false;

        for (int step = 0; step < 100 && world.Read<NavigationProgress>(actor).Status != NavigationStatus.Arrived; step++)
        {
            float time = step * 0.1f;
            JobHandle guidance = paths.ScheduleGuidance(0.1f, 128).Guidance;
            JobHandle forecast = prediction.Schedule(time, default);
            JobHandle neighborReady = neighbors.Schedule(predictionReady: forecast);
            JobHandle selected = mppi.Schedule(time, guidance, neighborReady);
            JobHandle predictionFinished = prediction.Schedule(time, selected);
            predictionFinished.Complete();
            NavigationTrajectoryPoint[] trajectory = [];
            world.ExecuteBufferRead<NavigationTrajectoryPoint>(actor, buffer => trajectory = buffer.AsSpan().ToArray());
            Assert.Equal(world.Read<TestMotion>(actor).Position, trajectory[0].Position);
            foreach (NavigationTrajectoryPoint point in trajectory)
            foreach (Vector2 corner in profile.Footprint.Span)
            {
                float cosine = MathF.Cos(point.Heading), sine = MathF.Sin(point.Heading);
                Vector3 body = point.Position + new Vector3(cosine * corner.X + sine * corner.Y, 0, -sine * corner.X + cosine * corner.Y);
                Assert.True(body.X >= -0.001f && body.X <= 9.001f && body.Z >= -0.001f && body.Z <= 7.001f && (body.X <= 4.001f || body.Z >= 2.999f));
            }
            JobHandle vehicleMoved = new ConsumeMppiVehicle(0.1f, model).ScheduleParallel(world, selected);
            JobHandle walkerMoved = new MoveMppiWalker(0.1f).ScheduleParallel(world, selected);
            paths.ScheduleProgress(WorldJobs.CombineDependencies(world, [vehicleMoved, walkerMoved])).Complete();
            TestMotion actual = world.Read<TestMotion>(actor);
            if (trajectory.Length > 1)
            {
                Assert.Equal(time + 2.4f, trajectory[^1].Time, 4);
                Assert.InRange(Vector3.Distance(trajectory[1].Position, actual.Position), 0, 0.0001f);
                Assert.InRange(MathF.Abs(NavigationHeading.Difference(trajectory[1].Heading, actual.Heading)), 0, 0.0001f);
                Assert.InRange(Vector3.Distance(trajectory[1].Velocity, actual.Velocity), 0, 0.0001f);
            }
            Vector3 relative = world.Read<TestMotion>(walker).Position - actual.Position;
            float localX = MathF.Cos(actual.Heading) * relative.X - MathF.Sin(actual.Heading) * relative.Z;
            float localZ = MathF.Sin(actual.Heading) * relative.X + MathF.Cos(actual.Heading) * relative.Z;
            float bodyDistance = new Vector2(MathF.Max(0, MathF.Abs(localX) - 0.3f), MathF.Max(0, MathF.Abs(localZ) - 0.8f)).Length();
            Assert.True(bodyDistance >= 0.29f, $"body separation: {bodyDistance}");
            turned |= actual.Heading > 0.4f;
        }

        Assert.True(turned);
        Assert.True(world.Read<TestMotion>(actor).Position.X > 4.5f);
        Assert.True(world.Read<NavigationProgress>(actor).Distance < firstDistance - 3);
    }

    // This L-shaped area and crossing actor are the example inputs for the general local controller.
    private static NavigationGrid BendGrid(NavigationProfile profile)
    {
        const float size = 0.5f;
        var positions = new List<Vector3>();
        var indices = new Dictionary<(int X, int Z), int>();
        for (int z = 0; z < 14; z++)
        for (int x = 0; x < 18; x++)
        {
            if (x >= 8 && z < 6) continue;
            indices.Add((x, z), positions.Count);
            positions.Add(new((x + 0.5f) * size, 0, (z + 0.5f) * size));
        }
        var cells = new NavigationGridCell[positions.Count];
        var edges = new List<NavigationGridEdge>();
        foreach (var pair in indices)
        {
            int first = edges.Count;
            foreach ((int dx, int dz) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
                if (indices.TryGetValue((pair.Key.X + dx, pair.Key.Z + dz), out int next)) edges.Add(new(next, size));
            cells[pair.Value] = new(positions[pair.Value], first, edges.Count - first);
        }
        return new(new(Vector3.Zero, new(9, 2, 7)), size, cells, edges.ToArray(),
            NavigationGridShape.FromProfile(profile, NavigationGridFootprintMode.Point));
    }
}

public readonly struct MppiVehicleTag : IComponent;
public readonly struct MppiWalkerTag : IComponent;

public partial struct ConsumeMppiVehicle(float deltaTime, NavigationVehicleModel model) : IJobEntity
{
    private readonly float _deltaTime = deltaTime;
    private readonly NavigationVehicleModel _model = model;
    public void Execute(in MppiVehicleTag tag, in NavigationSteering steering, ref TestMotion motion)
    {
        if (!steering.Active || steering.VehicleControl is not { } control) { motion.Velocity = default; return; }
        NavigationPose next = _model.Integrate(new(motion.Position, motion.Heading), control, MathF.Min(_deltaTime, control.Duration));
        motion.Position = next.Position;
        motion.Heading = next.Heading;
        motion.Velocity = NavigationHeading.Direction(next.Heading) * control.Speed;
    }
}

public partial struct MoveMppiWalker(float deltaTime) : IJobEntity
{
    private readonly float _deltaTime = deltaTime;
    public void Execute(in MppiWalkerTag tag, ref TestMotion motion) => motion.Position += motion.Velocity * _deltaTime;
}
