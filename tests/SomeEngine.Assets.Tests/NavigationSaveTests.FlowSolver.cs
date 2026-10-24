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
    public async Task RestoringAnUnfinishedFimKeepsGradientGuidanceAndReachesTheSharedGoal()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 2, ManagedPayloadPolicy = ManagedPayloadPolicy.Allow });
        var manifest = new AssetManifest();
        var resources = new NavigationSaveResources();
        var profile = new NavigationProfile([new Vector2(-0.1f), new(0.1f, -0.1f), new(0.1f), new(-0.1f, 0.1f)], 1);
        var policy = new NavigationPolicy();
        NavigationGrid grid = ExampleGrid(profile);
        using (var profileAsset = new NavigationProfileAsset())
        using (var policyAsset = new NavigationPolicyAsset())
        using (var gridAsset = new NavigationAsset())
        using (var profileBlock = new NavigationProfileBlock())
        using (var policyBlock = new NavigationPolicyBlock())
        using (var gridBlock = new NavigationGridBlock())
        {
            profileBlock.SetProfile(profile);
            policyBlock.SetPolicy(policy);
            gridBlock.SetGrid(new(profileAsset.Reference, "walk"), grid);
            profileAsset.SetBlock(profileBlock);
            policyAsset.SetBlock(policyBlock);
            gridAsset.SetBlock(gridBlock);
            Add(await profileAsset.SaveAsync(Path.Combine(_directory, "fim.navigation-profile.asset")));
            Add(await policyAsset.SaveAsync(Path.Combine(_directory, "fim.navigation-policy.asset")));
            Add(await gridAsset.SaveAsync(Path.Combine(_directory, "fim.navigation.asset")));
            resources.Register(profile, profileAsset.Reference);
            resources.Register(policy, policyAsset.Reference);
            resources.Register(grid, gridAsset.Reference, new(profileAsset.Reference, "walk"));
        }
        var registry = new SerializationRegistry().Register<SaveMotion, SaveMotionCodec>(
            new(Guid.Parse("f943af78-c106-4f68-a4a7-dc05d7008dcc"), "navigation-save-motion", 1));
        using var stream = new MemoryStream();
        Entity actor;
        using (World world = WorldJobs.CreateWorld(scheduler: scheduler))
        {
            NavigationCosts costs = NavigationCostSystem.Schedule(grid, policy, scheduler: scheduler).Complete().Costs;
            var flow = new NavigationFlow(costs, profile, [new(new(4.5f, 0, 2.5f), 0.1f)]);
            var solver = new NavigationFlowSystem<FastIterativeFlow, FastIterativeFlowState>(flow, scheduler);
            Assert.Equal(NavigationFlowStatus.Searching, solver.Schedule(1).Complete().Status);
            actor = world.CreateEntity(new SaveMotion(new(0.5f, 0, 0.5f), default, 0));
            NavigationComponents.AddFlow(world, actor, flow, 1);
            NavigationSave.Write(stream, world, registry, resources);
        }
        stream.Position = 0;
        NavigationSaveResult restored;
        await using (var loader = new AssetLoader(new LooseAssetStorage(_directory, manifest)))
            restored = await NavigationSave.ReadAsync(stream, registry, loader, scheduler);
        using World loaded = restored.World;
        NavigationFlow resumed = loaded.Read<FlowFollower>(actor).Flow;
        Assert.Equal(NavigationFlowSampling.Gradient, resumed.Field!.Sampling);
        var navigation = new NavigationSystems<SaveMotion, SaveMotionReader>(loaded);
        for (int frame = 0; frame < 80 && loaded.Read<NavigationProgress>(actor).Status != NavigationStatus.Arrived; frame++)
        {
            NavigationFlowStep step = navigation.Schedule(0.2f);
            navigation.ScheduleProgress(new ConsumeSavedSteering(0.2f).ScheduleParallel(loaded, step.Steering)).Complete();
        }
        Assert.Equal(NavigationStatus.Arrived, loaded.Read<NavigationProgress>(actor).Status);

        void Add(AssetEntry entry) => manifest.AddAsset(entry.AssetGuid, entry.Name, Path.GetFileName(entry.Path), entry.AssetType,
            entry.SchemaFingerprint, version: entry.Version);
    }
}
