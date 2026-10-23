using System.Numerics;
using SomeEngine.ECS;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;
using SomeEngine.Navigation;

namespace SomeEngine.Assets.Tests;

public sealed class NavigationFlowAssetTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "SomeEngine-NavigationFlow-" + Guid.NewGuid().ToString("N"));
    public NavigationFlowAssetTests() => Directory.CreateDirectory(_directory);
    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReopenedFlowResolvesItsInputsAndDrivesMovementAfterAssetsClose(bool precomputed)
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 2, ManagedPayloadPolicy = ManagedPayloadPolicy.Allow });
        var manifest = new AssetManifest();
        string flowPath = Path.Combine(_directory, "group.navigation-flow.asset");
        NavigationFlowSource source;
        using (var profileAsset = new NavigationProfileAsset())
        using (var policyAsset = new NavigationPolicyAsset())
        using (var navigationAsset = new NavigationAsset())
        using (var flowAsset = new NavigationFlowAsset())
        using (var profileBlock = new NavigationProfileBlock())
        using (var policyBlock = new NavigationPolicyBlock())
        using (var gridBlock = new NavigationGridBlock())
        using (var flowBlock = new NavigationFlowBlock())
        {
            var profile = new NavigationProfile([new Vector2(-0.1f), new(0.1f, -0.1f), new(0.1f), new(-0.1f, 0.1f)], 1.8f);
            var policy = new NavigationPolicy();
            NavigationGrid grid = NavigationGridRegionBuilder.Schedule(ExampleGrid(profile), profile, policy, scheduler: scheduler).Complete().Grid;
            profileBlock.SetProfile(profile);
            policyBlock.SetPolicy(policy);
            gridBlock.SetGrid(new(profileAsset.Reference, "main"), grid);
            profileAsset.SetBlock(profileBlock);
            policyAsset.SetBlock(policyBlock);
            navigationAsset.SetBlock(gridBlock);
            AssetEntry profileEntry = await profileAsset.SaveAsync(Path.Combine(_directory, "actor.navigation-profile.asset"));
            AssetEntry policyEntry = await policyAsset.SaveAsync(Path.Combine(_directory, "actor.navigation-policy.asset"));
            AssetEntry navigationEntry = await navigationAsset.SaveAsync(Path.Combine(_directory, "world.navigation.asset"));
            foreach (AssetEntry entry in new[] { profileEntry, policyEntry, navigationEntry })
                manifest.AddAsset(entry.AssetGuid, entry.Name, Path.GetFileName(entry.Path), entry.AssetType,
                    entry.SchemaFingerprint, version: entry.Version);
            source = new(navigationAsset.Reference, profileAsset.Reference, policyAsset.Reference, "main",
                navigationEntry.Version, profileEntry.Version, policyEntry.Version);
            NavigationCosts costs = NavigationCostSystem.Schedule(grid, policy, version: 4, scheduler: scheduler).Complete().Costs;
            var flow = new NavigationFlow(costs, profile, [new(new(3.5f, 0, 0.5f), 0.1f)]);
            new NavigationFlowSystem<ReverseDijkstraFlow, ReverseDijkstraFlowState>(flow, scheduler).Schedule().Complete();
            flowBlock.SetFlow(source, flow, precomputed);
            flowAsset.SetBlock(flowBlock);
            await flowAsset.SaveAsync(flowPath);
        }
        NavigationFlow restored;
        NavigationFlowSource restoredSource;
        bool storedField;
        await using (var loader = new AssetLoader(new LooseAssetStorage(_directory, manifest)))
        using (var asset = await NavigationFlowAsset.OpenAsync(flowPath))
        {
            NavigationFlowBlock block = await asset.GetBlockAsync<NavigationFlowBlock>();
            restoredSource = block.Source;
            storedField = block.HasPrecomputedField;
            restored = await block.CreateAsync(loader, scheduler);
        }
        using World world = WorldJobs.CreateWorld(scheduler: scheduler);
        var navigation = new NavigationSystems<FlowMotion, FlowMotionReader>(world);
        var entity = world.CreateEntity(new FlowMotion(new(0.5f, 0, 0.5f), default, 0));
        navigation.AddFlowAgent(entity, restored, 2);
        for (int step = 0; step < 40 && world.Read<NavigationProgress>(entity).Status != NavigationStatus.Arrived; step++)
        {
            navigation.Schedule(0.1f).Steering.Complete();
            NavigationSteering steering = world.Read<NavigationSteering>(entity);
            FlowMotion motion = world.Read<FlowMotion>(entity);
            world.Replace(entity, new FlowMotion(motion.Position + steering.Velocity * 0.1f, steering.Velocity, steering.Heading));
            navigation.ScheduleProgress(default).Complete();
        }

        Assert.Equal(source, restoredSource);
        Assert.Equal(precomputed, storedField);
        Assert.Equal(new Vector3(3.5f, 0, 0.5f), restored.Goals.Span[0].Position);
        Assert.Equal(2, restored.Field!.Tiles.Length);
        Assert.Equal(2, restored.Field.BoundaryDependencies.Length);
        Assert.All(restored.Field.BoundaryDependencies.ToArray(), boundary => Assert.Equal(restored.Field.Round, boundary.Round));
        Assert.Equal(NavigationStatus.Arrived, world.Read<NavigationProgress>(entity).Status);
    }

    // These four cells in two tiles are an asset roundtrip example of an ordinary completed field.
    private static NavigationGrid ExampleGrid(NavigationProfile profile) => new(
        new(new(0, 0, 0), new(4, 2, 1)), 1,
        [new(new(0.5f, 0, 0.5f), 0, 1), new(new(1.5f, 0, 0.5f), 1, 2),
         new(new(2.5f, 0, 0.5f), 3, 2, Tile: new(1, 0)), new(new(3.5f, 0, 0.5f), 5, 1, Tile: new(1, 0))],
        [new(1, 1), new(0, 1), new(2, 1), new(1, 1), new(3, 1), new(2, 1)],
        NavigationGridShape.FromProfile(profile), contentVersion: 9);

    private readonly record struct FlowMotion(Vector3 Position, Vector3 Velocity, float Heading) : IComponent;
    private readonly struct FlowMotionReader : INavigationMotionReader<FlowMotion>
    {
        public static NavigationMotion Read(in FlowMotion motion) => new(motion.Position, motion.Velocity, motion.Heading, true);
    }
}
