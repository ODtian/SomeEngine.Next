using SomeEngine.Assets.Schema;
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
        Assert.Equal(Asset<Texture>.FileType, entry.AssetType);
        Assert.Equal(TextureData.SchemaFingerprint, entry.SchemaFingerprint);
        Assert.Equal(pack.Generation, entry.Version);

        await using BinaryDocument<TextureData> document =
            await AssetTestFiles.OpenAsync<Texture, TextureData>(storage, entry);
        Assert.Equal(assetGuid.ToString(), document.Root.AssetGuid);
        TextureMipTile selected = Assert.Single(document.Root.MipTiles!);
        using ChunkLease tile = await document.AcquireChunkAsync(selected.PayloadChunk);
        Assert.Equal([1, 3, 3, 7], tile.Memory.ToArray());

        await Assert.ThrowsAsync<InvalidDataException>(async () =>
        {
            await AssetTestFiles.OpenAsync<Mesh, MeshData>(storage, entry);
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

        await using (BinaryDocument<TextureData> document =
            await AssetTestFiles.OpenAsync<Texture, TextureData>(storage, entry))
        {
            TextureMipTile selected = Assert.Single(document.Root.MipTiles!);
            using ChunkLease tile = await document.AcquireChunkAsync(selected.PayloadChunk);
            Assert.Equal([9], tile.Memory.ToArray());
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
        InvalidDataException error = await Assert.ThrowsAsync<InvalidDataException>(
            async () => await AssetTestFiles.OpenAsync<Texture, TextureData>(storage, entry));
        Assert.Contains("declares GUID", error.Message, StringComparison.OrdinalIgnoreCase);
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
            AssetWriter.Write((new TextureData
            {
                AssetGuid = (rootGuid ?? assetGuid).ToString(),
                Name = "stored-texture",
                Width = 1,
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
                        Width = 1,
                        Height = 1,
                        Payload = payload,
                    },
                ],
            }).ToAsset(), assetPath);
            byte[] documentBytes = await File.ReadAllBytesAsync(assetPath);
            string packPath = Path.Combine(directory, "stored.sepack");
            await new AssetPackBuilder()
                .AddAsset(assetGuid, Asset<Texture>.FileType, documentBytes, TextureData.SchemaFingerprint)
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
