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
    public async Task HeadingFollowersResumeSavedActionExecutionAndKeepTheirSharedFlowAndRequestSequence()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 3, ManagedPayloadPolicy = ManagedPayloadPolicy.Allow });
        var resources = new NavigationSaveResources();
        var manifest = new AssetManifest();
        var profile = new NavigationProfile([new(-0.2f, -0.8f), new(0.2f, -0.8f), new(0.2f, 0.8f), new(-0.2f, 0.8f)],
            1.5f, maximumSpeed: 2, maximumAngularSpeed: MathF.PI);
        var policy = new NavigationPolicy();
        HeadingNavigationFlowRegion[] regions = HeadingNavigationFlowAssetTests.ExampleRegions();
        var links = new NavigationLinkSet(new NavigationLink[] { new(71, new(10, 1), new(20, 2), 9,
            Bidirectional: true, Parameters: new(0.6f, 0, 0, 0), Capacity: 1) });
        HeadingNavigationFlowGraph graph = HeadingNavigationFlowBuilder.Schedule(regions, links, profile, policy,
            version: 4, scheduler: scheduler).Complete().Graph;
        var flow = new NavigationFlow(graph, profile, new NavigationFlowGoal[] { new(new(2, 0, 12), 0.05f, 0) });
        using (var profileAsset = new NavigationProfileAsset())
        using (var policyAsset = new NavigationPolicyAsset())
        using (var navigationAsset = new NavigationAsset())
        using (var flowAsset = new NavigationFlowAsset())
        using (var profileBlock = new NavigationProfileBlock())
        using (var policyBlock = new NavigationPolicyBlock())
        using (var headingBlock = new HeadingNavigationMeshBlock())
        using (var linkBlock = new NavigationLinkBlock())
        using (var flowBlock = new HeadingNavigationFlowBlock())
        {
            profileBlock.SetProfile(profile); policyBlock.SetPolicy(policy); linkBlock.SetLinks(links);
            headingBlock.SetMesh(new(profileAsset.Reference, "entry"), regions[0].Mesh);
            headingBlock.SetMesh(new(profileAsset.Reference, "exit"), regions[1].Mesh);
            profileAsset.SetBlock(profileBlock); policyAsset.SetBlock(policyBlock);
            navigationAsset.SetBlock(headingBlock); navigationAsset.SetBlock(linkBlock);
            Add(await profileAsset.SaveAsync(Path.Combine(_directory, "heading.navigation-profile.asset")));
            Add(await policyAsset.SaveAsync(Path.Combine(_directory, "heading.navigation-policy.asset")));
            Add(await navigationAsset.SaveAsync(Path.Combine(_directory, "heading.navigation.asset")));
            flowBlock.SetFlow(new(new HeadingNavigationFlowRegionSource[]
            {
                new(10, navigationAsset.Reference, new(profileAsset.Reference, "entry")),
                new(20, navigationAsset.Reference, new(profileAsset.Reference, "exit")),
            }, navigationAsset.Reference, profileAsset.Reference, policyAsset.Reference), flow);
            flowAsset.SetBlock(flowBlock);
            Add(await flowAsset.SaveAsync(Path.Combine(_directory, "heading.navigation-flow.asset")));
            resources.Register(profile, profileAsset.Reference); resources.Register(policy, policyAsset.Reference);
            resources.Register(regions[0].Mesh, navigationAsset.Reference, new(profileAsset.Reference, "entry"));
            resources.Register(regions[1].Mesh, navigationAsset.Reference, new(profileAsset.Reference, "exit"));
            resources.Register(links, navigationAsset.Reference); resources.RegisterHeadingFlow(flow, flowAsset.Reference);
        }
        new NavigationFlowSystem<ReverseDijkstraFlow, ReverseDijkstraFlowState>(flow, scheduler).Schedule().Complete();
        var registry = new SerializationRegistry()
            .Register<SaveMotion, SaveMotionCodec>(new(Guid.Parse("f943af78-c106-4f68-a4a7-dc05d7008dcc"), "navigation-save-motion", 1))
            .Register<SaveAction, SaveActionCodec>(new(Guid.Parse("35602cb1-c236-4d23-8e03-74baa01ad7a8"), "navigation-save-action", 1));
        using var stream = new MemoryStream();
        Entity[] actors;
        Entity headingActor;
        SaveMotion savedMotion;
        SaveAction savedAction;
        using (World world = WorldJobs.CreateWorld(scheduler: scheduler))
        {
            actors = [world.CreateEntity(new SaveMotion(new(2, 0, 2), default, MathF.PI / 2)),
                world.CreateEntity(new SaveMotion(new(2.1f, 0, 2), default, MathF.PI / 2))];
            var navigation = new NavigationSystems<SaveMotion, SaveMotionReader>(world);
            headingActor = world.CreateEntity(new SaveMotion(new(2, 0, 2), default, MathF.PI / 2));
            NavigationComponents.AddPath<HeadingNavigationMesh, AsaSearchState>(world, headingActor, regions[0].Mesh, profile, policy,
                new(new Vector3(2, 0, 6), 0.05f, 0), 2);
            world.Disable<NavigationGoal>(headingActor);
            foreach (Entity actor in actors) { world.Add(actor, default(SaveAction)); navigation.AddFlowAgent(actor, flow, 2); }
            for (int frame = 0; frame < 100; frame++)
            {
                MoveActions(world, navigation, actors);
                if (world.Read<SaveAction>(actors[0]).Elapsed >= 0.3f) break;
            }
            savedMotion = world.Read<SaveMotion>(actors[0]); savedAction = world.Read<SaveAction>(actors[0]);
            Assert.InRange(savedMotion.Position.Z, 8.1f, 10.1f);
            NavigationSave.Write(stream, world, registry, resources);
        }
        stream.Position = 0;
        NavigationSaveResult restored;
        await using (var loader = new AssetLoader(new LooseAssetStorage(_directory, manifest)))
            restored = await NavigationSave.ReadAsync(stream, registry, loader, scheduler);
        using World loaded = restored.World;
        NavigationFlow shared = loaded.Read<FlowFollower>(actors[0]).Flow;
        Assert.Same(shared, loaded.Read<FlowFollower>(actors[1]).Flow);
        Assert.Same(shared.Costs, shared.Input.HeadingGraph!.Costs);
        Assert.Same(shared.Input.HeadingGraph.Regions.Span[0].Mesh, loaded.Read<PathNavigation<HeadingNavigationMesh>>(headingActor).Space);
        Assert.False(loaded.IsEnabled<NavigationGoal>(headingActor));
        Assert.True(NavigationComponents.HasPath<HeadingNavigationMesh, AsaSearchState>(loaded, headingActor));
        loaded.Enable<NavigationGoal>(headingActor);
        var headingPath = new NavigationSystems<HeadingNavigationMesh, AsaNavigationSearch, AsaSearchState, SaveMotion, SaveMotionReader>(loaded);
        for (int step = 0; step < 80 && loaded.Read<NavigationProgress>(headingActor).Status != NavigationStatus.Arrived; step++)
        {
            headingPath.Schedule(0.1f, 64).Steering.Complete();
            SaveMotion motion = loaded.Read<SaveMotion>(headingActor);
            NavigationSteering steering = loaded.Read<NavigationSteering>(headingActor);
            loaded.Replace(headingActor, new SaveMotion(motion.Position + steering.Velocity * 0.1f, steering.Velocity, steering.Heading));
            headingPath.ScheduleProgress(default).Complete();
        }
        Assert.Equal(NavigationStatus.Arrived, loaded.Read<NavigationProgress>(headingActor).Status);
        Assert.Equal(savedMotion, loaded.Read<SaveMotion>(actors[0]));
        Assert.Equal(savedAction, loaded.Read<SaveAction>(actors[0]));
        Assert.Equal(savedAction.Request, NavigationComponents.CaptureFlowTraversal(loaded, actors[0]).Request.RequestId);
        Assert.Contains(shared.Input.HeadingGraph.Links.GetPassage(71).InUse.ToArray(), user => user.Entity == actors[0]);
        var loadedNavigation = new NavigationSystems<SaveMotion, SaveMotionReader>(loaded);
        CompleteTrip();
        Assert.All(actors, actor => Assert.Equal(1, loaded.Read<SaveAction>(actor).Count));

        shared.SetGoals(new NavigationFlowGoal[] { new(new(2, 0, 6), 0.05f, 0) });
        new NavigationFlowSystem<ReverseDijkstraFlow, ReverseDijkstraFlowState>(shared, scheduler).Schedule().Complete();
        MoveActions(loaded, loadedNavigation, actors);
        CompleteTrip();
        Assert.All(actors, actor =>
        {
            Assert.Equal(2, loaded.Read<SaveAction>(actor).Count);
            Assert.True(loaded.Read<SaveAction>(actor).Request > savedAction.Request);
        });

        void CompleteTrip()
        {
            for (int frame = 0; frame < 160 && actors.Any(actor => loaded.Read<NavigationProgress>(actor).Status != NavigationStatus.Arrived); frame++)
            {
                MoveActions(loaded, loadedNavigation, actors);
                Assert.InRange(shared.Input.HeadingGraph!.Links.GetPassage(71).InUse.Length, 0, 1);
            }
            Assert.All(actors, actor => Assert.Equal(NavigationStatus.Arrived, loaded.Read<NavigationProgress>(actor).Status));
        }
        void Add(AssetEntry entry) => manifest.AddAsset(entry.AssetGuid, entry.Name, Path.GetFileName(entry.Path), entry.AssetType,
            entry.SchemaFingerprint, version: entry.Version);
    }

    private static void MoveActions(World world, NavigationSystems<SaveMotion, SaveMotionReader> navigation, Entity[] actors)
    {
        const float step = 0.1f;
        navigation.Schedule(step).Steering.Complete();
        foreach (Entity actor in actors)
        {
            SaveMotion motion = world.Read<SaveMotion>(actor);
            NavigationTraversalRequest request = world.Read<NavigationTraversalRequest>(actor);
            if (request.Status is NavigationTraversalStatus.Requested or NavigationTraversalStatus.Executing)
            {
                SaveAction action = world.Read<SaveAction>(actor);
                if (action.Request != request.RequestId) action = new(request.RequestId, motion.Position, 0, action.Count + 1);
                action = action with { Elapsed = action.Elapsed + step };
                float progress = MathF.Min(1, action.Elapsed / request.Parameters.X);
                Vector3 position = Vector3.Lerp(action.Start, request.Exit, progress);
                motion = motion with { Position = position, Velocity = (position - motion.Position) / step };
                world.Replace(actor, action);
                world.Replace(actor, new NavigationTraversalResult(request.RequestId,
                    progress == 1 ? NavigationTraversalExecution.Completed : NavigationTraversalExecution.Running, progress));
            }
            else
            {
                NavigationSteering steering = world.Read<NavigationSteering>(actor);
                Vector3 velocity = steering.Active ? steering.Velocity : Vector3.Zero;
                motion = new(motion.Position + velocity * step, velocity, steering.Active ? steering.Heading : motion.Heading);
            }
            world.Replace(actor, motion);
        }
        navigation.ScheduleProgress(default).Complete();
    }
}

public readonly record struct SaveAction(ulong Request, Vector3 Start, float Elapsed, int Count) : IComponent;
public readonly struct SaveActionCodec : IComponentCodec<SaveAction>
{
    public void Write(ref DataWriter writer, in SaveAction value)
    {
        writer.WriteUInt64(value.Request); writer.WriteSingle(value.Start.X); writer.WriteSingle(value.Start.Y); writer.WriteSingle(value.Start.Z);
        writer.WriteSingle(value.Elapsed); writer.WriteInt32(value.Count);
    }
    public void Read(ref DataReader reader, out SaveAction value) => value = new(reader.ReadUInt64(),
        new(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle()), reader.ReadSingle(), reader.ReadInt32());
}
