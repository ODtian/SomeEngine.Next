using System.Numerics;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;

namespace SomeEngine.Navigation.Tests;

public sealed class NavigationVehicleTests
{
    [Fact]
    public void StateLatticeExecutesACompleteLongTurnFromItsSuppliedMotionControl()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 2, ManagedPayloadPolicy = ManagedPayloadPolicy.Allow });
        using World world = WorldJobs.CreateWorld(scheduler: scheduler);
        var model = new NavigationVehicleModel(1);
        var profile = new NavigationProfile([new Vector2(-0.1f), new(0.1f, -0.1f), new(0.1f), new(-0.1f, 0.1f)],
            1, maximumSpeed: 1, vehicleModel: model);
        // This three-quarter circle is an example of a general signed, multi-radian motion control.
        var control = new NavigationVehicleControl(1, 1, 3 * MathF.PI / 2);
        var relativeStart = new NavigationPose(Vector3.Zero, 0);
        NavigationPose relativeEnd = model.Integrate(relativeStart, control, control.Duration);
        var motions = new NavigationMotionSet(model, 0.5f, new float[] { 0, relativeEnd.Heading },
            new NavigationMotionPrimitive[] { new(0, 1, relativeStart, relativeEnd, 0, 1, 0, 0, 0, 0) },
            new NavigationVehicleControl[] { control }, default, default);
        var cells = new List<NavigationGridCell>();
        var edges = new List<NavigationGridEdge>();
        for (int x = 0; x < 12; x++)
        for (int z = 0; z < 12; z++)
        {
            int first = edges.Count;
            if (x > 0) edges.Add(new((x - 1) * 12 + z, 0.5f));
            if (x < 11) edges.Add(new((x + 1) * 12 + z, 0.5f));
            if (z > 0) edges.Add(new(x * 12 + z - 1, 0.5f));
            if (z < 11) edges.Add(new(x * 12 + z + 1, 0.5f));
            cells.Add(new(new((x + 0.5f) * 0.5f, 0, (z + 0.5f) * 0.5f), first, edges.Count - first));
        }
        var grid = new NavigationGrid(new(Vector3.Zero, new(6, 2, 6)), 0.5f, cells.ToArray(), edges.ToArray(),
            NavigationGridShape.FromProfile(profile, NavigationGridFootprintMode.Point));
        var space = new NavigationVehicleSpace(grid, motions);
        var navigation = new NavigationSystems<NavigationVehicleSpace, StateLatticeSearch, StateLatticeSearchState, TestMotion, TestMotionReader>(world);
        var start = new NavigationPose(new(2.25f, 0, 2.25f), 0);
        NavigationPose goal = model.Integrate(start, control, control.Duration);
        Entity actor = world.CreateEntity(new TestMotion { Position = start.Position });
        navigation.AddAgent(actor, space, profile, new NavigationPolicy(), new(goal.Position, 0.001f, goal.Heading, 0.001f), 1);
        float traveled = 0;
        for (int frame = 0; frame < 60 && world.Read<NavigationProgress>(actor).Status != NavigationStatus.Arrived; frame++)
        {
            Vector3 previous = world.Read<TestMotion>(actor).Position;
            navigation.ScheduleProgress(new ConsumeVehicleMotion(0.1f, model).ScheduleParallel(world, navigation.Schedule(0.1f, 8).Steering)).Complete();
            traveled += Vector3.Distance(previous, world.Read<TestMotion>(actor).Position);
        }
        Assert.Equal(NavigationStatus.Arrived, world.Read<NavigationProgress>(actor).Status);
        Assert.InRange(traveled, 4.7f, 4.72f);
        Assert.InRange(Vector3.Distance(goal.Position, world.Read<TestMotion>(actor).Position), 0, 0.001f);
        Assert.InRange(MathF.Abs(NavigationHeading.Difference(goal.Heading, world.Read<TestMotion>(actor).Heading)), 0, 0.001f);
    }

    [Fact]
    public void HybridAStarDrivesFromAnArbitraryHeadingToTheDockingPoseUsingContinuousControls()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 2, ManagedPayloadPolicy = ManagedPayloadPolicy.Allow });
        using World world = WorldJobs.CreateWorld(scheduler: scheduler);
        var model = new NavigationVehicleModel(minimumTurningRadius: 2);
        var profile = new NavigationProfile(
            [new(-0.25f, -0.6f), new(0.25f, -0.6f), new(0.25f, 0.6f), new(-0.25f, 0.6f)],
            1, maximumSpeed: 1, vehicleModel: model);
        NavigationMotionSet motions = NavigationMotionSet.CreateCarLattice(profile, resolution: 0.5f, speed: 1);
        var space = new NavigationVehicleSpace(BendGrid(profile), motions,
            hybridSettings: new() { AnalyticExpansionDistance = 2.5f });
        var navigation = new NavigationSystems<NavigationVehicleSpace, HybridAStarSearch,
            HybridAStarSearchState, TestMotion, TestMotionReader>(world);
        var start = new NavigationPose(new(1.35f, 0, 1.45f), 0.27f);
        var goal = new NavigationPose(new(6.62f, 0, 5.13f), 1.13f);
        Entity entity = world.CreateEntity(new TestMotion { Position = start.Position, Heading = start.Heading });
        navigation.AddAgent(entity, space, profile, new NavigationPolicy(), new(goal.Position, 0.05f, goal.Heading, 0.025f), 1);
        navigation.Schedule(0.1f, 1).Steering.Complete();
        Assert.Equal(NavigationSearchStatus.Searching, world.Read<NavigationPathResult>(entity).Status);
        for (int frame = 0; frame < 700 && world.Read<NavigationProgress>(entity).Status != NavigationStatus.Arrived; frame++)
        {
            NavigationStep next = navigation.Schedule(0.1f, 64);
            navigation.ScheduleProgress(new ConsumeVehicleMotion(0.1f, model).ScheduleParallel(world, next.Steering)).Complete();
            TestMotion actual = world.Read<TestMotion>(entity);
            foreach (Vector2 corner in profile.Footprint.Span)
            {
                float cosine = MathF.Cos(actual.Heading), sine = MathF.Sin(actual.Heading);
                Vector3 point = actual.Position + new Vector3(cosine * corner.X + sine * corner.Y, 0, -sine * corner.X + cosine * corner.Y);
                Assert.True(point.X >= 0 && point.X <= 9 && point.Z >= 0 && point.Z <= 7 && (point.X <= 4 || point.Z >= 3));
            }
        }
        Assert.Equal(NavigationStatus.Arrived, world.Read<NavigationProgress>(entity).Status);
        NavigationPath path = world.Read<NavigationPathResult>(entity).Path!;
        Assert.Equal(start, path.Poses.Span[0]);
        Assert.InRange(Vector3.Distance(world.Read<TestMotion>(entity).Position, goal.Position), 0, 0.0501f);
        Assert.InRange(MathF.Abs(NavigationHeading.Difference(goal.Heading, world.Read<TestMotion>(entity).Heading)), 0, 0.0251f);
        Assert.Contains(path.MotionSegments.ToArray(), segment =>
            MathF.Abs(NavigationHeading.Difference(segment.End.Heading, start.Heading)) is > 0.05f and < 1.3f);
        foreach (NavigationMotionSegment segment in path.MotionSegments.Span)
        {
            Assert.InRange(MathF.Abs(segment.Control.Curvature), 0, model.MaximumCurvature + 0.00001f);
            Assert.InRange(MathF.Abs(segment.Control.Speed * segment.Control.Curvature), 0, profile.MaximumAngularSpeed + 0.00001f);
            NavigationPose integrated = model.Integrate(segment.Start, segment.Control, segment.Control.Duration);
            Assert.InRange(Vector3.Distance(integrated.Position, segment.End.Position), 0, 0.0001f);
        }
    }

    [Fact]
    public void StateLatticeControlsDriveTheVehicleThroughABendWithinItsTurningRadius()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 2, ManagedPayloadPolicy = ManagedPayloadPolicy.Allow });
        using World world = WorldJobs.CreateWorld(scheduler: scheduler);
        var model = new NavigationVehicleModel(minimumTurningRadius: 2);
        var profile = new NavigationProfile(
            [new(-0.25f, -0.6f), new(0.25f, -0.6f), new(0.25f, 0.6f), new(-0.25f, 0.6f)],
            1, maximumSpeed: 1, vehicleModel: model);
        NavigationMotionSet motions = NavigationMotionSet.CreateCarLattice(profile, resolution: 0.5f, speed: 1);
        var space = new NavigationVehicleSpace(BendGrid(profile), motions);
        var navigation = new NavigationSystems<NavigationVehicleSpace, StateLatticeSearch,
            StateLatticeSearchState, TestMotion, TestMotionReader>(world);
        Entity entity = world.CreateEntity(new TestMotion { Position = new(1.75f, 0, 1.25f) });
        navigation.AddAgent(entity, space, profile, new NavigationPolicy(),
            new NavigationGoal(new(6.75f, 0, 5.25f), 0.04f, MathF.PI / 2, 0.03f), 1);

        for (int step = 0; step < 240 && world.Read<NavigationProgress>(entity).Status != NavigationStatus.Arrived; step++)
        {
            NavigationStep next = navigation.Schedule(0.1f, 32);
            navigation.ScheduleProgress(new ConsumeVehicleMotion(0.1f, model).ScheduleParallel(world, next.Steering)).Complete();
            NavigationSteering steering = world.Read<NavigationSteering>(entity);
            if (steering.VehicleControl is { } control)
                Assert.InRange(MathF.Abs(control.Curvature), 0, 0.50001f);
            TestMotion actual = world.Read<TestMotion>(entity);
            foreach (Vector2 corner in profile.Footprint.Span)
            {
                float cosine = MathF.Cos(actual.Heading), sine = MathF.Sin(actual.Heading);
                Vector3 point = actual.Position + new Vector3(cosine * corner.X + sine * corner.Y, 0, -sine * corner.X + cosine * corner.Y);
                Assert.True(point.X >= 0 && point.X <= 9 && point.Z >= 0 && point.Z <= 7 && (point.X <= 4 || point.Z >= 3));
            }
        }

        Assert.Equal(NavigationStatus.Arrived, world.Read<NavigationProgress>(entity).Status);
        Assert.Contains(world.Read<NavigationPathResult>(entity).Path!.MotionSegments.ToArray(),
            segment => MathF.Abs(segment.Control.Curvature) > 0.1f);
    }

    // This L-shaped floor is the fixture example for the general grid and motion-set inputs.
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
                if (indices.TryGetValue((pair.Key.X + dx, pair.Key.Z + dz), out int adjacent))
                    edges.Add(new(adjacent, size));
            cells[pair.Value] = new(positions[pair.Value], first, edges.Count - first);
        }
        return new(new(Vector3.Zero, new(9, 2, 7)), size, cells, edges.ToArray(),
            NavigationGridShape.FromProfile(profile, NavigationGridFootprintMode.Point));
    }
}

public partial struct ConsumeVehicleMotion(float deltaTime, NavigationVehicleModel model) : IJobEntity
{
    private readonly float _deltaTime = deltaTime;
    private readonly NavigationVehicleModel _model = model;

    public void Execute(in NavigationSteering steering, ref TestMotion motion)
    {
        if (!steering.Active || steering.VehicleControl is not { } control)
        {
            motion.Velocity = default;
            return;
        }
        NavigationPose next = _model.Integrate(new(motion.Position, motion.Heading), control, MathF.Min(_deltaTime, control.Duration));
        motion.Position = next.Position;
        motion.Heading = next.Heading;
        motion.Velocity = NavigationHeading.Direction(next.Heading) * control.Speed;
    }
}
