using System.Numerics;
using System.Runtime.CompilerServices;
using SomeEngine.Job;

namespace SomeEngine.Navigation.Tests;

public sealed class NavigationStreamingRecoveryTests
{
    [Fact]
    public void MeshStreamingAcquiresTheRequestedTileAfterAReadFailure()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 0 });
        var bounds = new NavigationBounds(new(0, 3, 0), new(4, 4, 4));
        var mesh = new NavigationMesh([new(0, 3.25f, 0), new(0, 3.25f, 4), new(4, 3.25f, 4), new(4, 3.25f, 0)],
            [0, 1, 2, 3], [new(0, 4, 0, 0)], []);
        var graph = new NavigationRegionGraph(new NavigationRegionTile[] { new(default, bounds, 1, 0, 0) }, default, default, default, 1);
        var scene = new NavigationScene(new(new NavigationSceneRegion[] { new(1, Matrix4x4.Identity, graph) }, default, default));
        using var source = new FailingReadSource<NavigationMesh>(graph.Tiles, _ => mesh);
        using var streaming = new NavigationStreaming(scene, scheduler);
        streaming.AddSource(1, source);
        streaming.Interests.Add(new(new(1, 3.25f, 1), 0.1f));

        streaming.Update();
        scheduler.Schedule(new StreamingCheckpoint(), new JobScheduleOptions(JobPriority.Low)).Complete();
        Assert.Throws<IOException>(streaming.Update);
        streaming.Update();
        Assert.Single(streaming.Reads).Completion.Complete();
        streaming.Update();
        streaming.Applied.Complete();

        Assert.True(NavigationScene.TryGetHeight(scene, new(1, 3.25f, 1), out float height));
        Assert.Equal(3.25f, height);
    }

    private readonly struct StreamingCheckpoint : IJob { public void Execute() { } }

    [Fact]
    public void GridStreamingAcquiresTheRequestedSupportAfterADecodeFailure()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 0 });
        var bounds = new NavigationBounds(new(0, 3, 0), new(4, 4, 4));
        var grid = new NavigationGrid(bounds, 4, new NavigationGridCell[] { new(new(2, 3.25f, 2), 0, 0) }, []);
        var graph = new NavigationGridRegionGraph(bounds, 4, default,
            new NavigationRegionTile[] { new(default, bounds, 1, 0, 0) }, default, default, default, 1);
        var profile = new NavigationProfile([new Vector2(-0.1f), new(0.1f, -0.1f), new(0.1f), new(-0.1f, 0.1f)], 1);
        NavigationFlow flow = NavigationGridFlowBuilder.Schedule(graph, default, profile, new NavigationPolicy(),
            new NavigationFlowGoal[] { new(new(2, 3.25f, 2)) }, scheduler: scheduler).Complete().Flow;
        using var source = new FailingGridDecodeSource(graph.Tiles, grid);
        using var streaming = new NavigationGridStreaming(flow, source, scheduler);
        streaming.Interests.Add(new(new(2, 3.25f, 2), 0.1f));

        streaming.Update();
        scheduler.Schedule(new StreamingCheckpoint(), new JobScheduleOptions(JobPriority.Low)).Complete();
        Assert.Throws<IOException>(streaming.Update);
        streaming.Update();
        Assert.Single(streaming.Reads).Completion.Complete();
        streaming.Update();
        new NavigationFlowSystem<ReverseDijkstraFlow, ReverseDijkstraFlowState>(flow, scheduler)
            .Schedule(dependency: streaming.Applied).Complete();

        Assert.True(NavigationGrid.TryGetHeight(flow.Costs.Grid, new(2, 3.25f, 2), out float height));
        Assert.Equal(3.25f, height);
        Assert.Equal(NavigationFlowAction.Arrived, flow.Field!.Sample(new(new(2, 3.25f, 2), 0)).Action);
    }

    [Fact]
    public void HeadingStreamingAcquiresTheRequestedPoseDataAfterAReadFailure()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 0 });
        NavigationProfile profile = AsaNavigationTests.Profile();
        var policy = new NavigationPolicy();
        HeadingNavigationMesh mesh = HeadingNavigationMeshBuilder.Schedule(AsaNavigationTests.Scene(), profile, policy,
            new() { ChannelCount = 16 }, scheduler: scheduler).Complete().Mesh;
        mesh = HeadingNavigationRegionBuilder.Schedule(mesh, profile, policy, scheduler: scheduler).Complete().Mesh;
        var scene = new HeadingNavigationScene(new(mesh.RegionGraph!));
        using var source = new FailingReadSource<HeadingNavigationMesh>(mesh.RegionGraph!.Tiles,
            tile => mesh.Tiles.ToArray().Single(value => value.Tile == tile).Mesh);
        using var streaming = new HeadingNavigationStreaming(scene, source, scheduler);
        streaming.Interests.Add(new(new(3, 0, 2), 0.1f));

        streaming.Update();
        scheduler.Schedule(new StreamingCheckpoint(), new JobScheduleOptions(JobPriority.Low)).Complete();
        Assert.Throws<IOException>(streaming.Update);
        streaming.Update();
        Assert.Single(streaming.Reads).Completion.Complete();
        streaming.Update();
        streaming.Applied.Complete();

        HeadingNavigationMeshTile tile = Assert.IsType<HeadingNavigationMeshTile>(scene.Content.FindTile(new(3, 0, 2), 0));
        Assert.True(tile.Mesh.IsPoseFeasible(new(3, 0, 2), 0));
        Assert.True(HeadingNavigationScene.TryGetHeight(scene, new(3, 0, 2), out float height));
        Assert.Equal(0, height);
    }

    // This source models one failed external read followed by the same complete tile data.
    private sealed class FailingReadSource<TTile> : NavigationTileSource<TTile> where TTile : class
    {
        private readonly ReadOnlyMemory<NavigationRegionTile> _directory;
        private readonly Func<NavigationTileCoordinate, TTile> _decode;
        private readonly ReadFailure _failure;
        public FailingReadSource(ReadOnlyMemory<NavigationRegionTile> directory, Func<NavigationTileCoordinate, TTile> decode)
            : this(directory, decode, new ReadFailure()) { }
        private FailingReadSource(ReadOnlyMemory<NavigationRegionTile> directory, Func<NavigationTileCoordinate, TTile> decode, ReadFailure failure) =>
            (_directory, _decode, _failure) = (directory, decode, failure);
        public override ReadOnlyMemory<NavigationRegionTile> Directory => _directory;
        public override NavigationTileSource<TTile> Retain() => new FailingReadSource<TTile>(_directory, _decode, _failure);
        public override ValueTask<NavigationTileBytes> ReadAsync(NavigationTileCoordinate tile, CancellationToken cancellationToken = default) =>
            Interlocked.Exchange(ref _failure.Remaining, 0) == 1
                ? ValueTask.FromException<NavigationTileBytes>(new IOException()) : ValueTask.FromResult(new NavigationTileBytes([]));
        public override TTile Decode(NavigationTileCoordinate tile, NavigationTileBytes bytes) => _decode(tile);
        public override void Dispose() { }
        private sealed class ReadFailure { public int Remaining = 1; }
    }

    private sealed class FailingGridDecodeSource(ReadOnlyMemory<NavigationRegionTile> directory, NavigationGrid grid,
        StrongBox<int>? remaining = null) : NavigationTileSource<NavigationGrid>
    {
        private readonly StrongBox<int> _remaining = remaining ?? new(1);
        public override ReadOnlyMemory<NavigationRegionTile> Directory => directory;
        public override NavigationTileSource<NavigationGrid> Retain() => new FailingGridDecodeSource(directory, grid, _remaining);
        public override ValueTask<NavigationTileBytes> ReadAsync(NavigationTileCoordinate tile, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new NavigationTileBytes([]));
        public override NavigationGrid Decode(NavigationTileCoordinate tile, NavigationTileBytes bytes) =>
            Interlocked.Exchange(ref _remaining.Value, 0) == 1 ? throw new IOException() : grid;
        public override void Dispose() { }
    }
}
