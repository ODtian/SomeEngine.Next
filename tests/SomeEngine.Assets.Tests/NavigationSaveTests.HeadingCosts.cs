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
    public async Task HeadingCostsRestoreDistinctStatesAndTheChosenTraversalForSharedFlows()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 3, ManagedPayloadPolicy = ManagedPayloadPolicy.Allow });
        var profile = new NavigationProfile([new(-0.2f, -0.8f), new(0.2f, -0.8f), new(0.2f, 0.8f), new(-0.2f, 0.8f)],
            1.5f, maximumSpeed: 2, maximumAngularSpeed: MathF.PI);
        var policy = new NavigationPolicy();
        HeadingNavigationFlowRegion[] regions = HeadingNavigationFlowAssetTests.ExampleRegions();
        // These parallel actions are an example of game-selected transitions with independently edited costs.
        var links = new NavigationLinkSet(new NavigationLink[]
        {
            new(71, new(10, 1), new(20, 2), 9, Parameters: new(0.3f, 0, 0, 0), Capacity: 1),
            new(72, new(10, 1), new(20, 2), 10, Cost: 4, Parameters: new(0.3f, 0, 0, 0), Capacity: 1),
        });
        HeadingNavigationFlowGraph graph = HeadingNavigationFlowBuilder.Schedule(regions, links, profile, policy,
            version: 4, scheduler: scheduler).Complete().Graph;
        float[] cells = graph.Costs.CellCosts.ToArray(), edges = graph.Costs.EdgeCosts.ToArray();
        int east = State(graph, 1), north = State(graph, 0);
        cells[east] = 7; cells[north] = 2;
        for (int edge = 0; edge < edges.Length; edge++)
            if (graph.Transitions.Span[edge].Traversal?.Connection == 71) edges[edge] = float.PositiveInfinity;
        var costs = new NavigationCosts(graph.Costs.Grid, policy, cells, edges, version: 9);
        var firstFlow = new NavigationFlow(graph, profile, new NavigationFlowGoal[] { new(new(2, 0, 12), 0.05f, 0) });
        var secondFlow = new NavigationFlow(graph, profile, new NavigationFlowGoal[] { new(new(2, 0, 13), 0.05f, 0) });
        firstFlow.SetCosts(costs); secondFlow.SetCosts(costs);
        new NavigationFlowSystem<ReverseDijkstraFlow, ReverseDijkstraFlowState>(firstFlow, scheduler).Schedule().Complete();
        new NavigationFlowSystem<ReverseDijkstraFlow, ReverseDijkstraFlowState>(secondFlow, scheduler).Schedule().Complete();
        Assert.Equal(72, firstFlow.Field!.Sample(new(new(2, 0, 7), 0)).Traversal!.Value.Connection);
        var resources = new NavigationSaveResources();
        var manifest = new AssetManifest();
        using (var profileAsset = new NavigationProfileAsset())
        using (var policyAsset = new NavigationPolicyAsset())
        using (var asset = new NavigationAsset())
        using (var profileBlock = new NavigationProfileBlock())
        using (var policyBlock = new NavigationPolicyBlock())
        using (var headingBlock = new HeadingNavigationMeshBlock())
        using (var linkBlock = new NavigationLinkBlock())
        {
            profileBlock.SetProfile(profile); policyBlock.SetPolicy(policy); linkBlock.SetLinks(links);
            headingBlock.SetMesh(new(profileAsset.Reference, "entry"), regions[0].Mesh);
            headingBlock.SetMesh(new(profileAsset.Reference, "exit"), regions[1].Mesh);
            profileAsset.SetBlock(profileBlock); policyAsset.SetBlock(policyBlock);
            asset.SetBlock(headingBlock); asset.SetBlock(linkBlock);
            Add(await profileAsset.SaveAsync(Path.Combine(_directory, "heading-cost.navigation-profile.asset")));
            Add(await policyAsset.SaveAsync(Path.Combine(_directory, "heading-cost.navigation-policy.asset")));
            Add(await asset.SaveAsync(Path.Combine(_directory, "heading-cost.navigation.asset")));
            resources.Register(profile, profileAsset.Reference); resources.Register(policy, policyAsset.Reference);
            resources.Register(regions[0].Mesh, asset.Reference, new(profileAsset.Reference, "entry"));
            resources.Register(regions[1].Mesh, asset.Reference, new(profileAsset.Reference, "exit"));
            resources.Register(links, asset.Reference);
        }
        var registry = new SerializationRegistry()
            .Register<SaveMotion, SaveMotionCodec>(new(Guid.Parse("f943af78-c106-4f68-a4a7-dc05d7008dcc"), "navigation-save-motion", 1))
            .Register<SaveAction, SaveActionCodec>(new(Guid.Parse("35602cb1-c236-4d23-8e03-74baa01ad7a8"), "navigation-save-action", 1));
        Entity[] actors;
        using var saved = new MemoryStream();
        using (World world = WorldJobs.CreateWorld(scheduler: scheduler))
        {
            actors = [world.CreateEntity(new SaveMotion(new(2, 0, 2), default, MathF.PI / 2)),
                world.CreateEntity(new SaveMotion(new(2.1f, 0, 2), default, MathF.PI / 2))];
            NavigationComponents.AddFlow(world, actors[0], firstFlow, 2);
            NavigationComponents.AddFlow(world, actors[1], secondFlow, 2);
            foreach (Entity actor in actors) world.Add(actor, default(SaveAction));
            NavigationSave.Write(saved, world, registry, resources);
        }
        saved.Position = 0;
        NavigationSaveResult restored;
        await using (var loader = new AssetLoader(new LooseAssetStorage(_directory, manifest)))
            restored = await NavigationSave.ReadAsync(saved, registry, loader, scheduler);
        using World loaded = restored.World;
        NavigationFlow first = loaded.Read<FlowFollower>(actors[0]).Flow, second = loaded.Read<FlowFollower>(actors[1]).Flow;
        HeadingNavigationFlowGraph restoredGraph = first.Input.HeadingGraph!;
        Assert.Same(first.Costs, second.Costs);
        Assert.Same(restoredGraph, second.Input.HeadingGraph);
        Assert.Equal(9ul, first.Costs.Version);
        Assert.Equal(7, first.Costs.CellCosts.Span[State(restoredGraph, 1)]);
        Assert.Equal(2, first.Costs.CellCosts.Span[State(restoredGraph, 0)]);
        Assert.Equal(72, first.Field!.Sample(new(new(2, 0, 7), 0)).Traversal!.Value.Connection);
        var navigation = new NavigationSystems<SaveMotion, SaveMotionReader>(loaded);
        var selected = new List<int>();
        for (int frame = 0; frame < 160 && actors.Any(actor => loaded.Read<NavigationProgress>(actor).Status != NavigationStatus.Arrived); frame++)
        {
            MoveActions(loaded, navigation, actors);
            foreach (Entity actor in actors)
            {
                NavigationTraversalRequest request = loaded.Read<NavigationTraversalRequest>(actor);
                if (request.Status is NavigationTraversalStatus.Requested or NavigationTraversalStatus.Executing)
                    selected.Add(request.Connection);
            }
        }
        Assert.NotEmpty(selected);
        Assert.All(selected, connection => Assert.Equal(72, connection));
        Assert.All(actors, actor =>
        {
            Assert.Equal(1, loaded.Read<SaveAction>(actor).Count);
            Assert.Equal(NavigationStatus.Arrived, loaded.Read<NavigationProgress>(actor).Status);
        });
        using var savedAgain = new MemoryStream();
        NavigationSave.Write(savedAgain, loaded, registry, restored.Resources);
        Assert.True(savedAgain.Length > 0);

        void Add(AssetEntry entry) => manifest.AddAsset(entry.AssetGuid, entry.Name, Path.GetFileName(entry.Path), entry.AssetType,
            entry.SchemaFingerprint, version: entry.Version);
        static int State(HeadingNavigationFlowGraph value, int channel) => Enumerable.Range(0, value.Nodes.Length).Single(index =>
            value.Nodes.Span[index] is { Region: 10, Polygon: 0 } node && node.Channel == channel && node.Pose.Position == new Vector3(2, 0, 2));
    }
}
