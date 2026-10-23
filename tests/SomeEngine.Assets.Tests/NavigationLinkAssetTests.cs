using System.Numerics;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;
using SomeEngine.Navigation;

namespace SomeEngine.Assets.Tests;

public sealed class NavigationLinkAssetTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "SomeEngine-NavigationLink-" + Guid.NewGuid().ToString("N"));
    public NavigationLinkAssetTests() => Directory.CreateDirectory(_directory);
    public void Dispose() => Directory.Delete(_directory, true);

    [Fact]
    public async Task ReopenedActionLinkRemainsUsableAfterAssetClosureAndCompletesMovement()
    {
        string path = Path.Combine(_directory, "crossing.navigation.asset");
        var definition = new NavigationLink(71, new(10, 1), new(20, 2), 9,
            Cost: 0.25f, RequiredCapabilities: 1, Parameters: new(0.3f, 2, 0, 0));
        using (var asset = new NavigationAsset())
        using (var block = new NavigationLinkBlock())
        {
            block.SetLinks(new(new[] { definition }));
            asset.SetBlock(block);
            await asset.SaveAsync(path);
        }
        NavigationLinkSet links;
        using (var asset = await NavigationAsset.OpenAsync(path))
            links = await (await asset.GetBlockAsync<NavigationLinkBlock>()).CreateAsync();

        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 2, ManagedPayloadPolicy = ManagedPayloadPolicy.Allow });
        using World world = WorldJobs.CreateWorld(scheduler: scheduler);
        var profile = new NavigationProfile([new Vector2(-0.1f), new(0.1f, -0.1f), new(0.1f), new(-0.1f, 0.1f)],
            1, maximumSpeed: 2, capabilities: 1);
        var policy = new NavigationPolicy();
        // This two-platform scene is a persistence example; endpoint ports and action parameters are public data.
        var mesh = new NavigationMesh([new(0, 0, 0), new(3, 0, 0), new(3, 0, 2), new(0, 0, 2)],
            [0, 1, 2, 3], [new(0, 4, 0, 0)], []);
        mesh = NavigationRegionBuilder.Schedule(mesh, profile, policy,
            [new NavigationRegionPort(1, default, new(2.5f, 0, 1)), new(2, default, new(0.5f, 0, 1))], scheduler: scheduler).Complete().Mesh;
        NavigationScene scene = NavigationSceneBuilder.Schedule(
            [new NavigationRegionPlacement(10, mesh, Matrix4x4.Identity), new(20, mesh, Matrix4x4.CreateTranslation(5, 0, 0))],
            Array.Empty<NavigationSceneConnection>(), profile, policy, scheduler: scheduler).Complete().Scene;
        scene.ScheduleLinks(links, scheduler: scheduler).Complete();
        var navigation = new NavigationSystems<NavigationScene, SceneNavigationSearch, SceneSearchState,
            AssetVehicleMotion, AssetVehicleMotionReader>(world);
        Entity actor = world.CreateEntity(new AssetVehicleMotion(new(1, 0, 1), default, 0));
        navigation.AddAgent(actor, scene, profile, policy, new(new Vector3(7, 0, 1), 0.05f), 2);
        for (int step = 0; step < 80 && world.Read<NavigationProgress>(actor).Status != NavigationStatus.Arrived; step++)
        {
            NavigationStep next = navigation.Schedule(0.1f, 32);
            navigation.ScheduleProgress(new ConsumeAssetAction(0.1f).ScheduleParallel(world, next.Steering)).Complete();
        }

        NavigationTraversalRequest request = world.Read<NavigationTraversalRequest>(actor);
        Assert.Equal(definition, links.GetLink(71));
        Assert.Equal(9, request.Action);
        Assert.Equal(definition.Parameters, request.Parameters);
        Assert.Equal(NavigationTraversalStatus.Completed, request.Status);
        Assert.Equal(NavigationStatus.Arrived, world.Read<NavigationProgress>(actor).Status);
    }
}

public partial struct ConsumeAssetAction(float deltaTime) : IJobEntity
{
    private readonly float _deltaTime = deltaTime;

    public void Execute(in NavigationSteering steering, in NavigationTraversalRequest request,
        ref NavigationTraversalResult result, ref AssetVehicleMotion motion)
    {
        if (request.Status is NavigationTraversalStatus.Requested or NavigationTraversalStatus.Executing)
        {
            motion = motion with { Position = request.Exit, Velocity = default };
            result = new(request.RequestId, NavigationTraversalExecution.Completed, 1);
        }
        else if (steering.Active)
            motion = new(motion.Position + steering.Velocity * _deltaTime, steering.Velocity, steering.Heading);
    }
}
