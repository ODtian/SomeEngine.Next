using System.Security.Cryptography;
using System.Runtime.InteropServices;
using SomeEngine.Assets.Pipeline;
using SomeEngine.Assets.Schema;
using SomeEngine.Serialization.Containers;
using SomeEngine.Serialization.IO;
using SomeEngine.Serialization.Streaming;

namespace SomeEngine.Assets.Tests.Assets;

public sealed class TextureMipTileStreamingTests
{
    [Fact]
    public async Task TextureCreationCompletesAfterBootstrapMipIsResident()
    {
        using var directory = new TemporaryDirectory();
        var project = new AssetProject(directory.Path, []);
        Guid guid = project.CreateAsset(
            "assets/bootstrap.texture.asset",(
            new TextureData
            {
                Name = "bootstrap",
                Width = 4,
                Height = 4,
                Dimension = SomeEngine.Graphics.TextureDimension.Texture2D,
                Depth = 1,
                MipLevelCount = 2,
                ArrayLayerCount = 1,
                Format = SomeEngine.Graphics.Format.R8UNorm,
                SampledFormat = SomeEngine.Graphics.Format.R8UNorm,
                SampledDimension = SomeEngine.Graphics.TextureViewDimension.Texture2D,
                MipTiles =
                [
                    Tile(0, 0, 0, 4, 4, new byte[16], rowPitch: 4, slicePitch: 16),
                    Tile(1, 0, 0, 2, 2, [1, 2, 3, 4], rowPitch: 2, slicePitch: 4),
                ],
            }).ToAsset());

        await using var loader = new AssetLoader(project.CreateStorage());
        await using Asset<Texture> file = await loader.OpenAsync(new AssetId<Texture>(guid));
        await using Texture texture = await Texture.CreateAsync(file);

        Assert.True(texture.HasBootstrap);
        Assert.Equal(1u, texture.BootstrapMipLevel);
        Assert.False(texture.TryAcquireResidentMipTile(0, 0, 0, 0, 0, 0, out _));
        Assert.True(texture.TryAcquireResidentMipTile(1, 0, 0, 0, 0, 0, out ResidentChunkLease lease));
        using (lease)
            Assert.Equal([1, 2, 3, 4], lease.Memory.ToArray());
    }

    [Fact]
    public async Task TextureCreationRejectsIncompleteBootstrapCoverage()
    {
        using var directory = new TemporaryDirectory();
        var project = new AssetProject(directory.Path, []);
        Guid guid = project.CreateAsset(
            "assets/incomplete-bootstrap.texture.asset",(
            new TextureData
            {
                Name = "incomplete-bootstrap",
                Width = 2,
                Height = 2,
                Dimension = SomeEngine.Graphics.TextureDimension.Texture2D,
                Depth = 1,
                MipLevelCount = 2,
                ArrayLayerCount = 2,
                Format = SomeEngine.Graphics.Format.R8UNorm,
                SampledFormat = SomeEngine.Graphics.Format.R8UNorm,
                SampledDimension = SomeEngine.Graphics.TextureViewDimension.Texture2DArray,
                MipTiles =
                [
                    Tile(0, 0, 0, 2, 2, new byte[4], arrayLayer: 0),
                    Tile(1, 0, 0, 1, 1, [7], arrayLayer: 0),
                ],
            }).ToAsset());

        await using var loader = new AssetLoader(project.CreateStorage());
        await using Asset<Texture> file = await loader.OpenAsync(new AssetId<Texture>(guid));
        InvalidDataException error = await Assert.ThrowsAsync<InvalidDataException>(
            () => Texture.CreateAsync(file).AsTask());

        Assert.Contains("missing layer 1", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReloadSwitchesBootstrapAndShapeAtExplicitUpdateBoundaryWithoutChangingIdentity()
    {
        using var directory = new TemporaryDirectory();
        Guid guid = Guid.NewGuid();
        string beforePath = directory.File("before.texture.asset");
        string afterPath = directory.File("after.texture.asset");
        TextureData before = SingleMipTexture("before", 2, [1, 2]);
        before.AssetGuid = guid.ToString();
        TextureData after = SingleMipTexture("after", 4, [3, 4, 5, 6]);
        after.AssetGuid = guid.ToString();
        AssetWriter.Write((before).ToAsset(), beforePath);
        AssetWriter.Write((after).ToAsset(), afterPath);
        var storage = new MutableTextureStorage();
        storage.Publish(guid, beforePath);
        await using var loader = new AssetLoader(storage);
        await using Asset<Texture> file = await loader.OpenAsync(new AssetId<Texture>(guid));
        await using Texture current = await Texture.CreateAsync(file);

        storage.Publish(guid, afterPath);
        await using Asset<Texture> replacement = await loader.OpenAsync(new AssetId<Texture>(guid));

        Assert.Equal("before", current.Name);
        Assert.Equal(2u, current.Width);
        Assert.Equal(1ul, current.Revision);

        await current.UpdateAsync(replacement);

        Assert.Equal("after", current.Name);
        Assert.Equal(4u, current.Width);
        Assert.Equal(2ul, current.Revision);
        Assert.Equal("before", file.Name);
        Assert.True(current.HasBootstrap);
        Assert.True(current.TryAcquireResidentMipTile(0, 0, 0, 0, 0, 0, out ResidentChunkLease lease));
        using (lease)
            Assert.Equal([3, 4, 5, 6], lease.Memory.ToArray());
    }

    [Fact]
    public async Task StreamedOpen_ExposesCanonicalMetadataAndAcquiresOnlyRequestedTile()
    {
        using var directory = new TemporaryDirectory();
        string path = directory.File("streamed.texture.asset");
        byte[] mipZeroLeft = [1, 2, 3, 4];
        byte[] mipZeroRight = [5, 6, 7, 8];
        byte[] mipOne = [9, 10];
        AssetWriter.Write((new TextureData
        {
            AssetGuid = "00112233445566778899aabbccddeeff",
            Name = "streamed",
            Width = 8,
            Height = 8,
            Dimension = SomeEngine.Graphics.TextureDimension.Texture2D,
            Depth = 1,
            MipLevelCount = 2,
            ArrayLayerCount = 3,
            Format = SomeEngine.Graphics.Format.BC7UNorm,
            SampledFormat = SomeEngine.Graphics.Format.BC7UNorm,
            SampledDimension = SomeEngine.Graphics.TextureViewDimension.Texture2DArray,
            MipTiles =
            [
                Tile(
                    mipLevel: 1,
                    tileX: 0,
                    tileY: 0,
                    width: 4,
                    height: 4,
                    payload: mipOne,
                    arrayLayer: 2,
                    face: 0,
                    depthSlice: 0,
                    rowPitch: 1,
                    slicePitch: 2),
                Tile(mipLevel: 0, tileX: 1, tileY: 0, width: 4, height: 8, payload: mipZeroRight),
                Tile(mipLevel: 0, tileX: 0, tileY: 0, width: 4, height: 8, payload: mipZeroLeft),
            ],
        }).ToAsset(), path);

        await using BinaryDocument<TextureData> document =
            await AssetTestFiles.OpenAsync<Texture, TextureData>(path);

        TextureMipTile[] metadata = Assert.IsAssignableFrom<IEnumerable<TextureMipTile>>(
            document.Root.MipTiles).ToArray();
        Assert.Equal(
            [
                (0u, 0u, 0u, 0u, 0u, 0u),
                (0u, 0u, 0u, 0u, 1u, 0u),
                (1u, 2u, 0u, 0u, 0u, 0u),
            ],
            metadata.Select(static tile => (
                tile.MipLevel,
                tile.ArrayLayer,
                tile.Face,
                tile.DepthSlice,
                tile.TileX,
                tile.TileY)).ToArray());
        Assert.All(metadata, static tile => Assert.Null(tile.Payload));
        Assert.All(metadata, tile => Assert.Equal(
            TextureData.MipTileChunkKey(
                tile.MipLevel,
                tile.ArrayLayer,
                tile.Face,
                tile.DepthSlice,
                tile.TileX,
                tile.TileY),
            tile.ChunkKey));
        Assert.Equal([4ul, 4ul, 2ul], metadata.Select(static tile => tile.DecodedLength).ToArray());

        TextureMipTile selected = TextureData.RequireMipTile(
            document.Root,
            mipLevel: 1,
            arrayLayer: 2,
            face: 0,
            depthSlice: 0,
            tileX: 0,
            tileY: 0);
        using ChunkLease lease = await document.AcquireChunkAsync(selected.PayloadChunk);
        Assert.Equal(mipOne, lease.Memory.ToArray());
    }

    [Fact]
    public async Task SingleMipTileUsesItsCanonicalSemanticChunkKey()
    {
        using var directory = new TemporaryDirectory();
        string path = directory.File("payload-alias.texture.asset");
        byte[] expected = [3, 1, 4, 1, 5, 9];
        AssetWriter.Write((new TextureData
        {
            AssetGuid = "10112233445566778899aabbccddeeff",
            Width = 2,
            Height = 1,
            Dimension = SomeEngine.Graphics.TextureDimension.Texture2D,
            Depth = 1,
            MipLevelCount = 1,
            ArrayLayerCount = 1,
            Format = SomeEngine.Graphics.Format.R8UNorm,
            SampledFormat = SomeEngine.Graphics.Format.R8UNorm,
            SampledDimension = SomeEngine.Graphics.TextureViewDimension.Texture2D,
            MipTiles =
            [
                new TextureMipTile
                {
                    Width = 2,
                    Height = 1,
                    Payload = expected,
                },
            ],
        }).ToAsset(), path);

        await using BinaryDocument<TextureData> document =
            await AssetTestFiles.OpenAsync<Texture, TextureData>(path);
        TextureMipTile tile = Assert.Single(document.Root.MipTiles!);
        Assert.Equal((0u, 0u, 0u), (tile.MipLevel, tile.TileX, tile.TileY));
        Assert.Equal(TextureData.MipTileChunkKey(0, 0, 0), tile.ChunkKey);
        Assert.Equal((ulong)expected.Length, tile.DecodedLength);
        using ChunkLease payload = await document.AcquireChunkAsync(tile.PayloadChunk);
        Assert.Equal(expected, payload.Memory.ToArray());
    }

    [Fact]
    public void Save_IsByteDeterministicAcrossInputTileOrderAndCallerMetadata()
    {
        using var directory = new TemporaryDirectory();
        string first = directory.File("first.texture.asset");
        string second = directory.File("second.texture.asset");
        TextureMipTile mipZero = Tile(0, 0, 0, 4, 4, [1, 2, 3, 4]);
        TextureMipTile mipOne = Tile(1, 0, 0, 2, 2, [5, 6]);
        mipZero.ChunkKey = ulong.MaxValue;
        mipZero.DecodedLength = ulong.MaxValue;

        AssetWriter.Write((Asset([mipOne, mipZero])).ToAsset(), first);
        mipZero.ChunkKey = ulong.MaxValue;
        mipZero.DecodedLength = ulong.MaxValue;
        AssetWriter.Write((Asset([mipZero, mipOne])).ToAsset(), second);

        byte[] firstHash;
        byte[] secondHash;
        using (FileStream stream = File.OpenRead(first))
            firstHash = SHA256.HashData(stream);
        using (FileStream stream = File.OpenRead(second))
            secondHash = SHA256.HashData(stream);
        Assert.Equal(firstHash, secondHash);
        Assert.Equal(
            TextureData.MipTileChunkKey(3, 2, 4, 6, 7, 11),
            TextureData.MipTileChunkKey(3, 2, 4, 6, 7, 11));
        Assert.NotEqual(
            TextureData.MipTileChunkKey(3, 2, 4, 6, 7, 11),
            TextureData.MipTileChunkKey(3, 2, 4, 7, 7, 11));
    }

    [Fact]
    public async Task SaveWritesDeterministicTileMetadataWithoutChangingTheInput()
    {
        using var directory = new TemporaryDirectory();
        byte[] payload = [2, 7, 1, 8];
        TextureMipTile tile = Tile(0, 0, 0, 1, 1, payload);
        TextureData asset = Asset([tile]);

        string path = directory.File("captured.texture.asset");
        using Asset<Texture> file = asset.ToAsset();
        AssetWriter.Write(file, path);

        TextureMipTile current = Assert.Single(asset.MipTiles!);
        Assert.Same(tile, current);
        Assert.Equal(payload, current.Payload!.Value.ToArray());
        Assert.Equal(0ul, current.ChunkKey);
        Assert.Equal(0ul, current.DecodedLength);
        await using Asset<Texture> opened = await AssetLoader.OpenFileAsync<Texture>(path);
        await using BinaryDocument<TextureData> document = opened.OpenDocumentGenerated<TextureData>()!;
        TextureMipTile written = Assert.Single(document.Root.MipTiles!);
        Assert.Equal(TextureData.MipTileChunkKey(0, 0, 0), written.ChunkKey);
        Assert.Equal((ulong)payload.Length, written.DecodedLength);
        Assert.Null(written.Payload);
        using ChunkLease chunk = await document.AcquireChunkAsync(written.PayloadChunk);
        Assert.Equal(payload, chunk.Memory.ToArray());
    }

    [Fact]
    public void Save_RejectsDuplicateOutOfBoundsAndMissingTilePayloads()
    {
        using var directory = new TemporaryDirectory();
        Assert.Throws<InvalidDataException>(() => AssetWriter.Write((
            Asset([Tile(0, 0, 0, 4, 4, [1]), Tile(0, 0, 0, 4, 4, [2])])).ToAsset(),
            directory.File("duplicate.texture.asset")));
        Assert.Throws<InvalidDataException>(() => AssetWriter.Write((
            Asset([Tile(0, 0, 0, 9, 4, [1])])).ToAsset(),
            directory.File("bounds.texture.asset")));
        Assert.Throws<InvalidDataException>(() => AssetWriter.Write((
            Asset([new TextureMipTile { Width = 4, Height = 4 }])).ToAsset(),
            directory.File("missing.texture.asset")));
        Assert.Throws<InvalidDataException>(() => AssetWriter.Write((
            Asset([Tile(0, 0, 0, 4, 4, [1, 2], rowPitch: 2, slicePitch: 1)])).ToAsset(),
            directory.File("pitch.texture.asset")));
    }

    private static TextureData Asset(IList<TextureMipTile> tiles)
        => new()
        {
            AssetGuid = "20112233445566778899aabbccddeeff",
            Name = "deterministic",
            Width = 4,
            Height = 4,
            Dimension = SomeEngine.Graphics.TextureDimension.Texture2D,
            Depth = 1,
            MipLevelCount = 2,
            ArrayLayerCount = 1,
            Format = SomeEngine.Graphics.Format.R8UNorm,
            SampledFormat = SomeEngine.Graphics.Format.R8UNorm,
            SampledDimension = SomeEngine.Graphics.TextureViewDimension.Texture2D,
            MipTiles = tiles,
        };

    private static TextureData SingleMipTexture(string name, uint width, byte[] payload)
        => new()
        {
            Name = name,
            Width = width,
            Height = 1,
            Dimension = SomeEngine.Graphics.TextureDimension.Texture2D,
            Depth = 1,
            MipLevelCount = 1,
            ArrayLayerCount = 1,
            Format = SomeEngine.Graphics.Format.R8UNorm,
            SampledFormat = SomeEngine.Graphics.Format.R8UNorm,
            SampledDimension = SomeEngine.Graphics.TextureViewDimension.Texture2D,
            MipTiles =
            [
                Tile(0, 0, 0, width, 1, payload, rowPitch: width, slicePitch: width),
            ],
        };

    private static TextureMipTile Tile(
        uint mipLevel,
        uint tileX,
        uint tileY,
        uint width,
        uint height,
        byte[] payload,
        uint arrayLayer = 0,
        uint face = 0,
        uint depthSlice = 0,
        ulong rowPitch = 0,
        ulong slicePitch = 0)
        => new()
        {
            MipLevel = mipLevel,
            TileX = tileX,
            TileY = tileY,
            ArrayLayer = arrayLayer,
            Face = face,
            DepthSlice = depthSlice,
            Width = width,
            Height = height,
            RowPitch = rowPitch,
            SlicePitch = slicePitch,
            Payload = payload,
        };

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
                    Asset<Texture>.FileType,
                    TextureData.SchemaFingerprint,
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
