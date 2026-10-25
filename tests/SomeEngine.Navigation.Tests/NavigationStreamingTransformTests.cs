using System.Numerics;
using SomeEngine.Job;

namespace SomeEngine.Navigation.Tests;

public sealed class NavigationStreamingTransformTests
{
    [Fact]
    public void ReplacingARegionInstallsTheNewTileContentAtItsNewTransform()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
        NavigationRegionTile[] local = [
            new(new(0, 0), new(new(0, 3, 0), new(4, 4, 4)), 1, 0, 0),
            new(new(1, 0), new(new(4, 3, 0), new(8, 4, 4)), 1, 0, 0),
        ];
        Vector3 firstOffset = new(10, 2, 5);
        var firstGraph = new NavigationRegionGraph(local.Select(tile => tile with
            { Bounds = new(tile.Bounds.Minimum + firstOffset, tile.Bounds.Maximum + firstOffset) }).ToArray(),
            default, default, default, 1);
        var scene = new NavigationScene(new(new NavigationSceneRegion[]
            { new(1, Matrix4x4.CreateTranslation(firstOffset), firstGraph) }, default, default));
        using var firstSource = new VersionedSource(local);
        using var streaming = new NavigationStreaming(scene, scheduler);
        streaming.AddSource(1, firstSource);
        var interest = new NavigationInterest(new(14, 5.25f, 7), 20);
        streaming.Interests.Add(interest);
        Acquire();
        Assert.Equal(2, scene.Content.Tiles.Length);
        Assert.True(NavigationScene.TryGetHeight(scene, new(12, 5.25f, 7), out float firstHeight));
        Assert.Equal(5.25f, firstHeight);

        Vector3 secondOffset = new(-10, 6, 2);
        NavigationRegionTile[] replacement = local.Select(tile => tile with { ContentVersion = 2 }).ToArray();
        var replacementGraph = new NavigationRegionGraph(replacement.Select(tile => tile with
            { Bounds = new(tile.Bounds.Minimum + secondOffset, tile.Bounds.Maximum + secondOffset) }).ToArray(),
            default, default, default, 2);
        using var nextSource = new VersionedSource(replacement);
        streaming.SetSource(1, nextSource);
        scene.ScheduleApply(new(new NavigationSceneRegion[]
            { new(1, Matrix4x4.CreateTranslation(secondOffset), replacementGraph) }, default, default), scheduler: scheduler).Complete();
        interest.Position = new(-6, 9.25f, 4);
        Acquire();

        Assert.Equal(2, scene.Content.Tiles.Length);
        Assert.All(scene.Content.Tiles.ToArray(), tile => Assert.Equal(2ul, tile.Mesh.ContentVersion));
        Assert.True(NavigationScene.TryGetHeight(scene, new(-8, 9.25f, 4), out float leftHeight));
        Assert.True(NavigationScene.TryGetHeight(scene, new(-4, 9.25f, 4), out float rightHeight));
        Assert.Equal(9.25f, leftHeight);
        Assert.Equal(9.25f, rightHeight);

        void Acquire()
        {
            streaming.Update();
            foreach (NavigationStreamingRead read in streaming.Reads) read.Completion.Complete();
            streaming.Update();
            streaming.Applied.Complete();
        }
    }

    private sealed class VersionedSource(ReadOnlyMemory<NavigationRegionTile> directory) : NavigationTileSource<NavigationMesh>
    {
        public override ReadOnlyMemory<NavigationRegionTile> Directory => directory;
        public override NavigationTileSource<NavigationMesh> Retain() => new VersionedSource(directory);
        public override ValueTask<NavigationTileBytes> ReadAsync(NavigationTileCoordinate tile,
            CancellationToken cancellationToken = default) => ValueTask.FromResult(new NavigationTileBytes([]));
        public override NavigationMesh Decode(NavigationTileCoordinate tile, NavigationTileBytes bytes)
        {
            float x = tile.X * 4;
            ulong version = directory.ToArray().Single(value => value.Tile == tile).ContentVersion;
            return new([new(x, 3.25f, 0), new(x, 3.25f, 4), new(x + 4, 3.25f, 4), new(x + 4, 3.25f, 0)],
                [0, 1, 2, 3], [new(0, 4, 0, 0)], [], contentVersion: version);
        }
        public override void Dispose() { }
    }
}
