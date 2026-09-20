using System.Security.Cryptography;
using SomeEngine.Assets.Schema;
using SomeEngine.Resources;
using SomeEngine.Serialization.Containers;
using SomeEngine.Serialization.IO;
using SomeEngine.Graphics;
using Texture = SomeEngine.Resources.Texture;
using TextureViewDimension = SomeEngine.Graphics.TextureViewDimension;

namespace SomeEngine.Assets.Tests.Assets;

public sealed class TextureMipTileStreamingTests
{
    [Fact]
    public async Task TextureCreationCompletesAfterBootstrapMipIsResident()
    {
        using var directory = new TemporaryDirectory();
        var project = new AssetProject(directory.Path, []);
        using var original = Asset([
            Tile(0, 0, 0, 4, 4, new byte[16], rowPitch: 4, slicePitch: 16),
            Tile(1, 0, 0, 2, 2, [1, 2, 3, 4], rowPitch: 2, slicePitch: 4)]);
        Guid guid = project.RegisterAsset(await original.SaveAsync(directory.File("bootstrap.texture.asset")));
        await using var loader = new AssetLoader(project.CreateStorage());
        using var file = await TextureAsset.OpenAsync(loader, new(guid));
        using Texture texture = await file.GetBlock<TextureBlock>().CreateAsync(file.Name);
        Assert.True(texture.HasBootstrap);
        Assert.Equal(1u, texture.BootstrapMipLevel);
        Assert.False(texture.TryRetainTile(TextureTiles.ChunkKey(0, 0, 0), out _));
        Assert.True(texture.TryRetainTile(TextureTiles.ChunkKey(1, 0, 0), out var read));
        using (read) Assert.Equal(new byte[] { 1, 2, 3, 4 }, read.ToArray());
    }

    [Fact]
    public async Task TextureCreationRejectsIncompleteBootstrapCoverage()
    {
        using var directory = new TemporaryDirectory();
        var project = new AssetProject(directory.Path, []);
        using var original = Asset([Tile(0, 0, 0, 2, 2, new byte[4]), Tile(1, 0, 0, 1, 1, [7])]);
        original.GetBlock<TextureBlock>().Width = original.GetBlock<TextureBlock>().Height = 2;
        original.GetBlock<TextureBlock>().ArrayLayerCount = 2;
        original.GetBlock<TextureBlock>().SampledDimension = TextureViewDimension.Texture2DArray;
        Guid guid = project.RegisterAsset(await original.SaveAsync(directory.File("incomplete-bootstrap.texture.asset")));
        await using var loader = new AssetLoader(project.CreateStorage());
        using var file = await TextureAsset.OpenAsync(loader, new(guid));
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => file.GetBlock<TextureBlock>().CreateAsync(file.Name).AsTask());
        Assert.Contains("missing layer 1", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReloadSwitchesBootstrapAndShapeAtExplicitUpdateBoundaryWithoutChangingIdentity()
    {
        using var directory = new TemporaryDirectory();
        Guid guid = Guid.NewGuid();
        string beforePath = directory.File("before.texture.asset");
        string afterPath = directory.File("after.texture.asset");
        using var before = SingleMipTexture("before", 2, [1, 2], guid);
        using var after = SingleMipTexture("after", 4, [3, 4, 5, 6], guid);
        await before.SaveAsync(beforePath);
        await after.SaveAsync(afterPath);
        var storage = new MutableTextureStorage();
        storage.Publish(guid, beforePath);
        await using var loader = new AssetLoader(storage);
        using var file = await TextureAsset.OpenAsync(loader, new(guid));
        using Texture current = await file.GetBlock<TextureBlock>().CreateAsync(file.Name);
        storage.Publish(guid, afterPath);
        using var replacement = await TextureAsset.OpenAsync(loader, new(guid));
        Assert.Equal("before", current.Name);
        Assert.Equal(2u, current.Width);
        Assert.Equal(1ul, current.Revision);
        await replacement.GetBlock<TextureBlock>().UpdateAsync(current, replacement.Name);
        Assert.Equal("after", current.Name);
        Assert.Equal(4u, current.Width);
        Assert.Equal(2ul, current.Revision);
        Assert.Equal("before", file.Name);
        Assert.True(current.HasBootstrap);
        Assert.True(current.TryRetainTile(TextureTiles.ChunkKey(0, 0, 0), out var read));
        using (read) Assert.Equal(new byte[] { 3, 4, 5, 6 }, read.ToArray());
    }

    [Fact]
    public async Task StreamedOpen_ExposesCanonicalMetadataAndAcquiresOnlyRequestedTile()
    {
        using var directory = new TemporaryDirectory();
        string path = directory.File("streamed.texture.asset");
        byte[] mipZeroLeft = [1, 2, 3, 4];
        byte[] mipZeroRight = [5, 6, 7, 8];
        byte[] mipOne = [9, 10];
        using var original = Asset([
            Tile(1, 0, 0, 4, 4, mipOne, arrayLayer: 2, rowPitch: 1, slicePitch: 2),
            Tile(0, 1, 0, 4, 8, mipZeroRight), Tile(0, 0, 0, 4, 8, mipZeroLeft)]);
        original.GetBlock<TextureBlock>().Width = original.GetBlock<TextureBlock>().Height = 8;
        original.GetBlock<TextureBlock>().ArrayLayerCount = 3;
        original.GetBlock<TextureBlock>().Format = original.GetBlock<TextureBlock>().SampledFormat = Format.BC7UNorm;
        original.GetBlock<TextureBlock>().SampledDimension = TextureViewDimension.Texture2DArray;
        await original.SaveAsync(path);
        using var file = await TextureAsset.OpenAsync(path);
        TextureMipTile[] metadata = file.GetBlock<TextureBlock>().MipTiles.ToArray();
        Assert.Equal(new[] { (0u, 0u, 0u, 0u, 0u, 0u), (0u, 0u, 0u, 0u, 1u, 0u), (1u, 2u, 0u, 0u, 0u, 0u) },
            metadata.Select(static tile => (tile.MipLevel, tile.ArrayLayer, tile.Face, tile.DepthSlice, tile.TileX, tile.TileY)).ToArray());
        Assert.Empty(file.GetBlock<TextureBlock>().Chunks.Keys);
        Assert.Equal(metadata.Length, metadata.Select(static tile => tile.ChunkKey).Distinct().Count());
        Assert.All(metadata, static tile => Assert.NotEqual(0ul, tile.ChunkKey));
        Assert.Equal(new ulong[] { 4, 4, 2 }, metadata.Select(static tile => tile.DecodedLength).ToArray());
        TextureMipTile selected = Assert.Single(metadata, static tile => tile.MipLevel == 1 && tile.ArrayLayer == 2);
        long loadsBefore = file.StreamingMetrics!.Snapshot().LoadsCompleted;
        using var bytes = await file.GetBlock<TextureBlock>().AcquireBufferAsync(selected.ChunkKey);
        using var read = bytes.Retain();
        Assert.Equal(mipOne, read.ToArray());
        Assert.Equal(loadsBefore + 1, file.StreamingMetrics!.Snapshot().LoadsCompleted);
    }

    [Fact]
    public async Task SingleMipTileUsesItsCanonicalSemanticChunkKey()
    {
        using var directory = new TemporaryDirectory();
        string path = directory.File("single.texture.asset");
        byte[] expected = [3, 1, 4, 1, 5, 9];
        using var original = SingleMipTexture("single", (uint)expected.Length, expected);
        await original.SaveAsync(path);
        using var file = await TextureAsset.OpenAsync(path);
        TextureMipTile tile = Assert.Single(file.GetBlock<TextureBlock>().MipTiles);
        Assert.Equal((0u, 0u, 0u), (tile.MipLevel, tile.TileX, tile.TileY));
        Assert.Equal(TextureTiles.ChunkKey(0, 0, 0), tile.ChunkKey);
        Assert.Equal((ulong)expected.Length, tile.DecodedLength);
        await using var document = await BinaryDocument.OpenAsync(FileRangeSource.Open(path), TextureAsset.WireType, ownsSource: true);
        using var payload = await document.AcquireChunkAsync(new BinaryChunkRef(file.BlockDescriptions.Single(block => block.BlockTypeId == TextureBlock.BlockTypeId).GetStorageKey(tile.ChunkKey), (long)tile.DecodedLength));
        Assert.Equal(expected, payload.Memory.ToArray());
    }

    [Fact]
    public async Task Save_IsByteDeterministicAcrossInputTileOrderAndCallerMetadata()
    {
        using var directory = new TemporaryDirectory();
        string firstPath = directory.File("first.texture.asset");
        string secondPath = directory.File("second.texture.asset");
        TexturePixels mipZero = Tile(0, 0, 0, 4, 4, [1, 2, 3, 4]);
        TexturePixels mipOne = Tile(1, 0, 0, 2, 2, [5, 6]);
        mipZero = mipZero with { Tile = mipZero.Tile with { ChunkKey = ulong.MaxValue, DecodedLength = ulong.MaxValue } };
        using var first = Asset([mipOne, mipZero]);
        using var second = Asset([mipZero, mipOne]);
        await first.SaveAsync(firstPath);
        await second.SaveAsync(secondPath);
        Assert.Equal(SHA256.HashData(File.ReadAllBytes(firstPath)), SHA256.HashData(File.ReadAllBytes(secondPath)));
    }

    [Fact]
    public async Task SaveWritesDeterministicTileMetadataWithoutChangingTheInput()
    {
        using var directory = new TemporaryDirectory();
        byte[] payload = [2, 7, 1, 8];
        TexturePixels pixel = Tile(0, 0, 0, 1, 1, payload);
        pixel = pixel with { Tile = pixel.Tile with { ChunkKey = 0, DecodedLength = 0 } };
        using var asset = Asset([pixel]);
        string path = directory.File("captured.texture.asset");
        await asset.SaveAsync(path);
        TextureMipTile current = Assert.Single(asset.GetBlock<TextureBlock>().MipTiles);
        Assert.Same(pixel.Tile, current);
        using var inputRead = asset.GetBlock<TextureBlock>().Chunks.Retain(0);
        Assert.Equal(payload, inputRead.ToArray());
        Assert.Equal(0ul, current.ChunkKey);
        Assert.Equal(0ul, current.DecodedLength);
        using var opened = await TextureAsset.OpenAsync(path);
        TextureMipTile written = Assert.Single(opened.GetBlock<TextureBlock>().MipTiles);
        Assert.NotEqual(0ul, written.ChunkKey);
        Assert.Equal((ulong)payload.Length, written.DecodedLength);
        Assert.Empty(opened.GetBlock<TextureBlock>().Chunks.Keys);
        using var bytes = await opened.GetBlock<TextureBlock>().AcquireBufferAsync(written.ChunkKey);
        using var read = bytes.Retain();
        Assert.Equal(payload, read.ToArray());
    }

    [Fact]
    public async Task Save_RejectsDuplicateOutOfBoundsAndMissingTilePayloads()
    {
        using var directory = new TemporaryDirectory();
        using var duplicate = Asset([Tile(0, 0, 0, 4, 4, [1]), Tile(0, 0, 0, 4, 4, [2])]);
        using var bounds = Asset([Tile(0, 0, 0, 9, 4, [1])]);
        using var missing = Asset([new TexturePixels(new TextureMipTile { Width = 4, Height = 4 }, default)]);
        using var pitch = Asset([Tile(0, 0, 0, 4, 4, [1, 2], rowPitch: 2, slicePitch: 1)]);
        await Assert.ThrowsAsync<InvalidDataException>(() => duplicate.SaveAsync(directory.File("duplicate.texture.asset")).AsTask());
        await Assert.ThrowsAsync<InvalidDataException>(() => bounds.SaveAsync(directory.File("bounds.texture.asset")).AsTask());
        await Assert.ThrowsAsync<InvalidDataException>(() => missing.SaveAsync(directory.File("missing.texture.asset")).AsTask());
        await Assert.ThrowsAsync<InvalidDataException>(() => pitch.SaveAsync(directory.File("pitch.texture.asset")).AsTask());
    }

    private static TextureAsset Asset(IReadOnlyList<TexturePixels> pixels)
    {
        using var content = new TextureBlock
        {
            Width = 4, Height = 4, Dimension = TextureDimension.Texture2D,
            Depth = 1, MipLevelCount = 2, ArrayLayerCount = 1, Format = Format.R8UNorm,
            SampledFormat = Format.R8UNorm, SampledDimension = TextureViewDimension.Texture2D,
        };
        var asset = new TextureAsset(PageArena.Shared, Guid.Parse("20112233445566778899aabbccddeeff")) { Name = "deterministic" };
        asset.SetBlock(content);
        try { SetPixels(asset, pixels); return asset; }
        catch { asset.Dispose(); throw; }
    }

    private static TextureAsset SingleMipTexture(string name, uint width, byte[] payload, Guid id = default)
    {
        using var content = new TextureBlock
        {
            Width = width, Height = 1, Dimension = TextureDimension.Texture2D,
            Depth = 1, MipLevelCount = 1, ArrayLayerCount = 1, Format = Format.R8UNorm,
            SampledFormat = Format.R8UNorm, SampledDimension = TextureViewDimension.Texture2D,
        };
        var asset = new TextureAsset(PageArena.Shared, id) { Name = name };
        asset.SetBlock(content);
        try { SetPixels(asset, [Tile(0, 0, 0, width, 1, payload, rowPitch: width, slicePitch: width)]); return asset; }
        catch { asset.Dispose(); throw; }
    }

    private static void SetPixels(TextureAsset asset, IReadOnlyList<TexturePixels> pixels)
    {
        asset.GetBlock<TextureBlock>().MipTiles = pixels.Select(static item => item.Tile).ToArray();
        foreach (TexturePixels item in pixels)
            if (!item.Bytes.IsEmpty) asset.GetBlock<TextureBlock>().Chunks.Set(item.Tile.ChunkKey, item.Bytes.Span);
    }

    private static TexturePixels Tile(uint mipLevel, uint tileX, uint tileY, uint width, uint height,
        byte[] payload, uint arrayLayer = 0, uint face = 0, uint depthSlice = 0, ulong rowPitch = 0, ulong slicePitch = 0)
        => new(new TextureMipTile
        {
            MipLevel = mipLevel, TileX = tileX, TileY = tileY, ArrayLayer = arrayLayer, Face = face,
            DepthSlice = depthSlice, Width = width, Height = height, RowPitch = rowPitch, SlicePitch = slicePitch,
            ChunkKey = TextureTiles.ChunkKey(mipLevel, arrayLayer, face, depthSlice, tileX, tileY),
            DecodedLength = (ulong)payload.Length,
        }, payload);
    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"SomeEngine-TextureMipTile-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }
        public string File(string name) => System.IO.Path.Combine(Path, name);

        public void Dispose()
        {
            if (Directory.Exists(Path))
                Directory.Delete(Path, recursive: true);
        }
    }

    private sealed class MutableTextureStorage : IAssetStorage
    {
        private readonly object _gate = new();
        private AssetEntry _entry;
        private string? _path;

        internal void Publish(Guid guid, string path)
        {
            lock (_gate)
            {
                _entry = new AssetEntry(
                    guid,
                    TextureAsset.FileType,
                    TextureAsset.SchemaHash,
                    Guid.NewGuid());
                _path = path;
            }
        }

        public bool TryFind(Guid assetGuid, out AssetEntry entry)
        {
            lock (_gate)
            {
                if (_path is not null && assetGuid == _entry.AssetGuid)
                {
                    entry = _entry;
                    return true;
                }
            }
            entry = default;
            return false;
        }

        public ValueTask<IRangeSource> OpenAsync(
            AssetEntry entry,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                if (_path is null || entry != _entry)
                    throw new InvalidDataException("The Texture publication is no longer current.");
                return ValueTask.FromResult<IRangeSource>(FileRangeSource.Open(_path));
            }
        }
    }
}
