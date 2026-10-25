using SomeEngine.Assets.Schema;
using SomeEngine.Graphics;
using SomeEngine.Serialization.Packs;
using TextureMipTile = SomeEngine.Resources.TextureMipTile;
using TextureTiles = SomeEngine.Resources.TextureTiles;

namespace SomeEngine.Assets.Tests.Assets;

public sealed class ContentPackTests
{
    [Fact]
    public async Task UpdatingSceneAssetsReusesTheSharedPack()
    {
        using var files = new ContentFiles();
        var project = new AssetProject(files.Root, []);
        Guid shared = await files.SaveTextureAsync(project, "shared", 1);
        Guid scene = await files.SaveTextureAsync(project, "scene", 2);
        Assert.True(await project.BuildPackAsync("shared.assetpack", [shared]));
        Assert.True(await project.BuildPackAsync("scene.assetpack", [scene]));
        Assert.False(await project.BuildPackAsync("scene.assetpack", [scene]));

        await files.SaveTextureAsync(project, "scene", 9, scene);
        Assert.False(await project.BuildPackAsync("shared.assetpack", [shared]));
        Assert.True(await project.BuildPackAsync("scene.assetpack", [scene]));
        AssetContent.WritePackPaths(files.Root, "scene.assetpack", "shared.assetpack");
        await using AssetContent content = await AssetContent.OpenAsync(files.Root);
        using TextureAsset texture = await TextureAsset.OpenAsync(content.Loader, new(scene));
        TextureBlock pixels = await texture.GetBlockAsync<TextureBlock>();
        Assert.Equal(new byte[] { 9 }, (await pixels.ReadBytesAsync(pixels.MipTiles[0].ChunkKey)).ToArray());
    }

    [Fact]
    public async Task ContentLoadsTheSceneOverrideAndItsSharedTexture()
    {
        using var files = new ContentFiles();
        var project = new AssetProject(files.Root, []);
        Guid surface = await files.SaveTextureAsync(project, "surface", 1);
        Guid shared = await files.SaveTextureAsync(project, "white", 7);
        Guid otherShared = await files.SaveTextureAsync(project, "other", 8);
        await project.BuildPackAsync("shared.assetpack", [surface, shared, otherShared]);
        await files.SaveTextureAsync(project, "surface", 9, surface);
        await project.BuildPackAsync("scene.assetpack", [surface]);
        AssetContent.WritePackPaths(files.Root, "scene.assetpack", "shared.assetpack");

        await using AssetContent content = await AssetContent.OpenAsync(files.Root);
        using TextureAsset sceneTexture = await TextureAsset.OpenAsync(content.Loader, new(surface));
        Task<byte[]> sharedRead = ReadTextureAsync(shared);
        Task<byte[]> otherRead = ReadTextureAsync(otherShared);
        byte[][] sharedPixels = await Task.WhenAll(sharedRead, otherRead);
        TextureBlock scenePixels = await sceneTexture.GetBlockAsync<TextureBlock>();
        Assert.Equal(new byte[] { 9 }, (await scenePixels.ReadBytesAsync(scenePixels.MipTiles[0].ChunkKey)).ToArray());
        Assert.Equal(new byte[] { 7 }, sharedPixels[0]);
        Assert.Equal(new byte[] { 8 }, sharedPixels[1]);
        Assert.Equal(3, content.Manifest.Assets.Count);
        Assert.Equal(project.Manifest.Assets[shared].Version, content.Manifest.Assets[shared].Version);

        async Task<byte[]> ReadTextureAsync(Guid id)
        {
            using TextureAsset texture = await TextureAsset.OpenAsync(content.Loader, new(id));
            TextureBlock pixels = await texture.GetBlockAsync<TextureBlock>();
            return (await pixels.ReadBytesAsync(pixels.MipTiles[0].ChunkKey)).ToArray();
        }
    }

    [Fact]
    public async Task CatalogPackedOutputsCanLoadAndRepackWithoutLooseFiles()
    {
        using var files = new ContentFiles();
        var project = new AssetProject(files.Root, []);
        Guid id = await files.SaveTextureAsync(project, "packed", 3, packPath: "imported.assetpack");
        Assert.False(File.Exists(Path.Combine(files.Root, "packed.texture.asset")));
        await using (AssetContent content = await AssetContent.OpenAsync(files.Root))
        {
            using TextureAsset texture = await TextureAsset.OpenAsync(content.Loader, new(id));
            TextureBlock pixels = await texture.GetBlockAsync<TextureBlock>();
            Assert.Equal(new byte[] { 3 }, (await pixels.ReadBytesAsync(pixels.MipTiles[0].ChunkKey)).ToArray());
        }

        Assert.True(await project.BuildPackAsync("published.assetpack", [id]));
        Assert.False(await project.BuildPackAsync("published.assetpack", [id]));
        AssetContent.WritePackPaths(files.Root, "published.assetpack");
        await using AssetContent published = await AssetContent.OpenAsync(files.Root);
        using TextureAsset result = await TextureAsset.OpenAsync(published.Loader, new(id));
        TextureBlock resultPixels = await result.GetBlockAsync<TextureBlock>();
        Assert.Equal(new byte[] { 3 }, (await resultPixels.ReadBytesAsync(resultPixels.MipTiles[0].ChunkKey)).ToArray());
        Assert.Equal(project.Manifest.Assets[id].Version, published.Manifest.Assets[id].Version);
        Assert.Empty(Directory.EnumerateFiles(files.Root, "*.asset", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task ContentDetectsARebuiltPackWithoutChangingItsPathList()
    {
        using var files = new ContentFiles();
        var project = new AssetProject(files.Root, []);
        Guid texture = await files.SaveTextureAsync(project, "surface", 1);
        await project.BuildPackAsync("scene.assetpack", [texture]);
        AssetContent.WritePackPaths(files.Root, "scene.assetpack");
        string packPath = Path.Combine(files.Root, "scene.assetpack");
        DateTime packWriteTime = File.GetLastWriteTimeUtc(packPath);
        string pathsFile = Path.Combine(files.Root, AssetContent.PackPathsFile);
        string paths = await File.ReadAllTextAsync(pathsFile);
        await using AssetContent content = await AssetContent.OpenAsync(files.Root);
        Assert.False(content.HasChanges);
        Assert.False(await project.BuildPackAsync("scene.assetpack", [texture]));
        Assert.False(content.HasChanges);

        await files.SaveTextureAsync(project, "surface", 9, texture);
        Assert.True(await project.BuildPackAsync("scene.assetpack", [texture]));
        // Avoid depending on the filesystem's timestamp resolution or on elapsed wall time.
        File.SetLastWriteTimeUtc(packPath, packWriteTime.AddMinutes(1));
        Assert.Equal(paths, await File.ReadAllTextAsync(pathsFile));
        Assert.True(content.HasChanges);
        Assert.Equal((byte)1, await ReadTextureAsync(content, texture));
        await using AssetContent updated = await AssetContent.OpenAsync(files.Root);
        Assert.False(updated.HasChanges);
        Assert.Equal((byte)9, await ReadTextureAsync(updated, texture));
    }

    [Fact]
    public async Task ContentDetectsSwitchingBetweenLooseAssetsAndPacks()
    {
        using var files = new ContentFiles();
        var project = new AssetProject(files.Root, []);
        Guid texture = await files.SaveTextureAsync(project, "surface", 1);
        await project.BuildPackAsync("scene.assetpack", [texture]);
        await files.SaveTextureAsync(project, "surface", 9, texture);
        await using AssetContent loose = await AssetContent.OpenAsync(files.Root);
        Assert.False(loose.HasChanges);
        Assert.Equal((byte)9, await ReadTextureAsync(loose, texture));

        AssetContent.WritePackPaths(files.Root, "scene.assetpack");
        Assert.True(loose.HasChanges);
        await using AssetContent packed = await AssetContent.OpenAsync(files.Root);
        Assert.False(packed.HasChanges);
        Assert.Equal((byte)1, await ReadTextureAsync(packed, texture));

        File.Delete(Path.Combine(files.Root, AssetContent.PackPathsFile));
        Assert.True(packed.HasChanges);
        await using AssetContent reopened = await AssetContent.OpenAsync(files.Root);
        Assert.False(reopened.HasChanges);
        Assert.Equal((byte)9, await ReadTextureAsync(reopened, texture));
    }

    [Fact]
    public async Task LooseContentDetectsAChangedCatalog()
    {
        using var files = new ContentFiles();
        var project = new AssetProject(files.Root, []);
        await files.SaveTextureAsync(project, "first", 1);
        string catalog = Path.Combine(files.Root, "Library", "AssetManifest", AssetManifest.CatalogFileName);
        DateTime writeTime = File.GetLastWriteTimeUtc(catalog);
        await using AssetContent content = await AssetContent.OpenAsync(files.Root);
        Assert.False(content.HasChanges);

        Guid added = await files.SaveTextureAsync(project, "second", 2);
        File.SetLastWriteTimeUtc(catalog, writeTime.AddMinutes(1));
        Assert.True(content.HasChanges);
        await using AssetContent updated = await AssetContent.OpenAsync(files.Root);
        Assert.False(updated.HasChanges);
        Assert.Equal((byte)2, await ReadTextureAsync(updated, added));
    }

    private static async Task<byte> ReadTextureAsync(AssetContent content, Guid id)
    {
        using TextureAsset texture = await TextureAsset.OpenAsync(content.Loader, new(id));
        TextureBlock pixels = await texture.GetBlockAsync<TextureBlock>();
        return (await pixels.ReadBytesAsync(pixels.MipTiles[0].ChunkKey)).ToArray()[0];
    }

    private sealed class ContentFiles : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), $"SomeEngine-ContentPacks-{Guid.NewGuid():N}");

        internal async Task<Guid> SaveTextureAsync(AssetProject project, string name, byte value, Guid id = default, string? packPath = null)
        {
            using var texture = new TextureAsset(id) { Name = name };
            var pixels = texture.GetBlock<TextureBlock>();
            pixels.Width = pixels.Height = pixels.Depth = pixels.MipLevelCount = pixels.ArrayLayerCount = 1;
            pixels.Format = pixels.SampledFormat = Format.R8UNorm;
            pixels.Dimension = TextureDimension.Texture2D;
            pixels.SampledDimension = TextureViewDimension.Texture2D;
            pixels.MipTiles = [new TextureMipTile { Width = 1, Height = 1, ChunkKey = TextureTiles.ChunkKey(0, 0, 0), DecodedLength = 1 }];
            pixels.SetBytes(pixels.MipTiles[0].ChunkKey, new byte[] { value });
            if (packPath is null)
                return project.RegisterAsset(await texture.SaveAsync(Path.Combine(Root, name + ".texture.asset")));
            using var bytes = new MemoryStream();
            AssetEntry entry = await texture.WriteAsync(bytes);
            var builder = new AssetPackBuilder();
            builder.AddAsset(entry.AssetGuid, entry.AssetType, bytes.ToArray(), entry.SchemaFingerprint);
            await builder.PublishAsync(Path.Combine(Root, packPath));
            project.Manifest.AddAsset(entry.AssetGuid, entry.Name, name + ".texture.asset", entry.AssetType,
                entry.SchemaFingerprint, version: entry.Version, packPath: packPath);
            project.Manifest.Save(Path.Combine(Root, "Library", "AssetManifest"));
            return entry.AssetGuid;
        }

        public void Dispose()
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
    }
}
