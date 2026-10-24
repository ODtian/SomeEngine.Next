using System.Numerics;
using SomeEngine.Navigation;
using SomeEngine.Serialization.Containers;
using SomeEngine.Serialization.IO;

namespace SomeEngine.Assets.Tests;

public sealed class NavigationCreationRecoveryTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "SomeEngine-NavigationCreation-" + Guid.NewGuid().ToString("N"));
    public NavigationCreationRecoveryTests() => Directory.CreateDirectory(_directory);
    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public async Task MeshCreationRetriesAFailedReadAndSharesTheAcquiredData()
    {
        var selection = new NavigationMeshSelection(new(Guid.NewGuid()), "main");
        using var definition = new NavigationMeshBlock();
        definition.SetMesh(selection, Square());
        RetryStorage storage = await SaveAsync(definition);
        await using var loader = new AssetLoader(storage);
        using var asset = await NavigationAsset.OpenAsync(loader, new(storage.Entry.AssetGuid));
        NavigationMeshBlock block = await asset.GetBlockAsync<NavigationMeshBlock>();
        storage.Source.FailNext = true;

        await Assert.ThrowsAsync<IOException>(() => block.CreateAsync(selection).AsTask());
        NavigationMesh mesh = await block.CreateAsync(selection);
        using NavigationMeshBlock branch = block.Duplicate();
        NavigationMesh shared = await branch.CreateAsync(selection);

        Assert.True(NavigationMesh.TryGetHeight(mesh, new(0.5f, 2, 0.5f), out float height));
        Assert.Equal(2, height);
        Assert.True(mesh.Positions.Equals(shared.Positions));
    }

    [Fact]
    public async Task HeadingMeshCreationRetriesAFailedReadAndSharesTheAcquiredData()
    {
        var selection = new NavigationMeshSelection(new(Guid.NewGuid()), "main");
        using var definition = new HeadingNavigationMeshBlock();
        definition.SetMesh(selection, new(Square(), 1, new HeadingNavigationRegion[] { new(0, 1, 0, 0, 0, 0) },
            new HeadingNavigationInterval[] { new(0, -MathF.PI, MathF.PI) },
            Array.Empty<HeadingNavigationTranslation>(), Array.Empty<HeadingNavigationRotation>(),
            new(Array.Empty<HeadingNavigationNode>(), Array.Empty<HeadingNavigationEdge>())));
        RetryStorage storage = await SaveAsync(definition);
        await using var loader = new AssetLoader(storage);
        using var asset = await NavigationAsset.OpenAsync(loader, new(storage.Entry.AssetGuid));
        HeadingNavigationMeshBlock block = await asset.GetBlockAsync<HeadingNavigationMeshBlock>();
        storage.Source.FailNext = true;

        await Assert.ThrowsAsync<IOException>(() => block.CreateAsync(selection).AsTask());
        HeadingNavigationMesh mesh = await block.CreateAsync(selection);
        using HeadingNavigationMeshBlock branch = block.Duplicate();
        HeadingNavigationMesh shared = await branch.CreateAsync(selection);

        Assert.True(mesh.IsPoseFeasible(new(0.5f, 2, 0.5f), 0));
        Assert.True(mesh.Geometry.Positions.Equals(shared.Geometry.Positions));
    }

    [Fact]
    public async Task GridCreationRetriesAFailedReadAndSharesTheAcquiredData()
    {
        var selection = new NavigationGridSelection(new(Guid.NewGuid()), "main");
        using var definition = new NavigationGridBlock();
        definition.SetGrid(selection, new(new(new(0, 2, 0), new(1, 3, 1)), 1,
            new NavigationGridCell[] { new(new(0.5f, 2, 0.5f), 0, 0) }, Array.Empty<NavigationGridEdge>()));
        RetryStorage storage = await SaveAsync(definition);
        await using var loader = new AssetLoader(storage);
        using var asset = await NavigationAsset.OpenAsync(loader, new(storage.Entry.AssetGuid));
        NavigationGridBlock block = await asset.GetBlockAsync<NavigationGridBlock>();
        storage.Source.FailNext = true;

        await Assert.ThrowsAsync<IOException>(() => block.CreateAsync(selection).AsTask());
        NavigationGrid grid = await block.CreateAsync(selection);
        using NavigationGridBlock branch = block.Duplicate();
        NavigationGrid shared = await branch.CreateAsync(selection);

        Assert.True(NavigationGrid.TryGetHeight(grid, new(0.5f, 2, 0.5f), out float height));
        Assert.Equal(2, height);
        Assert.True(grid.Cells.Equals(shared.Cells));
    }

    // The elevated square is a fixture example of persisted navigation data.
    private static NavigationMesh Square() => new(
        [new(0, 2, 0), new(1, 2, 0), new(1, 2, 1), new(0, 2, 1)],
        [0, 1, 2, 3], [new(0, 4, 0, 0)], []);

    private async Task<RetryStorage> SaveAsync<TBlock>(TBlock block) where TBlock : AssetBlock, IAssetBlock<TBlock>
    {
        using var asset = new NavigationAsset();
        asset.SetBlock(block);
        AssetEntry entry = await asset.SaveAsync(Path.Combine(_directory, typeof(TBlock).Name + ".navigation.asset"));
        using NavigationAsset saved = await NavigationAsset.OpenAsync(entry.Path);
        ulong key = saved.BlockDescriptions.Single().GetStorageKey(1);
        await using BinaryDocument document = await BinaryDocument.OpenAsync(FileRangeSource.Open(entry.Path),
            NavigationAsset.WireType, ownsSource: true);
        BinaryChunkEntry chunk = (await document.FindChunkAsync(key))!.Value;
        return new(entry, new(await File.ReadAllBytesAsync(entry.Path), chunk.Offset));
    }

    private sealed class RetryStorage(AssetEntry entry, RetrySource source) : IAssetStorage
    {
        internal AssetEntry Entry => entry;
        internal RetrySource Source => source;
        public bool TryFind(Guid id, out AssetEntry found) { found = entry; return id == entry.AssetGuid; }
        public ValueTask<IRangeSource> OpenAsync(AssetEntry selected, CancellationToken cancellationToken = default)
            => ValueTask.FromResult<IRangeSource>(source);
    }

    private sealed class RetrySource(byte[] bytes, long dataOffset) : IRangeSource
    {
        internal bool FailNext;
        public long Length => bytes.Length;
        public string Generation => "navigation-creation";
        public bool LeasesAreImmutable => true;
        public ValueTask ReadExactlyAsync(long offset, Memory<byte> destination, CancellationToken cancellationToken = default)
        {
            if (offset == dataOffset && FailNext) { FailNext = false; throw new IOException(); }
            bytes.AsMemory(checked((int)offset), destination.Length).CopyTo(destination);
            return ValueTask.CompletedTask;
        }
        public ValueTask<RangeLease> AcquireAsync(long offset, int length, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(RangeLease.Borrow(bytes.AsMemory(checked((int)offset), length)));
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
