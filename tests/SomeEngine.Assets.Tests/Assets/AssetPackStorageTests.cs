using SomeEngine.Assets.Schema;
using SomeEngine.Resources;
using SomeEngine.Serialization;
using SomeEngine.Serialization.Containers;
using SomeEngine.Serialization.Packs;

namespace SomeEngine.Assets.Tests.Assets;

public sealed class AssetPackStorageTests
{
    [Fact]
    public async Task PackStorageOpensTypedBinaryDocumentThroughUnifiedReader()
    {
        Guid assetGuid = Guid.Parse("6575cc07-f3bf-465c-b976-4a3301525511");
        AssetPack pack = await OpenPackAsync(assetGuid, [1, 3, 3, 7]);
        await using var storage = new AssetPackStorage([pack]);

        Assert.True(storage.TryFind(assetGuid, out AssetEntry entry));
        Assert.Equal(TextureAsset.FileType, entry.AssetType);
        Assert.Equal(TextureAsset.SchemaHash, entry.SchemaFingerprint);
        Assert.Equal(pack.Generation, entry.Version);

        await using var loader = new AssetLoader(storage);
        using TextureAsset file = await TextureAsset.OpenAsync(loader, new(assetGuid));
        Assert.Equal(assetGuid, file.Id);
        TextureBlock pixels = await file.GetBlockAsync<TextureBlock>();
        TextureMipTile selected = Assert.Single(pixels.MipTiles);
        var tile = await pixels.ReadBytesAsync(selected.ChunkKey);
        var read = tile;
        Assert.Equal([1, 3, 3, 7], read.ToArray());

        await Assert.ThrowsAsync<InvalidDataException>(async () =>
        {
            await MeshAsset.OpenAsync(loader, new(assetGuid));
        });
    }

    [Fact]
    public async Task HighestPriorityPackWinsAndStorageDisposalFailsClosed()
    {
        Guid assetGuid = Guid.Parse("1b53ef27-9692-4ddc-8b79-c7550e0d52a2");
        AssetPack basePack = await OpenPackAsync(assetGuid, [1]);
        AssetPack hotfixPack = await OpenPackAsync(assetGuid, [9]);
        var storage = new AssetPackStorage([hotfixPack, basePack]);
        Assert.True(storage.TryFind(assetGuid, out AssetEntry entry));

        await using (var loader = new AssetLoader(storage))
        {
            using TextureAsset file = await TextureAsset.OpenAsync(loader, new(assetGuid));
            TextureBlock pixels = await file.GetBlockAsync<TextureBlock>();
            TextureMipTile selected = Assert.Single(pixels.MipTiles);
            var tile = await pixels.ReadBytesAsync(selected.ChunkKey);
            var read = tile;
            Assert.Equal([9], read.ToArray());
        }

        await storage.DisposeAsync();
        Assert.Throws<ObjectDisposedException>(() => storage.TryFind(assetGuid, out _));
    }

    [Fact]
    public async Task PackStorageRejectsDocumentWhoseRootGuidDiffersFromItsEntry()
    {
        Guid entryGuid = Guid.Parse("37299864-10d9-4d11-99a1-279f79ee20f6");
        Guid rootGuid = Guid.Parse("40a31947-73c5-4477-9c02-aed0c37fdff9");
        AssetPack pack = await OpenPackAsync(entryGuid, [5, 8], rootGuid);
        await using var storage = new AssetPackStorage([pack]);

        Assert.True(storage.TryFind(entryGuid, out AssetEntry entry));
        await using var loader = new AssetLoader(storage);
        await Assert.ThrowsAsync<InvalidDataException>(
            async () => await TextureAsset.OpenAsync(loader, new(entryGuid)));
    }

    private static async ValueTask<AssetPack> OpenPackAsync(
        Guid assetGuid,
        byte[] payload,
        Guid? rootGuid = null)
    {
        string directory = Path.Combine(Path.GetTempPath(), $"SomeEngine-PackStorage-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            string assetPath = Path.Combine(directory, "stored.texture.asset");
            using var pixels = new TextureBlock {
                Width = (uint)payload.Length,
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
                        Width = (uint)payload.Length,
                        Height = 1,
                        ChunkKey = TextureTiles.ChunkKey(0, 0, 0), DecodedLength = (ulong)payload.Length,
                    },
                ],
            };
            pixels.SetBytes(pixels.MipTiles[0].ChunkKey, payload);
            using var created = new TextureAsset(rootGuid ?? assetGuid) { Name = "stored-texture" };
            created.SetBlock(pixels);
            await created.SaveAsync(assetPath);
            byte[] documentBytes = await File.ReadAllBytesAsync(assetPath);
            string packPath = Path.Combine(directory, "stored.sepack");
            await new AssetPackBuilder()
                .AddAsset(assetGuid, TextureAsset.FileType, documentBytes, TextureAsset.SchemaHash)
                .PublishAsync(packPath);
            Array.Clear(documentBytes);
            return await AssetPack.OpenAsync(packPath);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
