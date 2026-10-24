using System.Numerics;
using SomeEngine.ECS;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;
using SomeEngine.Navigation;

namespace SomeEngine.Assets.Tests;

public sealed class HeadingNavigationMeshTests
{
    [Fact]
    public async Task HeadingTilesResumeOneRequestAfterBackgroundReadsAndPathsKeepTheirDataAfterEviction()
    {
        string directory = Path.Combine(Path.GetTempPath(), "SomeEngine-HeadingStreaming-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 3 });
            NavigationProfile profile = Profile();
            var policy = new NavigationPolicy();
            HeadingNavigationMesh mesh = HeadingNavigationMeshBuilder.Schedule(Scene(), profile, policy,
                new() { ChannelCount = 16 }, scheduler: scheduler).Complete().Mesh;
            mesh = HeadingNavigationRegionBuilder.Schedule(mesh, profile, policy, scheduler: scheduler).Complete().Mesh;
            string assetPath = Path.Combine(directory, "turning.navigation.asset");
            var selection = new NavigationMeshSelection(new(Guid.NewGuid()), "main");
            using (var asset = new NavigationAsset())
            using (var block = new HeadingNavigationMeshBlock())
            {
                block.SetMesh(selection, mesh); asset.SetBlock(block); await asset.SaveAsync(assetPath);
            }
            HeadingNavigationMeshTileSource source;
            using (var asset = await NavigationAsset.OpenAsync(assetPath))
                source = (await asset.GetBlockAsync<HeadingNavigationMeshBlock>()).CreateTileSource(selection);
            using (source)
            {
                var first = new NavigationTileCoordinate(0, 0);
                HeadingNavigationMesh initial = source.ScheduleRead(first, scheduler).Complete();
                var scene = new HeadingNavigationScene(new(source.Graph,
                    new HeadingNavigationMeshTile[] { new(first, initial, source.Directory.Span[0].Bounds) }));
                using var controlled = new ControlledHeadingSource(source, release.Task);
                using var streaming = new HeadingNavigationStreaming(scene, controlled, scheduler);
                var interest = new NavigationInterest(new(3, 0, 2), 0.1f) { LookAheadDistance = 3 };
                streaming.Interests.Add(interest);
                using World world = WorldJobs.CreateWorld(scheduler: scheduler);
                var navigation = new NavigationSystems<HeadingNavigationScene, HeadingSceneNavigationSearch, HeadingSceneSearchState, HeadingMotion, HeadingReader>(world);
                var actor = world.CreateEntity(new HeadingMotion(new(3, 0, 2), default, 0));
                navigation.AddAgent(actor, scene, profile, policy, new(new Vector3(9, 0, 7), 0.05f, MathF.PI / 2, 0.01f), 2);
                ulong version = 0;
                void Frame()
                {
                    HeadingMotion before = world.Read<HeadingMotion>(actor);
                    interest.Position = before.Position; interest.Path = world.Read<NavigationPathResult>(actor).Path;
                    streaming.Update();
                    navigation.Schedule(0.1f, 1024, streaming.Applied).Steering.Complete();
                    NavigationSteering steering = world.Read<NavigationSteering>(actor);
                    float heading = before.Heading + Math.Clamp(NavigationHeading.Difference(steering.Heading, before.Heading),
                        -profile.MaximumAngularSpeed * 0.1f, profile.MaximumAngularSpeed * 0.1f);
                    world.Replace(actor, new HeadingMotion(before.Position + steering.Velocity * 0.1f, steering.Velocity, heading));
                    navigation.ScheduleProgress(default).Complete();
                    ulong current = world.Read<NavigationProgress>(actor).RequestVersion;
                    if (version == 0) version = current;
                    Assert.Equal(version, current);
                }
                try
                {
                    for (int frame = 0; frame < 600 && world.Read<NavigationProgress>(actor).Status != NavigationStatus.WaitingForTiles; frame++) Frame();
                    Assert.Equal(NavigationStatus.WaitingForTiles, world.Read<NavigationProgress>(actor).Status);
                    Assert.True(Vector3.Distance(world.Read<HeadingMotion>(actor).Position, new(3, 0, 2)) > 1);
                    NavigationPath retained = world.Read<NavigationPathResult>(actor).Path!;
                    Assert.NotEmpty(retained.RequiredTiles.ToArray());
                    interest.Path = retained; streaming.Update();
                    release.SetResult();
                    foreach (NavigationStreamingRead read in streaming.Reads) read.Completion.Complete();
                    for (int frame = 0; frame < 600 && world.Read<NavigationProgress>(actor).Status != NavigationStatus.Arrived; frame++)
                    {
                        Frame();
                        foreach (NavigationStreamingRead read in streaming.Reads) read.Completion.Complete();
                    }
                    Assert.Equal(NavigationStatus.Arrived, world.Read<NavigationProgress>(actor).Status);
                    Assert.InRange(MathF.Abs(NavigationHeading.Difference(MathF.PI / 2, world.Read<HeadingMotion>(actor).Heading)), 0, 0.01f);
                    streaming.Interests.Clear(); streaming.ResidentTileBudget = 0;
                    streaming.Update(); streaming.Applied.Complete();
                    Assert.Empty(scene.Content.Tiles.ToArray());
                    Assert.True(retained.TrySampleHeight(retained.Poses.Span[0].Position, out _));
                }
                finally { release.TrySetResult(); }
            }
        }
        finally { release.TrySetResult(); Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task ReopenedProfileVariantsRetainTheirPoseAndTurningPermissionsAfterTheAssetCloses()
    {
        string directory = Path.Combine(Path.GetTempPath(), "SomeEngine-HeadingMesh-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
            NavigationSurface surface = NavigationSurfaceBuilder.Schedule(Scene(), scheduler: scheduler).Complete().Surface;
            var policy = new NavigationPolicy();
            var small = new NavigationProfile([new(-0.1f, -0.1f), new(0.1f, -0.1f), new(0.1f, 0.1f), new(-0.1f, 0.1f)], 1.5f);
            HeadingNavigationMesh longMesh = HeadingNavigationMeshBuilder.Schedule(surface, Profile(), policy, scheduler: scheduler).Complete().Mesh;
            longMesh = HeadingNavigationRegionBuilder.Schedule(longMesh, Profile(), policy, scheduler: scheduler).Complete().Mesh;
            HeadingNavigationMesh smallMesh = HeadingNavigationMeshBuilder.Schedule(surface, small, policy, scheduler: scheduler).Complete().Mesh;
            var longSelection = new NavigationMeshSelection(new(Guid.NewGuid()), "main");
            var smallSelection = new NavigationMeshSelection(new(Guid.NewGuid()), "main");
            string path = Path.Combine(directory, "turning.navigation.asset");
            using (var asset = new NavigationAsset())
            using (var block = new HeadingNavigationMeshBlock())
            {
                block.SetMesh(longSelection, longMesh);
                block.SetMesh(smallSelection, smallMesh);
                asset.SetBlock(block);
                await asset.SaveAsync(path);
            }
            using (var asset = await NavigationAsset.OpenAsync(path))
            {
                HeadingNavigationMeshBlock block = await asset.GetBlockAsync<HeadingNavigationMeshBlock>();
                longMesh = await block.CreateAsync(longSelection);
                smallMesh = await block.CreateAsync(smallSelection);
            }

            Vector3 entrance = new(3, 0, 2), turning = new(3, 0, 7);
            Assert.True(longMesh.IsPoseFeasible(entrance, 0));
            Assert.False(longMesh.IsPoseFeasible(entrance, MathF.PI / 2));
            Assert.False(longMesh.IsRotationFeasible(entrance, 0, MathF.PI / 2));
            Assert.True(smallMesh.IsRotationFeasible(entrance, 0, MathF.PI / 2));
            Assert.NotEmpty(longMesh.GetRotationConnections(turning, 0));
            Assert.Contains(longMesh.Graph.Edges.ToArray(), edge => edge.Action == HeadingNavigationAction.Rotation);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void LongFootprintUsesAlignedEntrancesAndTurnsInTheWiderRegion()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
        HeadingNavigationMesh mesh = HeadingNavigationMeshBuilder.Schedule(Scene(), Profile(), new NavigationPolicy(),
            new() { ChannelCount = 32 }, scheduler: scheduler).Complete().Mesh;
        Vector3 entrance = new(3, 0, 2), turning = new(3, 0, 7), exit = new(9, 0, 7);

        Assert.True(mesh.IsPoseFeasible(entrance, 0));
        Assert.True(mesh.IsPoseFeasible(entrance, MathF.PI));
        Assert.False(mesh.IsPoseFeasible(entrance, MathF.PI / 2));
        Assert.Contains(mesh.GetAllowedHeadings(entrance).ToArray(), interval => interval.Contains(0.04f));
        Assert.True(mesh.IsPoseFeasible(exit, MathF.PI / 2));
        Assert.False(mesh.IsPoseFeasible(exit, 0));
        Assert.NotEmpty(mesh.GetTranslationConnections(entrance, 0));
        Assert.Empty(mesh.GetTranslationConnections(entrance, MathF.PI / 2));
        Assert.True(mesh.IsRotationFeasible(turning, 0, MathF.PI / 2));
        Assert.False(mesh.IsRotationFeasible(entrance, 0, MathF.PI));
        Assert.NotEmpty(mesh.GetRotationConnections(turning, 0));
    }

    private static NavigationProfile Profile() => new(
        [new(-0.2f, -1.2f), new(0.2f, -1.2f), new(0.3f, 0.7f), new(0, 1.2f), new(-0.3f, 0.7f)],
        1.5f, maximumStepHeight: 0.3f);

    private sealed class ControlledHeadingSource(NavigationTileSource<HeadingNavigationMesh> source, Task released) : NavigationTileSource<HeadingNavigationMesh>
    {
        private readonly NavigationTileSource<HeadingNavigationMesh> _source = source.Retain();
        public override ReadOnlyMemory<NavigationRegionTile> Directory => _source.Directory;
        public override NavigationTileSource<HeadingNavigationMesh> Retain() => new ControlledHeadingSource(_source, released);
        public override async ValueTask<NavigationTileBytes> ReadAsync(NavigationTileCoordinate tile, CancellationToken cancellationToken = default)
        {
            await released.WaitAsync(cancellationToken);
            return await _source.ReadAsync(tile, cancellationToken);
        }
        public override HeadingNavigationMesh Decode(NavigationTileCoordinate tile, NavigationTileBytes bytes) => _source.Decode(tile, bytes);
        public override void Dispose() => _source.Dispose();
    }
    private readonly record struct HeadingMotion(Vector3 Position, Vector3 Velocity, float Heading) : IComponent;
    private readonly struct HeadingReader : INavigationMotionReader<HeadingMotion>
    {
        public static NavigationMotion Read(in HeadingMotion motion) => new(motion.Position, motion.Velocity, motion.Heading, true);
    }

    // These dimensions and the tapered five-vertex footprint are test examples of the general shape input.
    private static NavigationBuildInput Scene()
    {
        var positions = new List<Vector3>();
        var triangles = new List<NavigationTriangle>();
        void Floor(float minX, float maxX, float minZ, float maxZ)
        {
            int first = positions.Count;
            positions.AddRange([new(minX, 0, minZ), new(minX, 0, maxZ), new(maxX, 0, maxZ), new(maxX, 0, minZ)]);
            triangles.Add(new(first, first + 1, first + 2, 0));
            triangles.Add(new(first, first + 2, first + 3, 0));
        }
        Floor(2, 4, 0, 4);
        Floor(0, 6, 4, 10);
        Floor(6, 12, 6, 8);
        return new([new(new NavigationGeometry(positions.ToArray(), triangles.ToArray()), Matrix4x4.Identity, 1)],
            new(new(0, -1, 0), new(12, 3, 10)), new() { CellSize = 0.2f, CellHeight = 0.05f, TileSize = 32 });
    }
}
