using System.Security.Cryptography;
using System.Runtime.InteropServices;
using SomeEngine.Assets.Pipeline;
using SomeEngine.Assets.Schema;
using SomeEngine.Serialization.Containers;

namespace SomeEngine.Assets.Tests.Assets;

public sealed class TextureMipTileStreamingTests
{
    [Fact]
    public async Task StreamedOpen_ExposesCanonicalMetadataAndAcquiresOnlyRequestedTile()
    {
        using var directory = new TemporaryDirectory();
        string path = directory.File("streamed.texture.asset");
        byte[] mipZeroLeft = [1, 2, 3, 4];
        byte[] mipZeroRight = [5, 6, 7, 8];
        byte[] mipOne = [9, 10];
        AssetWriter.Write(new Texture
        {
            AssetGuid = "00112233445566778899aabbccddeeff",
            Name = "streamed",
            Width = 8,
            Height = 8,
            Format = "BC7_UNorm",
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
                    face: 3,
                    depthSlice: 4,
                    rowPitch: 1,
                    slicePitch: 2),
                Tile(mipLevel: 0, tileX: 1, tileY: 0, width: 4, height: 8, payload: mipZeroRight),
                Tile(mipLevel: 0, tileX: 0, tileY: 0, width: 4, height: 8, payload: mipZeroLeft),
            ],
        }, path);

        await using BinaryDocument<Texture> document =
            await AssetProject.OpenAsync<Texture>(path);

        TextureMipTile[] metadata = Assert.IsAssignableFrom<IEnumerable<TextureMipTile>>(
            document.Root.MipTiles).ToArray();
        Assert.Equal(
            [
                (0u, 0u, 0u, 0u, 0u, 0u),
                (0u, 0u, 0u, 0u, 1u, 0u),
                (1u, 2u, 3u, 4u, 0u, 0u),
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
            Texture.MipTileChunkKey(
                tile.MipLevel,
                tile.ArrayLayer,
                tile.Face,
                tile.DepthSlice,
                tile.TileX,
                tile.TileY),
            tile.ChunkKey));
        Assert.Equal([4ul, 4ul, 2ul], metadata.Select(static tile => tile.DecodedLength).ToArray());

        TextureMipTile selected = Texture.RequireMipTile(
            document.Root,
            mipLevel: 1,
            arrayLayer: 2,
            face: 3,
            depthSlice: 4,
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
        AssetWriter.Write(new Texture
        {
            AssetGuid = "10112233445566778899aabbccddeeff",
            Width = 2,
            Height = 1,
            MipTiles =
            [
                new TextureMipTile
                {
                    Width = 2,
                    Height = 1,
                    Payload = expected,
                },
            ],
        }, path);

        await using BinaryDocument<Texture> document =
            await AssetProject.OpenAsync<Texture>(path);
        TextureMipTile tile = Assert.Single(document.Root.MipTiles!);
        Assert.Equal((0u, 0u, 0u), (tile.MipLevel, tile.TileX, tile.TileY));
        Assert.Equal(Texture.MipTileChunkKey(0, 0, 0), tile.ChunkKey);
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

        AssetWriter.Write(Asset([mipOne, mipZero]), first);
        mipZero.ChunkKey = ulong.MaxValue;
        mipZero.DecodedLength = ulong.MaxValue;
        AssetWriter.Write(Asset([mipZero, mipOne]), second);

        byte[] firstHash;
        byte[] secondHash;
        using (FileStream stream = File.OpenRead(first))
            firstHash = SHA256.HashData(stream);
        using (FileStream stream = File.OpenRead(second))
            secondHash = SHA256.HashData(stream);
        Assert.Equal(firstHash, secondHash);
        Assert.Equal(
            Texture.MipTileChunkKey(3, 2, 4, 6, 7, 11),
            Texture.MipTileChunkKey(3, 2, 4, 6, 7, 11));
        Assert.NotEqual(
            Texture.MipTileChunkKey(3, 2, 4, 6, 7, 11),
            Texture.MipTileChunkKey(3, 2, 4, 7, 7, 11));
    }

    [Fact]
    public void SaveCanonicalizesExistingTilesWithoutCloningTheirPayloadBacking()
    {
        using var directory = new TemporaryDirectory();
        byte[] payload = [2, 7, 1, 8];
        TextureMipTile tile = Tile(0, 0, 0, 1, 1, payload);
        Texture asset = Asset([tile]);

        AssetWriter.Write(asset, directory.File("borrowed.texture.asset"));

        TextureMipTile canonical = Assert.Single(asset.MipTiles!);
        Assert.Same(tile, canonical);
        Assert.True(MemoryMarshal.TryGetArray(canonical.Payload!.Value, out ArraySegment<byte> segment));
        Assert.Same(payload, segment.Array);
        Assert.Equal(Texture.MipTileChunkKey(0, 0, 0), canonical.ChunkKey);
        Assert.Equal((ulong)payload.Length, canonical.DecodedLength);
    }

    [Fact]
    public void Save_RejectsDuplicateOutOfBoundsAndMissingTilePayloads()
    {
        using var directory = new TemporaryDirectory();
        Assert.Throws<InvalidDataException>(() => AssetWriter.Write(
            Asset([Tile(0, 0, 0, 4, 4, [1]), Tile(0, 0, 0, 4, 4, [2])]),
            directory.File("duplicate.texture.asset")));
        Assert.Throws<InvalidDataException>(() => AssetWriter.Write(
            Asset([Tile(0, 0, 0, 9, 4, [1])]),
            directory.File("bounds.texture.asset")));
        Assert.Throws<InvalidDataException>(() => AssetWriter.Write(
            Asset([new TextureMipTile { Width = 4, Height = 4 }]),
            directory.File("missing.texture.asset")));
        Assert.Throws<InvalidDataException>(() => AssetWriter.Write(
            Asset([Tile(0, 0, 0, 4, 4, [1, 2], rowPitch: 2, slicePitch: 1)]),
            directory.File("pitch.texture.asset")));
    }

    private static Texture Asset(IList<TextureMipTile> tiles)
        => new()
        {
            AssetGuid = "20112233445566778899aabbccddeeff",
            Name = "deterministic",
            Width = 4,
            Height = 4,
            Format = "R8_UNorm",
            MipTiles = tiles,
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
}
