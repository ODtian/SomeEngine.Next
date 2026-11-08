using System.Numerics;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Serialization;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;
using SomeEngine.Navigation;

namespace SomeEngine.Assets.Tests;

public sealed partial class NavigationSaveTests
{
    [Fact]
    public async Task MotionAssetRestoreResumesSelectedVehicleSearchAndMppiFromActualFeedback()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 3, ManagedPayloadPolicy = ManagedPayloadPolicy.Allow });
        var manifest = new AssetManifest();
        var resources = new NavigationSaveResources();
        var model = new NavigationVehicleModel(0.5f, allowReverse: true);
        var profile = new NavigationProfile([new(-0.1f, -0.2f), new(0.1f, -0.2f), new(0.1f, 0.2f), new(-0.1f, 0.2f)],
            1, maximumSpeed: 1, maximumAcceleration: 2, maximumAngularSpeed: 1.5f, vehicleModel: model);
        var policy = new NavigationPolicy();
        NavigationGrid originalGrid = ExampleGrid(profile);
        var grid = new NavigationGrid(originalGrid.Bounds, originalGrid.CellSize, originalGrid.Cells, originalGrid.Edges,
            NavigationGridShape.FromProfile(profile, NavigationGridFootprintMode.Point));
        NavigationMotionSet motions = NavigationMotionSet.CreateCarLattice(profile, 0.5f, 1);
        NavigationCosts costs = NavigationCostSystem.Schedule(grid, policy, scheduler: scheduler).Complete().Costs;
        var space = new NavigationVehicleSpace(grid, motions, costs, new() { HeadingBins = 32, SpatialResolution = 0.25f, StepLength = 0.4f, ReversePenalty = 1.8f });
        using (var profileAsset = new NavigationProfileAsset())
        using (var policyAsset = new NavigationPolicyAsset())
        using (var navigationAsset = new NavigationAsset())
        using (var profileBlock = new NavigationProfileBlock())
        using (var policyBlock = new NavigationPolicyBlock())
        using (var motionBlock = new NavigationMotionBlock())
        using (var gridBlock = new NavigationGridBlock())
        {
            profileBlock.SetProfile(profile); policyBlock.SetPolicy(policy); motionBlock.SetMotionSet(motions);
            gridBlock.SetGrid(new(profileAsset.Reference, "vehicle"), grid);
            profileAsset.SetBlock(profileBlock); profileAsset.SetBlock(motionBlock);
            policyAsset.SetBlock(policyBlock); navigationAsset.SetBlock(gridBlock);
            Add(await profileAsset.SaveAsync(Path.Combine(_directory, "vehicle.navigation-profile.asset")));
            Add(await policyAsset.SaveAsync(Path.Combine(_directory, "vehicle.navigation-policy.asset")));
            Add(await navigationAsset.SaveAsync(Path.Combine(_directory, "vehicle.navigation.asset")));
            resources.Register(profile, profileAsset.Reference); resources.Register(motions, profileAsset.Reference);
            resources.Register(policy, policyAsset.Reference); resources.Register(grid, navigationAsset.Reference, new(profileAsset.Reference, "vehicle"));
        }
        var registry = new SerializationRegistry().Register<SaveMotion, SaveMotionCodec>(
            new(Guid.Parse("f943af78-c106-4f68-a4a7-dc05d7008dcc"), "navigation-save-motion", 1));
        using var stream = new MemoryStream();
        Entity latticeActor, mppiActor;
        SaveMotion saved;
        using (World world = WorldJobs.CreateWorld(scheduler: scheduler))
        {
            latticeActor = world.CreateEntity(new SaveMotion(new(0.5f, 0, 0.5f), default, 0));
            mppiActor = world.CreateEntity(new SaveMotion(new(1.5f, 0, 0.5f), default, MathF.PI / 2));
            NavigationComponents.AddPath<NavigationVehicleSpace, StateLatticeSearchState>(world, latticeActor, space, profile, policy,
                new(new Vector3(0.5f, 0, 2.5f), 0.05f, 0), 1);
            NavigationComponents.AddPath<NavigationVehicleSpace, HybridAStarSearchState>(world, mppiActor, space, profile, policy,
                new(new Vector3(4.5f, 0, 0.5f), 0.05f, MathF.PI / 2), 1);
            NavigationComponents.AddPrediction<NavigationVelocityPrediction>(world, latticeActor, 0.7f, 0.1f);
            NavigationComponents.AddPrediction<NavigationVelocityPrediction>(world, mppiActor, 0.7f, 0.1f);
            NavigationComponents.AddMppi<NavigationVehicleSpace, NavigationVehicleMppiModel>(world, mppiActor, space,
                new() { CandidateCount = 16, HorizonSteps = 8, TimeStep = 0.1f, NoiseStandardDeviation = new(0.1f, 0.05f), Seed = 41 }, new(0.25f));
            using var neighbors = new NavigationNeighborSystem<SaveMotion, SaveMotionReader>(world, scheduler);
            Action<float> move = Driver(world, model, neighbors);
            for (int step = 0; step < 5; step++) move(step * 0.1f);
            saved = world.Read<SaveMotion>(mppiActor);
            NavigationSave.Write(stream, world, registry, resources);
        }
        stream.Position = 0;
        NavigationSaveResult restored;
        await using (var loader = new AssetLoader(new LooseAssetStorage(_directory, manifest)))
            restored = await NavigationSave.ReadAsync(stream, registry, loader, scheduler);
        using World loaded = restored.World;
        NavigationVehicleSpace restoredSpace = loaded.Read<PathNavigation<NavigationVehicleSpace>>(latticeActor).Space;
        Assert.Same(restoredSpace, loaded.Read<PathNavigation<NavigationVehicleSpace>>(mppiActor).Space);
        Assert.Equal(saved, loaded.Read<SaveMotion>(mppiActor));
        Assert.Equal(model, restoredSpace.Motions.Model);
        Assert.Equal(space.HybridSettings, restoredSpace.HybridSettings);
        Assert.True(NavigationComponents.HasPath<NavigationVehicleSpace, StateLatticeSearchState>(loaded, latticeActor));
        Assert.True(NavigationComponents.HasPath<NavigationVehicleSpace, HybridAStarSearchState>(loaded, mppiActor));
        Assert.True(loaded.Has<NavigationMppi<NavigationVehicleSpace, NavigationVehicleMppiModel>>(mppiActor));
        using var loadedNeighbors = new NavigationNeighborSystem<SaveMotion, SaveMotionReader>(loaded, scheduler);
        Action<float> continued = Driver(loaded, restoredSpace.Motions.Model, loadedNeighbors);
        continued(0.5f);
        NavigationTrajectoryPoint[] trajectory = [];
        loaded.ExecuteBufferRead<NavigationTrajectoryPoint>(mppiActor, buffer => trajectory = buffer.AsSpan().ToArray());
        Assert.Equal(1.3f, trajectory[^1].Time, 4);
        Assert.InRange(Vector3.Distance(trajectory[1].Position, loaded.Read<SaveMotion>(mppiActor).Position), 0, 0.0001f);
        for (int step = 6; step < 120 && new[] { latticeActor, mppiActor }.Any(actor => loaded.Read<NavigationProgress>(actor).Status != NavigationStatus.Arrived); step++)
            continued(step * 0.1f);
        Assert.Equal(NavigationStatus.Arrived, loaded.Read<NavigationProgress>(latticeActor).Status);
        Assert.InRange(loaded.Read<NavigationProgress>(mppiActor).Distance, 0, 0.2f);
        Assert.True(loaded.Read<SaveMotion>(mppiActor).Position.X > saved.Position.X + 1.5f);

        Action<float> Driver(World world, NavigationVehicleModel vehicleModel,
            NavigationNeighborSystem<SaveMotion, SaveMotionReader> neighbors)
        {
            var lattice = new NavigationSystems<NavigationVehicleSpace, StateLatticeSearch, StateLatticeSearchState, SaveMotion, SaveMotionReader>(world);
            var hybrid = new NavigationSystems<NavigationVehicleSpace, HybridAStarSearch, HybridAStarSearchState, SaveMotion, SaveMotionReader>(world);
            var mppi = new NavigationAvoidanceSystem<NavigationVehicleSpace, NavigationVehicleMppiModel, SaveMotion, SaveMotionReader>(world, neighbors);
            var prediction = new NavigationPredictionSystem<NavigationVelocityPrediction, SaveMotion, SaveMotionReader>(world);
            return time =>
            {
                JobHandle guidance = hybrid.ScheduleGuidance(0.1f, 256).Guidance;
                JobHandle selected = WorldJobs.CombineDependencies(world, [lattice.Schedule(0.1f, 64).Steering, mppi.Schedule(time, guidance, neighbors.Schedule())]);
                JobHandle forecast = prediction.Schedule(time, selected);
                JobHandle moved = new ConsumeSavedVehicle(0.1f, vehicleModel).ScheduleParallel(world, forecast);
                lattice.ScheduleProgress(moved).Complete(); hybrid.ScheduleProgress(moved).Complete();
            };
        }
        void Add(AssetEntry entry) => manifest.AddAsset(entry.AssetGuid, entry.Name, Path.GetFileName(entry.Path), entry.AssetType,
            entry.SchemaFingerprint, version: entry.Version);
    }
}

public partial struct ConsumeSavedVehicle(float deltaTime, NavigationVehicleModel model) : IJobEntity
{
    private readonly float _deltaTime = deltaTime;
    private readonly NavigationVehicleModel _model = model;
    public void Execute(in NavigationSteering steering, ref SaveMotion motion)
    {
        if (!steering.Active || steering.VehicleControl is not { } control) { motion = motion with { Velocity = default }; return; }
        NavigationPose next = _model.Integrate(new(motion.Position, motion.Heading), control, MathF.Min(_deltaTime, control.Duration));
        motion = new(next.Position, NavigationHeading.Direction(next.Heading) * control.Speed, next.Heading);
    }
}
