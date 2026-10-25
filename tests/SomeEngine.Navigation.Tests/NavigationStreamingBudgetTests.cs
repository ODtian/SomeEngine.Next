using System.Numerics;
using SomeEngine.Job;

namespace SomeEngine.Navigation.Tests;

public sealed class NavigationStreamingBudgetTests
{
    [Fact]
    public void NearestRequestedTileIsReacquiredAfterTheBudgetMovesAcrossTheDirectory()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
        // This strip is acceptance input spanning several directory ranges.
        NavigationRegionTile[] tiles = Enumerable.Range(0, 260).Select(index =>
            new NavigationRegionTile(new(index, 0), new(new(index * 4, 3, 0), new(index * 4 + 4, 4, 4)), 1, 0, 0)).ToArray();
        var graph = new NavigationRegionGraph(tiles, default, default, default, 1);
        var scene = new NavigationScene(new(new NavigationSceneRegion[] { new(1, Matrix4x4.Identity, graph) }, default, default));
        using var source = new StripSource(tiles);
        using var streaming = new NavigationStreaming(scene, scheduler) { ResidentTileBudget = 1 };
        streaming.AddSource(1, source);
        var interest = new NavigationInterest(new(18, 3.25f, 2), 2000);
        streaming.Interests.Add(interest);

        Acquire();
        Assert.Equal(new NavigationTileCoordinate(4, 0), Assert.Single(scene.Content.Tiles.ToArray()).Tile);
        interest.Position = new(802, 3.25f, 2);
        Acquire();
        Assert.Equal(new NavigationTileCoordinate(200, 0), Assert.Single(scene.Content.Tiles.ToArray()).Tile);
        interest.Position = new(18, 3.25f, 2);
        Acquire();
        Assert.Equal(new NavigationTileCoordinate(4, 0), Assert.Single(scene.Content.Tiles.ToArray()).Tile);
        Assert.True(NavigationScene.TryGetHeight(scene, interest.Position, out float height));
        Assert.Equal(3.25f, height);

        void Acquire()
        {
            streaming.Update();
            foreach (NavigationStreamingRead read in streaming.Reads) read.Completion.Complete();
            streaming.Update();
            streaming.Applied.Complete();
        }
    }

    private sealed class StripSource(ReadOnlyMemory<NavigationRegionTile> directory) : NavigationTileSource<NavigationMesh>
    {
        public override ReadOnlyMemory<NavigationRegionTile> Directory => directory;
        public override NavigationTileSource<NavigationMesh> Retain() => new StripSource(directory);
        public override ValueTask<NavigationTileBytes> ReadAsync(NavigationTileCoordinate tile,
            CancellationToken cancellationToken = default) => ValueTask.FromResult(new NavigationTileBytes([]));
        public override NavigationMesh Decode(NavigationTileCoordinate tile, NavigationTileBytes bytes)
        {
            float x = tile.X * 4;
            return new([new(x, 3.25f, 0), new(x, 3.25f, 4), new(x + 4, 3.25f, 4), new(x + 4, 3.25f, 0)],
                [0, 1, 2, 3], [new(0, 4, 0, 0)], []);
        }
        public override void Dispose() { }
    }
}
