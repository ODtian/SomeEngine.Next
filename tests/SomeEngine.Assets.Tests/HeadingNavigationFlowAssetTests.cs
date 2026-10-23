using System.Numerics;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;
using SomeEngine.Navigation;

namespace SomeEngine.Assets.Tests;

public sealed class HeadingNavigationFlowAssetTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "SomeEngine-HeadingFlow-" + Guid.NewGuid().ToString("N"));
    public HeadingNavigationFlowAssetTests() => Directory.CreateDirectory(_directory);
    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public async Task ReopenedHeadingFlowDrivesSharedActorsThroughTurningAndTraversalAfterAssetsClose()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 2, ManagedPayloadPolicy = ManagedPayloadPolicy.Allow });
        var manifest = new AssetManifest();
        string flowPath = Path.Combine(_directory, "group.navigation-flow.asset");
        HeadingNavigationFlowSource source;
        using (var profileAsset = new NavigationProfileAsset())
        using (var policyAsset = new NavigationPolicyAsset())
        using (var navigationAsset = new NavigationAsset())
        using (var flowAsset = new NavigationFlowAsset())
        using (var profileBlock = new NavigationProfileBlock())
        using (var policyBlock = new NavigationPolicyBlock())
        using (var meshBlock = new HeadingNavigationMeshBlock())
        using (var linkBlock = new NavigationLinkBlock())
        using (var flowBlock = new HeadingNavigationFlowBlock())
        {
            var profile = new NavigationProfile([new Vector2(-0.2f, -0.8f), new(0.2f, -0.8f), new(0.2f, 0.8f), new(-0.2f, 0.8f)],
                1.5f, maximumSpeed: 2, maximumAngularSpeed: MathF.PI);
            var policy = new NavigationPolicy();
            HeadingNavigationFlowRegion[] regions = ExampleRegions();
            var links = new NavigationLinkSet(new NavigationLink[]
            {
                new(71, new(10, 1), new(20, 2), 9, Parameters: new(0.3f, 0, 0, 0), Capacity: 1),
            });
            profileBlock.SetProfile(profile);
            policyBlock.SetPolicy(policy);
            meshBlock.SetMesh(new(profileAsset.Reference, "entry"), regions[0].Mesh);
            meshBlock.SetMesh(new(profileAsset.Reference, "exit"), regions[1].Mesh);
            linkBlock.SetLinks(links);
            profileAsset.SetBlock(profileBlock);
            policyAsset.SetBlock(policyBlock);
            navigationAsset.SetBlock(meshBlock);
            navigationAsset.SetBlock(linkBlock);
            AssetEntry profileEntry = await profileAsset.SaveAsync(Path.Combine(_directory, "actor.navigation-profile.asset"));
            AssetEntry policyEntry = await policyAsset.SaveAsync(Path.Combine(_directory, "actor.navigation-policy.asset"));
            AssetEntry navigationEntry = await navigationAsset.SaveAsync(Path.Combine(_directory, "world.navigation.asset"));
            foreach (AssetEntry entry in new[] { profileEntry, policyEntry, navigationEntry })
                manifest.AddAsset(entry.AssetGuid, entry.Name, Path.GetFileName(entry.Path), entry.AssetType,
                    entry.SchemaFingerprint, version: entry.Version);
            source = new(new HeadingNavigationFlowRegionSource[]
                {
                    new(10, navigationAsset.Reference, new(profileAsset.Reference, "entry"), navigationEntry.Version),
                    new(20, navigationAsset.Reference, new(profileAsset.Reference, "exit"), navigationEntry.Version),
                }, navigationAsset.Reference, profileAsset.Reference, policyAsset.Reference,
                navigationEntry.Version, profileEntry.Version, policyEntry.Version);
            HeadingNavigationFlowGraph graph = HeadingNavigationFlowBuilder.Schedule(regions, links, profile, policy,
                version: 4, scheduler: scheduler).Complete().Graph;
            var flow = new NavigationFlow(graph, profile, new NavigationFlowGoal[] { new(new(2, 0, 12), 0.05f, 0) });
            flowBlock.SetFlow(source, flow);
            flowAsset.SetBlock(flowBlock);
            await flowAsset.SaveAsync(flowPath);
        }

        NavigationFlow restored;
        HeadingNavigationFlowSource restoredSource;
        await using (var loader = new AssetLoader(new LooseAssetStorage(_directory, manifest)))
        using (var asset = await NavigationFlowAsset.OpenAsync(flowPath))
        {
            HeadingNavigationFlowBlock block = await asset.GetBlockAsync<HeadingNavigationFlowBlock>();
            restoredSource = block.Source;
            restored = await block.CreateAsync(loader, scheduler);
        }

        Assert.Equal(source.Regions.ToArray(), restoredSource.Regions.ToArray());
        Assert.Equal(source.Links, restoredSource.Links);
        Assert.Equal(source.Profile, restoredSource.Profile);
        Assert.Equal(source.Policy, restoredSource.Policy);
        Assert.Equal(NavigationFlowAction.Rotate, restored.Field!.Sample(new(new(2, 0, 2), MathF.PI / 2)).Action);
        Assert.Equal(NavigationFlowAction.Move, restored.Field.Sample(new(new(2, 0, 5), 0)).Action);
        NavigationFlowSample action = restored.Field.Sample(new(new(2, 0, 7), 0));
        Assert.Equal(NavigationFlowAction.Traversal, action.Action);
        Assert.Equal(71, action.Traversal!.Value.Connection);
        Assert.Equal(9, action.Traversal.Value.Action);
        Assert.Equal(new Vector3(2, 0, 11), action.Traversal.Value.Exit);
        Assert.Equal(new Vector3(2, 0, 12), restored.Goals.Span[0].Position);

        using World world = WorldJobs.CreateWorld(scheduler: scheduler);
        var navigation = new NavigationSystems<FlowMotion, FlowMotionReader>(world);
        Entity[] actors = [world.CreateEntity(new FlowMotion(new(2, 0, 2), default, MathF.PI / 2)),
            world.CreateEntity(new FlowMotion(new(2.1f, 0, 2), default, MathF.PI / 2))];
        foreach (Entity actor in actors)
        {
            world.Add(actor, new FlowAction());
            navigation.AddFlowAgent(actor, restored, 2);
        }
        for (int frame = 0; frame < 150 && actors.Any(actor => world.Read<NavigationProgress>(actor).Status != NavigationStatus.Arrived); frame++)
        {
            navigation.Schedule(0.1f).Steering.Complete();
            foreach (Entity actor in actors)
            {
                FlowMotion motion = world.Read<FlowMotion>(actor);
                NavigationTraversalRequest request = world.Read<NavigationTraversalRequest>(actor);
                if (request.Status is NavigationTraversalStatus.Requested or NavigationTraversalStatus.Executing)
                {
                    FlowAction execution = world.Read<FlowAction>(actor);
                    if (execution.Request != request.RequestId)
                        execution = new(request.RequestId, motion.Position, 0, execution.Count + 1, request.Action);
                    execution = execution with { Elapsed = execution.Elapsed + 0.1f };
                    float progress = MathF.Min(1, execution.Elapsed / request.Parameters.X);
                    Vector3 position = Vector3.Lerp(execution.Start, request.Exit, progress);
                    motion = motion with { Velocity = (position - motion.Position) / 0.1f, Position = position };
                    world.Replace(actor, execution);
                    world.Replace(actor, new NavigationTraversalResult(request.RequestId,
                        progress == 1 ? NavigationTraversalExecution.Completed : NavigationTraversalExecution.Running, progress));
                }
                else
                {
                    NavigationSteering steering = world.Read<NavigationSteering>(actor);
                    Vector3 velocity = steering.Active ? steering.Velocity : Vector3.Zero;
                    motion = new(motion.Position + velocity * 0.1f, velocity, steering.Active ? steering.Heading : motion.Heading);
                }
                world.Replace(actor, motion);
                if (motion.Position.Z >= 4 && motion.Position.Z <= 8)
                    Assert.InRange(MathF.Abs(NavigationHeading.Difference(0, motion.Heading)), 0, 0.01f);
            }
            navigation.ScheduleProgress(default).Complete();
            Assert.InRange(restored.Input.HeadingGraph!.Links.GetPassage(71).InUse.Length, 0, 1);
        }
        Assert.All(actors, actor =>
        {
            Assert.Same(restored, world.Read<FlowFollower>(actor).Flow);
            Assert.Equal(1, world.Read<FlowAction>(actor).Count);
            Assert.Equal(9, world.Read<FlowAction>(actor).Action);
            Assert.Equal(NavigationStatus.Arrived, world.Read<NavigationProgress>(actor).Status);
        });
    }

    // These completed polygons and heading channels are a fixture example for the asset roundtrip.
    private static HeadingNavigationFlowRegion[] ExampleRegions()
    {
        var entryGeometry = new NavigationMesh(
            [new(0, 0, 0), new(4, 0, 0), new(4, 0, 4), new(0, 0, 4),
             new(1.5f, 0, 4), new(2.5f, 0, 4), new(2.5f, 0, 8), new(1.5f, 0, 8)],
            [0, 1, 2, 3, 4, 5, 6, 7], [new(0, 4, 0, 1), new(4, 4, 1, 0, Tile: new(0, 1))],
            [new(1, new(1.5f, 0, 4), new(2.5f, 0, 4))]);
        HeadingNavigationInterval north = new(0, -MathF.PI / 4, MathF.PI / 4);
        HeadingNavigationInterval east = new(1, MathF.PI / 4, MathF.PI * 3 / 4);
        var entry = new HeadingNavigationMesh(entryGeometry, 4,
            new HeadingNavigationRegion[] { new(0, 2, 0, 1, 0, 2, 0, 3), new(0, 1, 1, 0, 2, 0, 3, 2) },
            new[] { north, east },
            new HeadingNavigationTranslation[] { new(1, 0, new(1.5f, 0, 4), new(2.5f, 0, 4), 0) },
            new HeadingNavigationRotation[] { new(0, 1, MathF.PI / 2), new(1, 0, -MathF.PI / 2) },
            new(new HeadingNavigationNode[]
                { new(0, 1, new(2, 0, 2), 0, 1), new(0, 0, new(2, 0, 2), 1, 2), new(0, 0, new(2, 0, 4), 3, 2),
                  new(1, 0, new(2, 0, 4), 5, 1), new(1, 0, new(2, 0, 7), 6, 1) },
                new HeadingNavigationEdge[] { new(1, HeadingNavigationAction.Rotation), new(0, HeadingNavigationAction.Rotation),
                    new(2, HeadingNavigationAction.Translation), new(1, HeadingNavigationAction.Translation), new(3, HeadingNavigationAction.Translation, 0),
                    new(4, HeadingNavigationAction.Translation), new(3, HeadingNavigationAction.Translation) }));
        var exitGeometry = new NavigationMesh([new(0, 0, 10), new(4, 0, 10), new(4, 0, 14), new(0, 0, 14)],
            [0, 1, 2, 3], [new(0, 4, 0, 0)], []);
        var exit = new HeadingNavigationMesh(exitGeometry, 4, new HeadingNavigationRegion[] { new(0, 1, 0, 0, 0, 0, 0, 1) },
            new[] { north }, Array.Empty<HeadingNavigationTranslation>(), Array.Empty<HeadingNavigationRotation>(),
            new(new HeadingNavigationNode[] { new(0, 0, new(2, 0, 11), 0, 0) }, Array.Empty<HeadingNavigationEdge>()));
        return [new(10, entry, new HeadingNavigationFlowPort[] { new(1, new(2, 0, 7)) }),
            new(20, exit, new HeadingNavigationFlowPort[] { new(2, new(2, 0, 11)) })];
    }

    private readonly record struct FlowAction(ulong Request, Vector3 Start, float Elapsed, int Count, int Action) : IComponent;
    private readonly record struct FlowMotion(Vector3 Position, Vector3 Velocity, float Heading) : IComponent;
    private readonly struct FlowMotionReader : INavigationMotionReader<FlowMotion>
    {
        public static NavigationMotion Read(in FlowMotion motion) => new(motion.Position, motion.Velocity, motion.Heading, true);
    }
}
