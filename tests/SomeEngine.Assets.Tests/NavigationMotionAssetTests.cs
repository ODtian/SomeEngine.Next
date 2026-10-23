using System.Numerics;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;
using SomeEngine.Navigation;

namespace SomeEngine.Assets.Tests;

public sealed class NavigationMotionAssetTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "SomeEngine-NavigationMotion-" + Guid.NewGuid().ToString("N"));
    public NavigationMotionAssetTests() => Directory.CreateDirectory(_directory);
    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public async Task ReopenedVehicleProfileAndMotionSetDriveAnActorThroughTheBend()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 2, ManagedPayloadPolicy = ManagedPayloadPolicy.Allow });
        var model = new NavigationVehicleModel(2, allowReverse: true);
        var sourceProfile = new NavigationProfile(
            [new(-0.25f, -0.6f), new(0.25f, -0.6f), new(0.25f, 0.6f), new(-0.25f, 0.6f)],
            1, maximumSpeed: 1, maximumAngularSpeed: 0.25f, vehicleModel: model);
        NavigationMotionSet sourceMotions = NavigationMotionSet.CreateCarLattice(sourceProfile, 0.5f, 1);
        string profilePath = Path.Combine(_directory, "car.navigation-profile.asset");
        string gridPath = Path.Combine(_directory, "bend.navigation.asset");
        NavigationGridSelection selection;
        using (var asset = new NavigationProfileAsset())
        using (var navigationAsset = new NavigationAsset())
        using (var profileBlock = new NavigationProfileBlock())
        using (var motionBlock = new NavigationMotionBlock())
        using (var gridBlock = new NavigationGridBlock())
        {
            profileBlock.SetProfile(sourceProfile);
            motionBlock.SetMotionSet(sourceMotions);
            asset.SetBlock(profileBlock);
            asset.SetBlock(motionBlock);
            selection = new(asset.Reference, "vehicle");
            gridBlock.SetGrid(selection, BendGrid(sourceProfile));
            navigationAsset.SetBlock(gridBlock);
            await asset.SaveAsync(profilePath);
            await navigationAsset.SaveAsync(gridPath);
        }

        NavigationProfile profile;
        NavigationMotionSet motions;
        NavigationGrid grid;
        using (var asset = await NavigationProfileAsset.OpenAsync(profilePath))
        using (var navigationAsset = await NavigationAsset.OpenAsync(gridPath))
        {
            profile = await (await asset.GetBlockAsync<NavigationProfileBlock>()).CreateAsync();
            motions = await (await asset.GetBlockAsync<NavigationMotionBlock>()).CreateAsync();
            grid = await (await navigationAsset.GetBlockAsync<NavigationGridBlock>()).CreateAsync(selection);
        }

        using World world = WorldJobs.CreateWorld(scheduler: scheduler);
        var space = new NavigationVehicleSpace(grid, motions);
        var navigation = new NavigationSystems<NavigationVehicleSpace, StateLatticeSearch,
            StateLatticeSearchState, AssetVehicleMotion, AssetVehicleMotionReader>(world);
        Entity actor = world.CreateEntity(new AssetVehicleMotion(new(1.75f, 0, 1.25f), default, 0));
        navigation.AddAgent(actor, space, profile, new(), new NavigationGoal(new Vector3(6.75f, 0, 5.25f), 0.04f, MathF.PI / 2, 0.03f), 1);
        for (int step = 0; step < 240 && world.Read<NavigationProgress>(actor).Status != NavigationStatus.Arrived; step++)
        {
            JobHandle steering = navigation.Schedule(0.1f, 32).Steering;
            navigation.ScheduleProgress(new ConsumeAssetVehicle(0.1f, motions.Model).ScheduleParallel(world, steering)).Complete();
        }

        Assert.Equal(model, profile.VehicleModel);
        Assert.Equal(model, motions.Model);
        Assert.Equal(sourceMotions.Headings.ToArray(), motions.Headings.ToArray());
        Assert.Equal(sourceMotions.Primitives.ToArray(), motions.Primitives.ToArray());
        Assert.Equal(sourceMotions.Controls.ToArray(), motions.Controls.ToArray());
        Assert.Equal(sourceMotions.Samples.ToArray(), motions.Samples.ToArray());
        Assert.Equal(sourceMotions.SweptGeometry.ToArray(), motions.SweptGeometry.ToArray());
        Assert.All(motions.Controls.ToArray(), control =>
            Assert.InRange(MathF.Abs(control.Speed * control.Curvature), 0, profile.MaximumAngularSpeed + 0.00001f));
        Assert.Equal(NavigationStatus.Arrived, world.Read<NavigationProgress>(actor).Status);
        Assert.Contains(world.Read<NavigationPathResult>(actor).Path!.MotionSegments.ToArray(),
            segment => MathF.Abs(segment.Control.Curvature) > 0.1f);
    }

    // This L-shaped floor is the asset roundtrip example for a general point-support grid.
    private static NavigationGrid BendGrid(NavigationProfile profile)
    {
        const float size = 0.5f;
        var indices = new Dictionary<(int X, int Z), int>();
        var positions = new List<Vector3>();
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
                if (indices.TryGetValue((pair.Key.X + dx, pair.Key.Z + dz), out int adjacent)) edges.Add(new(adjacent, size));
            cells[pair.Value] = new(positions[pair.Value], first, edges.Count - first);
        }
        return new(new(Vector3.Zero, new(9, 2, 7)), size, cells, edges.ToArray(),
            NavigationGridShape.FromProfile(profile, NavigationGridFootprintMode.Point));
    }
}

public readonly record struct AssetVehicleMotion(Vector3 Position, Vector3 Velocity, float Heading) : IComponent;

public readonly struct AssetVehicleMotionReader : INavigationMotionReader<AssetVehicleMotion>
{
    public static NavigationMotion Read(in AssetVehicleMotion motion) => new(motion.Position, motion.Velocity, motion.Heading, true);
}

public partial struct ConsumeAssetVehicle(float deltaTime, NavigationVehicleModel model) : IJobEntity
{
    private readonly float _deltaTime = deltaTime;
    private readonly NavigationVehicleModel _model = model;
    public void Execute(in NavigationSteering steering, ref AssetVehicleMotion motion)
    {
        if (!steering.Active || steering.VehicleControl is not { } control)
        {
            motion = motion with { Velocity = default };
            return;
        }
        NavigationPose next = _model.Integrate(new(motion.Position, motion.Heading), control, MathF.Min(_deltaTime, control.Duration));
        motion = new(next.Position, NavigationHeading.Direction(next.Heading) * control.Speed, next.Heading);
    }
}
