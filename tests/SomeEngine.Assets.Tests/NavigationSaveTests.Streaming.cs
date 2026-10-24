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
    public async Task StreamedFlowsRestoreSharedCostsAndTheClosedPassageAfterItsTileWasRemoved()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 2, ManagedPayloadPolicy = ManagedPayloadPolicy.Allow });
        var profile = new NavigationProfile([new Vector2(-0.1f), new(0.1f, -0.1f), new(0.1f), new(-0.1f, 0.1f)], 1);
        var policy = new NavigationPolicy();
        NavigationGrid original = ExampleGrid(profile);
        NavigationGridCell[] tiledCells = original.Cells.ToArray().Select(cell => cell with { Tile = new((int)cell.Position.X / 3, 0) }).ToArray();
        NavigationGrid grid = NavigationGridRegionBuilder.Schedule(new(original.Bounds, original.CellSize, tiledCells, original.Edges, original.Shape),
            profile, policy, scheduler: scheduler).Complete().Grid;
        var resources = new NavigationSaveResources();
        var manifest = new AssetManifest();
        NavigationGridSelection selection;
        AssetId<NavigationAsset> assetId;
        string path = Path.Combine(_directory, "streamed.navigation.asset");
        using (var profileAsset = new NavigationProfileAsset())
        using (var policyAsset = new NavigationPolicyAsset())
        using (var asset = new NavigationAsset())
        using (var profileBlock = new NavigationProfileBlock())
        using (var policyBlock = new NavigationPolicyBlock())
        using (var gridBlock = new NavigationGridBlock())
        {
            selection = new(profileAsset.Reference, "walk");
            profileBlock.SetProfile(profile); policyBlock.SetPolicy(policy); gridBlock.SetGrid(selection, grid);
            profileAsset.SetBlock(profileBlock); policyAsset.SetBlock(policyBlock); asset.SetBlock(gridBlock);
            Add(await profileAsset.SaveAsync(Path.Combine(_directory, "streamed.navigation-profile.asset")));
            Add(await policyAsset.SaveAsync(Path.Combine(_directory, "streamed.navigation-policy.asset")));
            Add(await asset.SaveAsync(path));
            assetId = asset.Reference;
            resources.Register(profile, profileAsset.Reference); resources.Register(policy, policyAsset.Reference);
        }
        var registry = new SerializationRegistry().Register<SaveMotion, SaveMotionCodec>(
            new(Guid.Parse("f943af78-c106-4f68-a4a7-dc05d7008dcc"), "navigation-save-motion", 1));
        using var saved = new MemoryStream();
        Entity first, second;
        using (var asset = await NavigationAsset.OpenAsync(path))
        {
            NavigationGridBlock block = await asset.GetBlockAsync<NavigationGridBlock>();
            grid = await block.CreateAsync(selection);
            resources.Register(grid, assetId, selection);
            NavigationCosts baseline = NavigationCostSystem.Schedule(grid, policy, scheduler: scheduler).Complete().Costs;
            float[] cells = baseline.CellCosts.ToArray(), edges = baseline.EdgeCosts.ToArray();
            int blocked = grid.FindCell(new(1.5f, 0, 1.5f));
            for (int cell = 0; cell < cells.Length; cell++)
                cells[cell] = cell == blocked ? float.PositiveInfinity : grid.Cells.Span[cell].Position.Z > 2 ? 8 : 1;
            for (int cell = 0; cell < cells.Length; cell++)
            {
                NavigationGridCell from = grid.Cells.Span[cell];
                for (int edge = from.EdgeStart; edge < from.EdgeStart + from.EdgeCount; edge++)
                    edges[edge] = cell == blocked || grid.Edges.Span[edge].ToCell == blocked ? float.PositiveInfinity : cells[cell];
            }
            var costs = new NavigationCosts(grid, policy, cells, edges, version: 7);
            var firstFlow = new NavigationFlow(costs, profile, [new(new(4.5f, 0, 1.5f), 0.05f)]);
            var secondFlow = new NavigationFlow(costs, profile, [new(new(4.5f, 0, 0.5f), 0.05f)]);
            using var source = block.CreateTileSource(selection);
            using var firstStream = new NavigationGridStreaming(firstFlow, source, scheduler) { ResidentTileBudget = 1 };
            using var secondStream = new NavigationGridStreaming(secondFlow, source, scheduler) { ResidentTileBudget = 1 };
            firstStream.Interests.Add(new(new(4.5f, 0, 1.5f), 0.1f));
            secondStream.Interests.Add(new(new(4.5f, 0, 0.5f), 0.1f));
            firstStream.Update(); firstStream.Applied.Complete();
            secondStream.Update(); secondStream.Applied.Complete();
            var solver = new NavigationFlowSystem<ReverseDijkstraFlow, ReverseDijkstraFlowState>(firstFlow, scheduler);
            firstFlow.SetGoals([new(new(4.5f, 0, 1.5f), 0.1f)]);
            solver.Schedule().Complete();
            Assert.Equal(new NavigationTileCoordinate(1, 0), Assert.Single(firstFlow.Costs.Grid.Tiles.ToArray()).Tile);
            using World world = WorldJobs.CreateWorld(scheduler: scheduler);
            first = world.CreateEntity(new SaveMotion(new(0.5f, 0, 1.5f), default, 0));
            second = world.CreateEntity(new SaveMotion(new(0.5f, 0, 0.5f), default, 0));
            NavigationComponents.AddFlow(world, first, firstFlow, 2);
            NavigationComponents.AddFlow(world, second, secondFlow, 2);
            NavigationSave.Write(saved, world, registry, resources);
        }

        saved.Position = 0;
        NavigationSaveResult restored;
        await using (var loader = new AssetLoader(new LooseAssetStorage(_directory, manifest)))
            restored = await NavigationSave.ReadAsync(saved, registry, loader, scheduler);
        using World loaded = restored.World;
        NavigationFlow firstRestored = loaded.Read<FlowFollower>(first).Flow, secondRestored = loaded.Read<FlowFollower>(second).Flow;
        Assert.Equal(7ul, firstRestored.Costs.Version);
        Assert.Equal(float.PositiveInfinity, firstRestored.Costs.CellCosts.Span[firstRestored.Costs.Grid.FindCell(new(1.5f, 0, 1.5f))]);
        Assert.Equal(8, firstRestored.Costs.CellCosts.Span[firstRestored.Costs.Grid.FindCell(new(1.5f, 0, 2.5f))]);
        Assert.Same(firstRestored.Costs, secondRestored.Costs);

        using (var asset = await NavigationAsset.OpenAsync(path))
        using (NavigationGridTileSource source = (await asset.GetBlockAsync<NavigationGridBlock>()).CreateTileSource(selection))
        using (var streaming = new NavigationGridStreaming(firstRestored, source, scheduler) { ResidentTileBudget = 1 })
        {
            var interest = new NavigationInterest(new(4.5f, 0, 1.5f), 0.1f);
            streaming.Interests.Add(interest);
            streaming.Update(); streaming.Applied.Complete();
            streaming.ResidentTileBudget = 2;
            interest.Position = new(0.5f, 0, 1.5f);
            interest.Direction = Vector3.UnitX; interest.LookAheadDistance = 4;
            streaming.Update();
            foreach (NavigationGridStreamingRead read in streaming.Reads) read.Completion.Complete();
            streaming.Update();
            new NavigationFlowSystem<ReverseDijkstraFlow, ReverseDijkstraFlowState>(firstRestored, scheduler)
                .Schedule(dependency: streaming.Applied).Complete();
            var navigation = new NavigationSystems<SaveMotion, SaveMotionReader>(loaded);
            var positions = new List<Vector3>();
            for (int frame = 0; frame < 100 && new[] { first, second }.Any(actor => loaded.Read<NavigationProgress>(actor).Status != NavigationStatus.Arrived); frame++)
            {
                navigation.ScheduleProgress(new ConsumeSavedSteering(0.1f).ScheduleParallel(loaded, navigation.Schedule(0.1f).Steering)).Complete();
                positions.Add(loaded.Read<SaveMotion>(first).Position);
            }
            Assert.Contains(positions, position => position.X > 1 && position.X < 2 && position.Z < 1);
            Assert.All(new[] { first, second }, actor => Assert.Equal(NavigationStatus.Arrived, loaded.Read<NavigationProgress>(actor).Status));
            using var savedAgain = new MemoryStream();
            NavigationSave.Write(savedAgain, loaded, registry, restored.Resources);
            Assert.True(savedAgain.Length > 0);
        }

        void Add(AssetEntry entry) => manifest.AddAsset(entry.AssetGuid, entry.Name, Path.GetFileName(entry.Path), entry.AssetType,
            entry.SchemaFingerprint, version: entry.Version);
    }
}
