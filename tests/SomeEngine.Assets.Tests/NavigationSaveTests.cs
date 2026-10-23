using System.Numerics;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Serialization;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;
using SomeEngine.Navigation;

namespace SomeEngine.Assets.Tests;

public sealed partial class NavigationSaveTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "SomeEngine-NavigationSave-" + Guid.NewGuid().ToString("N"));
    public NavigationSaveTests() => Directory.CreateDirectory(_directory);
    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public async Task DurableRestoreKeepsSharedResourcesGoalsAndPassageChangesWhileActorsContinueMoving()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 3, ManagedPayloadPolicy = ManagedPayloadPolicy.Allow });
        var manifest = new AssetManifest();
        var profile = new NavigationProfile([new Vector2(-0.1f), new(0.1f, -0.1f), new(0.1f), new(-0.1f, 0.1f)], 1);
        var policy = new NavigationPolicy();
        NavigationMesh mesh = ExampleMesh();
        NavigationGrid grid = ExampleGrid(profile);
        NavigationCosts costs = NavigationCostSystem.Schedule(grid, policy, scheduler: scheduler).Complete().Costs;
        var flow = new NavigationFlow(costs, profile, [new(new(4.5f, 0, 1.5f), 0.1f)]);
        var sources = new NavigationSaveResources();
        using (var profileAsset = new NavigationProfileAsset())
        using (var policyAsset = new NavigationPolicyAsset())
        using (var navigationAsset = new NavigationAsset())
        using (var flowAsset = new NavigationFlowAsset())
        using (var profileBlock = new NavigationProfileBlock())
        using (var policyBlock = new NavigationPolicyBlock())
        using (var meshBlock = new NavigationMeshBlock())
        using (var gridBlock = new NavigationGridBlock())
        using (var flowBlock = new NavigationFlowBlock())
        {
            profileBlock.SetProfile(profile);
            policyBlock.SetPolicy(policy);
            meshBlock.SetMesh(new(profileAsset.Reference, "walk"), mesh);
            gridBlock.SetGrid(new(profileAsset.Reference, "walk"), grid);
            profileAsset.SetBlock(profileBlock);
            policyAsset.SetBlock(policyBlock);
            navigationAsset.SetBlock(meshBlock);
            navigationAsset.SetBlock(gridBlock);
            Add(await profileAsset.SaveAsync(Path.Combine(_directory, "actor.navigation-profile.asset")));
            Add(await policyAsset.SaveAsync(Path.Combine(_directory, "actor.navigation-policy.asset")));
            Add(await navigationAsset.SaveAsync(Path.Combine(_directory, "world.navigation.asset")));
            flowBlock.SetFlow(new(navigationAsset.Reference, profileAsset.Reference, policyAsset.Reference, "walk"), flow);
            flowAsset.SetBlock(flowBlock);
            Add(await flowAsset.SaveAsync(Path.Combine(_directory, "group.navigation-flow.asset")));
            sources.Register(profile, profileAsset.Reference);
            sources.Register(policy, policyAsset.Reference);
            sources.Register(mesh, navigationAsset.Reference, new(profileAsset.Reference, "walk"));
            sources.Register(grid, navigationAsset.Reference, new(profileAsset.Reference, "walk"));
            sources.Register(flow, flowAsset.Reference);
        }
        var registry = new SerializationRegistry().Register<SaveMotion, SaveMotionCodec>(
            new(Guid.Parse("f943af78-c106-4f68-a4a7-dc05d7008dcc"), "navigation-save-motion", 1));
        using var stream = new MemoryStream();
        Entity independent, first, second, target;
        SaveMotion savedFirst;
        using (World world = WorldJobs.CreateWorld(scheduler: scheduler))
        {
            target = world.CreateEntity(new SaveMotion(new(4.5f, 0, 2.5f), default, 0));
            independent = world.CreateEntity(new SaveMotion(new(0.5f, 0, 2.5f), default, 0));
            first = world.CreateEntity(new SaveMotion(new(0.5f, 0, 1.5f), default, 0));
            second = world.CreateEntity(new SaveMotion(new(0.5f, 0, 0.5f), default, 0));
            var paths = new NavigationSystems<NavigationMesh, PolyanyaNavigationSearch, PolyanyaSearchState, SaveMotion, SaveMotionReader>(world);
            var followers = new NavigationSystems<SaveMotion, SaveMotionReader>(world);
            paths.AddAgent(independent, mesh, profile, policy, new(target, 0.1f), 1);
            followers.AddFlowAgent(first, flow, 1);
            followers.AddFlowAgent(second, flow, 1);
            world.Add(target, new NavigationCrowdAgent(2));
            world.Add(second, new NavigationCrowdAgent(1.5f));
            world.Disable<NavigationCrowdAgent>(second);
            new NavigationFlowSystem<ReverseDijkstraFlow, ReverseDijkstraFlowState>(flow, scheduler).Schedule().Complete();
            for (int step = 0; step < 3; step++) Move(world, paths, followers);
            savedFirst = world.Read<SaveMotion>(first);
            float[] cells = costs.CellCosts.ToArray(), edges = costs.EdgeCosts.ToArray();
            cells[7] = float.PositiveInfinity;
            for (int cell = 0; cell < grid.Cells.Length; cell++)
                for (int edge = grid.Cells.Span[cell].EdgeStart; edge < grid.Cells.Span[cell].EdgeStart + grid.Cells.Span[cell].EdgeCount; edge++)
                    if (cell == 7 || grid.Edges.Span[edge].ToCell == 7) edges[edge] = float.PositiveInfinity;
            flow.SetCosts(new(grid, policy, cells, edges, version: 5));
            flow.SetGoals([new(new(4.5f, 0, 1.5f), 0.15f)]);
            NavigationSave.Write(stream, world, registry, sources);
        }
        stream.Position = 0;
        NavigationSaveResult restored;
        await using (var loader = new AssetLoader(new LooseAssetStorage(_directory, manifest)))
            restored = await NavigationSave.ReadAsync(stream, registry, loader, scheduler);
        using World loaded = restored.World;
        NavigationFlow shared = loaded.Read<FlowFollower>(first).Flow;
        Assert.Same(shared, loaded.Read<FlowFollower>(second).Flow);
        Assert.Same(loaded.Read<NavigationAgent>(independent).Profile, shared.Profile);
        Assert.Same(loaded.Read<NavigationAgent>(independent).Policy, shared.Costs.Policy);
        Assert.Equal(savedFirst, loaded.Read<SaveMotion>(first));
        Assert.Equal(target, loaded.Read<NavigationGoal>(independent).Target);
        NavigationCrowdField density = new NavigationCrowdSystem<SaveMotion, SaveMotionReader>(loaded, scheduler).Schedule(shared.Costs.Grid).Complete().Field;
        Assert.True(density.Sample(loaded.Read<SaveMotion>(target).Position).Density > 0);
        Assert.Equal(0, density.Sample(loaded.Read<SaveMotion>(second).Position).Density);
        Assert.Equal(1.5f, loaded.Read<NavigationCrowdAgent>(second).Weight);
        Assert.Equal(0.15f, shared.Goals.Span[0].ArrivalRange);
        Assert.Equal(5UL, shared.Costs.Version);
        Assert.True(float.IsPositiveInfinity(shared.Costs.CellCosts.Span[7]));
        Assert.True(NavigationComponents.HasPath<NavigationMesh, PolyanyaSearchState>(loaded, independent));
        var loadedPaths = new NavigationSystems<NavigationMesh, PolyanyaNavigationSearch, PolyanyaSearchState, SaveMotion, SaveMotionReader>(loaded);
        var loadedFollowers = new NavigationSystems<SaveMotion, SaveMotionReader>(loaded);
        for (int step = 0; step < 100 && new[] { independent, first, second }.Any(actor => loaded.Read<NavigationProgress>(actor).Status != NavigationStatus.Arrived); step++)
        {
            Move(loaded, loadedPaths, loadedFollowers);
            foreach (Entity actor in new[] { first, second })
            {
                Vector3 position = loaded.Read<SaveMotion>(actor).Position;
                Assert.True(position.X < 2 || position.X > 3 || position.Z < 1 || position.Z > 2);
            }
        }
        Assert.All(new[] { independent, first, second }, actor => Assert.Equal(NavigationStatus.Arrived, loaded.Read<NavigationProgress>(actor).Status));

        void Add(AssetEntry entry) => manifest.AddAsset(entry.AssetGuid, entry.Name, Path.GetFileName(entry.Path), entry.AssetType,
            entry.SchemaFingerprint, version: entry.Version);
    }

    private static void Move(World world,
        NavigationSystems<NavigationMesh, PolyanyaNavigationSearch, PolyanyaSearchState, SaveMotion, SaveMotionReader> paths,
        NavigationSystems<SaveMotion, SaveMotionReader> followers)
    {
        JobHandle ready = WorldJobs.CombineDependencies(world, [paths.Schedule(0.2f, 64).Steering, followers.Schedule(0.2f).Steering]);
        JobHandle moved = new ConsumeSavedSteering(0.2f).ScheduleParallel(world, ready);
        paths.ScheduleProgress(moved).Complete();
        followers.ScheduleProgress(moved).Complete();
    }

    [Fact]
    public async Task RestoredSourceGeometryRebuildsSceneAndSharedGridBeforeActorsResume()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 3, ManagedPayloadPolicy = ManagedPayloadPolicy.Allow });
        var manifest = new AssetManifest();
        var resources = new NavigationSaveResources();
        var profile = new NavigationProfile([new Vector2(-0.1f), new(0.1f, -0.1f), new(0.1f), new(-0.1f, 0.1f)], 1);
        var policy = new NavigationPolicy();
        var floorGeometry = new NavigationGeometry([new(-1, 0, -1), new(-1, 0, 7), new(13, 0, 7), new(13, 0, -1)], [new(0, 1, 2, 0), new(0, 2, 3, 0)]);
        var obstacleGeometry = new NavigationGeometry(
            [new(-0.5f, 0, -1), new(-0.5f, 0, 2.5f), new(0.5f, 0, 2.5f), new(0.5f, 0, -1),
             new(-0.5f, 3, -1), new(-0.5f, 3, 2.5f), new(0.5f, 3, 2.5f), new(0.5f, 3, -1)],
            [new(0, 2, 1, 0), new(0, 3, 2, 0), new(4, 5, 6, 0), new(4, 6, 7, 0), new(0, 4, 7, 0), new(0, 7, 3, 0),
             new(1, 2, 6, 0), new(1, 6, 5, 0), new(0, 1, 5, 0), new(0, 5, 4, 0), new(3, 7, 6, 0), new(3, 6, 2, 0)]);
        using (var profileAsset = new NavigationProfileAsset())
        using (var policyAsset = new NavigationPolicyAsset())
        using (var profileBlock = new NavigationProfileBlock())
        using (var policyBlock = new NavigationPolicyBlock())
        {
            profileBlock.SetProfile(profile); policyBlock.SetPolicy(policy);
            profileAsset.SetBlock(profileBlock); policyAsset.SetBlock(policyBlock);
            Add(await profileAsset.SaveAsync(Path.Combine(_directory, "dynamic.navigation-profile.asset")));
            Add(await policyAsset.SaveAsync(Path.Combine(_directory, "dynamic.navigation-policy.asset")));
            resources.Register(profile, profileAsset.Reference); resources.Register(policy, policyAsset.Reference);
        }
        await SaveGeometry(floorGeometry, "floor");
        await SaveGeometry(obstacleGeometry, "obstacle");
        var registry = new SerializationRegistry().Register<SaveMotion, SaveMotionCodec>(
            new(Guid.Parse("f943af78-c106-4f68-a4a7-dc05d7008dcc"), "navigation-save-motion", 1));
        using var stream = new MemoryStream();
        Entity independent, follower, obstacle;
        using (World world = WorldJobs.CreateWorld(scheduler: scheduler))
        {
            var scene = new NavigationScene(new(default, default, default));
            var region = new NavigationRegionSources(scene, 3, new(new(0, -1, 0), new(12, 4, 6)), profile,
                policy, new() { CellSize = 0.5f, CellHeight = 0.1f, TileSize = 8 });
            Entity floor = world.CreateEntity(new SaveMotion(default, default, 0));
            world.Add(floor, new NavigationSource(floorGeometry));
            obstacle = world.CreateEntity(new SaveMotion(new(5.5f, 0, 0), default, 0));
            world.Add(obstacle, new NavigationObstacle(obstacleGeometry, Version: 4));
            var collect = new NavigationSourceSystem<SaveMotion, SaveSourceReader>(world, region, scheduler);
            var grids = new NavigationGridBuildSystem(region, scheduler: scheduler);
            NavigationBuild<NavigationRegionUpdate> build = NavigationBuildSystem.Schedule(collect.Schedule(), scheduler);
            NavigationGridUpdate updated = grids.Schedule(build, NavigationApplySystem.Schedule(build, scheduler)).Complete();
            resources.RegisterRegion(region, grids);
            var flow = new NavigationFlow(updated.Costs, profile, [new(new(10.25f, 0, 1.25f), 0.05f)]);
            independent = world.CreateEntity(new SaveMotion(new(1.25f, 0, 1.25f), default, 0));
            follower = world.CreateEntity(new SaveMotion(new(1.25f, 0, 1.25f), default, 0));
            NavigationComponents.AddPath<NavigationScene, DStarLiteSearchState>(world, independent, scene, profile, policy,
                new(new Vector3(10.25f, 0, 1.25f), 0.05f), 2);
            NavigationComponents.AddFlow(world, follower, flow, 2);
            NavigationSave.Write(stream, world, registry, resources);
        }
        stream.Position = 0;
        NavigationSaveResult restored;
        await using (var loader = new AssetLoader(new LooseAssetStorage(_directory, manifest)))
            restored = await NavigationSave.ReadAsync(stream, registry, loader, scheduler,
                new NavigationSaveOptions().UseSources<SaveMotion, SaveSourceReader>());
        using World loaded = restored.World;
        Assert.Equal(4UL, loaded.Read<NavigationObstacle>(obstacle).Version);
        Assert.Equal(new Vector3(5.5f, 0, 0), loaded.Read<SaveMotion>(obstacle).Position);
        Assert.True(NavigationComponents.HasPath<NavigationScene, DStarLiteSearchState>(loaded, independent));
        NavigationScene restoredScene = loaded.Read<PathNavigation<NavigationScene>>(independent).Space;
        Assert.Null(restoredScene.Content.FindTile(new(5.5f, 0, 1.25f), out _));
        var paths = new NavigationSystems<NavigationScene, DStarLiteNavigationSearch, DStarLiteSearchState, SaveMotion, SaveMotionReader>(loaded);
        var followers = new NavigationSystems<SaveMotion, SaveMotionReader>(loaded);
        var positions = new List<Vector3>();
        for (int step = 0; step < 130 && new[] { independent, follower }.Any(actor => loaded.Read<NavigationProgress>(actor).Status != NavigationStatus.Arrived); step++)
        {
            JobHandle ready = WorldJobs.CombineDependencies(loaded, [paths.Schedule(0.1f, 128).Steering, followers.Schedule(0.1f).Steering]);
            JobHandle moved = new ConsumeSavedSteering(0.1f).ScheduleParallel(loaded, ready);
            paths.ScheduleProgress(moved).Complete(); followers.ScheduleProgress(moved).Complete();
            positions.Add(loaded.Read<SaveMotion>(follower).Position);
        }
        Assert.Contains(positions, position => position.X > 5 && position.X < 6 && position.Z > 2.5f);
        Assert.All(new[] { independent, follower }, actor => Assert.Equal(NavigationStatus.Arrived, loaded.Read<NavigationProgress>(actor).Status));

        void Add(AssetEntry entry) => manifest.AddAsset(entry.AssetGuid, entry.Name, Path.GetFileName(entry.Path), entry.AssetType,
            entry.SchemaFingerprint, version: entry.Version);
        async Task SaveGeometry(NavigationGeometry geometry, string name)
        {
            using var asset = new NavigationAsset(); using var block = new NavigationGeometryBlock();
            block.SetGeometry(geometry); asset.SetBlock(block);
            Add(await asset.SaveAsync(Path.Combine(_directory, name + ".navigation.asset")));
            resources.Register(geometry, asset.Reference);
        }
    }

    // This floor, grid and one closed middle cell are example save inputs.
    private static NavigationMesh ExampleMesh() => new(
        [new(0, 0, 0), new(5, 0, 0), new(5, 0, 3), new(0, 0, 3)], [0, 1, 2, 3], [new(0, 4, 0, 0)], []);

    private static NavigationGrid ExampleGrid(NavigationProfile profile)
    {
        var cells = new NavigationGridCell[15];
        var edges = new List<NavigationGridEdge>();
        for (int z = 0; z < 3; z++)
        for (int x = 0; x < 5; x++)
        {
            int first = edges.Count;
            foreach ((int dx, int dz) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
                if (x + dx >= 0 && x + dx < 5 && z + dz >= 0 && z + dz < 3) edges.Add(new((z + dz) * 5 + x + dx, 1));
            cells[z * 5 + x] = new(new(x + 0.5f, 0, z + 0.5f), first, edges.Count - first);
        }
        return new(new(Vector3.Zero, new(5, 2, 3)), 1, cells, edges.ToArray(), NavigationGridShape.FromProfile(profile));
    }
}

public readonly record struct SaveMotion(Vector3 Position, Vector3 Velocity, float Heading) : IComponent;
public readonly struct SaveMotionReader : INavigationMotionReader<SaveMotion>
{
    public static NavigationMotion Read(in SaveMotion motion) => new(motion.Position, motion.Velocity, motion.Heading, true);
}
public readonly struct SaveSourceReader : INavigationTransformReader<SaveMotion>
{
    public static Matrix4x4 Read(in SaveMotion motion) => Matrix4x4.CreateRotationY(motion.Heading) * Matrix4x4.CreateTranslation(motion.Position);
}
public readonly struct SaveMotionCodec : IComponentCodec<SaveMotion>
{
    public void Write(ref DataWriter writer, in SaveMotion value)
    {
        writer.WriteSingle(value.Position.X); writer.WriteSingle(value.Position.Y); writer.WriteSingle(value.Position.Z);
        writer.WriteSingle(value.Velocity.X); writer.WriteSingle(value.Velocity.Y); writer.WriteSingle(value.Velocity.Z); writer.WriteSingle(value.Heading);
    }
    public void Read(ref DataReader reader, out SaveMotion value) => value = new(
        new(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle()), new(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle()), reader.ReadSingle());
}
public partial struct ConsumeSavedSteering(float deltaTime) : IJobEntity
{
    private readonly float _deltaTime = deltaTime;
    public void Execute(in NavigationSteering steering, ref SaveMotion motion) =>
        motion = new(motion.Position + steering.Velocity * _deltaTime, steering.Velocity, steering.Heading);
}
