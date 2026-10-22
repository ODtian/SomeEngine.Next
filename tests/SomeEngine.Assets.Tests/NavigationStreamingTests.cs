using System.Numerics;
using System.Runtime.InteropServices;
using SomeEngine.ECS;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;
using SomeEngine.Navigation;

namespace SomeEngine.Assets.Tests;

public sealed class NavigationStreamingTests
{
    [Fact]
    public void SubmittedHeightSampleKeepsItsTileAfterTheResidenceBudgetRemovesTheDirectoryEntry()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
        var mesh = new NavigationMesh([new(0, 3.25f, 0), new(0, 3.25f, 2), new(4, 3.25f, 2), new(4, 3.25f, 0)],
            [0, 1, 2, 3], [new(0, 4, 0, 0)], []);
        var profile = new NavigationProfile([new(-0.1f, -0.1f), new(0.1f, -0.1f), new(0.1f, 0.1f), new(-0.1f, 0.1f)], 1);
        NavigationMesh built = NavigationRegionBuilder.Schedule(mesh, profile, new NavigationPolicy(), scheduler: scheduler).Complete().Mesh;
        NavigationMesh held = built.Tiles.Span[0].Mesh;
        var scene = new NavigationScene(new(new NavigationSceneRegion[] { new(1, Matrix4x4.Identity, built.RegionGraph!) },
            default, new NavigationSceneTile[] { new(1, default, held) }));
        using var streaming = new NavigationStreaming(scene, scheduler) { ResidentTileBudget = 0 };
        var gate = new SampleGate();
        float[] height = [float.NaN];
        JobHandle sample = scheduler.Schedule(new SampleHeight(held, height),
            [Read(held.Positions), Read(held.PolygonVertices), Read(held.Polygons), Read(held.DetailPositions), Read(held.DetailTriangles),
             JobResourceAccess.Write(scheduler.GetResourceToken(height))], scheduler.CreateExternalFenceHandle(gate));
        try
        {
            streaming.Update();
            streaming.Applied.Complete();
            Assert.Empty(scene.Content.Tiles.ToArray());
            Assert.Equal(1, scene.Content.ContentVersion);
            Assert.Equal(2, scene.Content.ResidenceVersion);
        }
        finally { gate.Signal.SetResult(); }
        sample.Complete();
        Assert.Equal(3.25f, height[0]);

        JobResourceAccess Read<T>(ReadOnlyMemory<T> memory)
        {
            MemoryMarshal.TryGetArray(memory, out ArraySegment<T> segment);
            return JobResourceAccess.Read(scheduler.GetResourceToken(segment.Array!));
        }
    }

    [Fact]
    public async Task OneRequestWalksEachAvailableSectionWhileFollowingTilesLoadOutsideTheMainGroup()
    {
        string directory = Path.Combine(Path.GetTempPath(), "SomeEngine-NavigationStreaming-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var signals = new ReadSignals();
        try
        {
            using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
            var profile = new NavigationProfile([new(-0.1f, -0.1f), new(0.1f, -0.1f), new(0.1f, 0.1f), new(-0.1f, 0.1f)], 1f);
            var policy = new NavigationPolicy();
            NavigationMesh mesh = NavigationRegionBuilder.Schedule(Corridor(), profile, policy, scheduler: scheduler).Complete().Mesh;
            var selection = new NavigationMeshSelection(new(Guid.NewGuid()), "main");
            string path = Path.Combine(directory, "corridor.navigation.asset");
            using (var asset = new NavigationAsset())
            using (var block = new NavigationMeshBlock())
            {
                block.SetMesh(selection, mesh);
                asset.SetBlock(block);
                await asset.SaveAsync(path);
            }

            NavigationMeshTileSource source;
            using (var asset = await NavigationAsset.OpenAsync(path))
                source = (await asset.GetBlockAsync<NavigationMeshBlock>()).CreateTileSource(selection);
            NavigationMesh first = source.ScheduleRead(new(0, 0), scheduler).Complete();
            var scene = new NavigationScene(new(new NavigationSceneRegion[] { new(10, Matrix4x4.Identity, source.Graph) },
                default, new NavigationSceneTile[] { new(10, new(0, 0), first) }));
            using var streaming = new NavigationStreaming(scene, scheduler);
            using (var controlled = new ControlledSource(source, signals)) streaming.AddSource(10, controlled);
            source.Dispose();
            var interest = new NavigationInterest(new(1, 0, 1), 0.1f) { LookAheadDistance = 5f };
            streaming.Interests.Add(interest);
            using World world = WorldJobs.CreateWorld(scheduler: scheduler);
            var navigation = new NavigationSystems<NavigationScene, SceneNavigationSearch, SceneSearchState, Motion, MotionReader>(world);
            var actor = world.CreateEntity(new Motion(new(1, 0, 1), default, 0));
            navigation.AddAgent(actor, scene, profile, policy, new NavigationGoal(new Vector3(11, 0, 1), 0.05f), 2f);
            using var background = new SystemGroup<ImmediateSystemContext>(new ImmediateSystemDriver(world));
            using var main = new SystemGroup<ImmediateSystemContext>(new ImmediateSystemDriver(world));
            background.Add(new NavigationStreamingSystem(streaming));
            main.Add(new MainNavigation(navigation, streaming));
            ulong version = 0;
            try
            {
                for (int section = 1; section <= 3; section++)
                {
                    for (int frame = 0; frame < 100; frame++)
                    {
                        interest.Position = world.Read<Motion>(actor).Position;
                        interest.Path = world.Read<NavigationPathResult>(actor).Path;
                        background.Update();
                        main.Update();
                        main.Complete();
                        Motion motion = world.Read<Motion>(actor);
                        NavigationSteering steering = world.Read<NavigationSteering>(actor);
                        world.Replace(actor, new Motion(motion.Position + steering.Velocity * 0.1f, steering.Velocity, steering.Heading));
                        navigation.ScheduleProgress(default).Complete();
                        NavigationProgress progress = world.Read<NavigationProgress>(actor);
                        if (version == 0) version = progress.RequestVersion;
                        Assert.Equal(version, progress.RequestVersion);
                        if (progress.Status is NavigationStatus.WaitingForTiles or NavigationStatus.Arrived) break;
                    }
                    if (section == 3) break;
                    Assert.Equal(4f * section, world.Read<Motion>(actor).Position.X, 3);
                    NavigationPathResult result = world.Read<NavigationPathResult>(actor);
                    Assert.Equal(NavigationSearchStatus.WaitingForTiles, result.Status);
                    Assert.Contains(new NavigationSceneTileId(10, new(section, 0)), result.Path!.RequiredTiles.ToArray());
                    interest.Path = result.Path;
                    background.Update();
                    await signals.Started[section - 1].Task;
                    signals.Release[section - 1].SetResult();
                    streaming.Reads.Single(read => read.Tile == new NavigationSceneTileId(10, new(section, 0))).Completion.Complete();
                }
            }
            finally { foreach (TaskCompletionSource signal in signals.Release) signal.TrySetResult(); }
            background.Complete();
            Assert.Equal(NavigationStatus.Arrived, world.Read<NavigationProgress>(actor).Status);
            Assert.Equal(NavigationSearchStatus.Found, world.Read<NavigationPathResult>(actor).Status);
        }
        finally
        {
            foreach (TaskCompletionSource signal in signals.Release) signal.TrySetResult();
            Directory.Delete(directory, recursive: true);
        }
    }

    // These three rectangles are an example of a route whose detail arrives in successive blocks.
    private static NavigationMesh Corridor() => new(
        [new(0, 0, 0), new(0, 0, 2), new(4, 0, 2), new(4, 0, 0),
         new(4, 0, 0), new(4, 0, 2), new(8, 0, 2), new(8, 0, 0),
         new(8, 0, 0), new(8, 0, 2), new(12, 0, 2), new(12, 0, 0)],
        [0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11],
        [new(0, 4, 0, 1, Tile: new(0, 0)), new(4, 4, 1, 2, Tile: new(1, 0)), new(8, 4, 3, 1, Tile: new(2, 0))],
        [new(1, new(4, 0, 2), new(4, 0, 0)), new(0, new(4, 0, 0), new(4, 0, 2)),
         new(2, new(8, 0, 2), new(8, 0, 0)), new(1, new(8, 0, 0), new(8, 0, 2))]);

    private sealed class ReadSignals
    {
        public TaskCompletionSource[] Started { get; } = [new(TaskCreationOptions.RunContinuationsAsynchronously), new(TaskCreationOptions.RunContinuationsAsynchronously)];
        public TaskCompletionSource[] Release { get; } = [new(TaskCreationOptions.RunContinuationsAsynchronously), new(TaskCreationOptions.RunContinuationsAsynchronously)];
    }

    private sealed class SampleGate : IJobExternalFence
    {
        public TaskCompletionSource Signal { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool IsSignaled => Signal.Task.IsCompleted;
        public void OnSignaled(Action<object?> continuation, object? state)
        {
            if (IsSignaled) continuation(state);
            else Signal.Task.GetAwaiter().OnCompleted(() => continuation(state));
        }
    }
    private readonly struct SampleHeight(NavigationMesh mesh, float[] result) : IJob
    {
        public void Execute()
        {
            if (NavigationMesh.TryGetHeight(mesh, new(2, 3, 1), out float height)) result[0] = height;
        }
    }

    private sealed class ControlledSource(NavigationTileSource<NavigationMesh> source, ReadSignals signals) : NavigationTileSource<NavigationMesh>
    {
        private readonly NavigationTileSource<NavigationMesh> _source = source.Retain();
        public override ReadOnlyMemory<NavigationRegionTile> Directory => _source.Directory;
        public override NavigationTileSource<NavigationMesh> Retain() => new ControlledSource(_source, signals);
        public override async ValueTask<NavigationTileBytes> ReadAsync(NavigationTileCoordinate tile, CancellationToken cancellationToken = default)
        {
            if (tile.X > 0)
            {
                signals.Started[tile.X - 1].TrySetResult();
                await signals.Release[tile.X - 1].Task.WaitAsync(cancellationToken);
            }
            return await _source.ReadAsync(tile, cancellationToken);
        }
        public override NavigationMesh Decode(NavigationTileCoordinate tile, NavigationTileBytes bytes) => _source.Decode(tile, bytes);
        public override void Dispose() => _source.Dispose();
    }

    private readonly record struct Motion(Vector3 Position, Vector3 Velocity, float Heading) : IComponent;
    private readonly struct MotionReader : INavigationMotionReader<Motion>
    {
        public static NavigationMotion Read(in Motion value) => new(value.Position, value.Velocity, value.Heading, true);
    }
    private sealed class MainNavigation(NavigationSystems<NavigationScene, SceneNavigationSearch, SceneSearchState, Motion, MotionReader> navigation,
        NavigationStreaming streaming) : ISystem<ImmediateSystemContext>
    {
        public void OnUpdate(ref ImmediateSystemContext context) => navigation.Schedule(0.1f, 4, streaming.Applied);
    }
}
