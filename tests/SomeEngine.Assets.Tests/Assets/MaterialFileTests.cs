using System.Numerics;
using SomeEngine.Assets.Schema;
using SomeEngine.Resources;
using SomeEngine.Graphics;
using SomeEngine.Serialization.Streaming;
using Texture = SomeEngine.Resources.Texture;
using TextureViewDimension = SomeEngine.Graphics.TextureViewDimension;

namespace SomeEngine.Assets.Tests.Assets;

public sealed class MaterialFileTests
{
    [Fact]
    public async Task MaterialTemplatesSupplySharedWhiteAndFlatNormalAssets()
    {
        using var directory = new AssetTestDirectory();
        var project = new AssetProject(directory.Path, []);
        AssetId<TextureAsset> whiteId = await SaveColorAsync(project, directory, "white", [255, 255, 255, 255]);
        AssetId<TextureAsset> normalId = await SaveColorAsync(project, directory, "normal", [128, 128, 255, 255]);
        using var template = new MaterialAsset { Name = "Standard template" };
        _ = new StandardMaterialParameters(template.GetBlock<MaterialBlock>())
        {
            AlbedoMap = whiteId, ARMMap = whiteId, NormalMap = normalId,
        };
        project.RegisterAsset(await template.SaveAsync(directory.File("template.material.asset")));
        using var asset = new StandardMaterialAsset();
        asset.GetBlock<MaterialBlock>().Parent = template.Reference;
        Guid id = project.RegisterAsset(await asset.SaveAsync(directory.File("standard.material.asset")));
        await using var loader = new AssetLoader(project.CreateStorage());
        await using MaterialAsset opened = await MaterialAsset.OpenAsync(loader, new(id));
        using StandardMaterial material = await opened.CreateAsync<StandardMaterial>(loader);
        Assert.Equal(0.5f, material.Roughness);
        Assert.Same(material.AlbedoMap, material.ARMMap);
        Assert.True(material.AlbedoMap!.TryGetTile(TextureTiles.ChunkKey(0, 0, 0), out var white));
        Assert.Equal(new byte[] { 255, 255, 255, 255 }, white.ToArray());
        Assert.True(material.NormalMap!.TryGetTile(TextureTiles.ChunkKey(0, 0, 0), out var normal));
        Assert.Equal(new byte[] { 128, 128, 255, 255 }, normal.ToArray());
        Assert.True(material.ARMMap!.TryGetTile(TextureTiles.ChunkKey(0, 0, 0), out var arm));
        Assert.Equal(new byte[] { 255, 255, 255, 255 }, arm.ToArray());

        using var unlitTemplate = new MaterialAsset { Name = "Unlit template" };
        _ = new UnlitMaterialParameters(unlitTemplate.GetBlock<MaterialBlock>()) { AlbedoMap = whiteId };
        project.RegisterAsset(await unlitTemplate.SaveAsync(directory.File("unlit.material.asset")));
        using UnlitMaterial unlit = await unlitTemplate.CreateAsync<UnlitMaterial>(loader);
        Assert.Same(material.AlbedoMap, unlit.AlbedoMap);
    }

    [Fact]
    public async Task ParentAndOverridesRoundTripWithSharedTextureReferences()
    {
        using var directory = new AssetTestDirectory();
        var project = new AssetProject(directory.Path, []);
        using var content = new TextureBlock
        {
            Dimension = TextureDimension.Texture2D,
            Width = 1, Height = 1, Depth = 1, MipLevelCount = 1, ArrayLayerCount = 1,
            Format = Format.R8UNorm, SampledFormat = Format.R8UNorm,
            SampledDimension = TextureViewDimension.Texture2D,
            MipTiles = [new TextureMipTile { Width = 1, Height = 1, ChunkKey = TextureTiles.ChunkKey(0, 0, 0), DecodedLength = 1 }],
        };
        content.SetBytes(content.MipTiles[0].ChunkKey, new byte[] { 19 });
        using var textureFile = new TextureAsset { Name = "material texture" };
        textureFile.SetBlock(content);
        Guid textureId = project.RegisterAsset(await textureFile.SaveAsync(Path.Combine(project.RootPath, "color.texture.asset")));
        using MaterialAsset parent = new StandardMaterialAsset { Name = "parent" };
        _ = new StandardMaterialParameters(parent.GetBlock<MaterialBlock>())
        {
            AlbedoMap = new(textureId), Roughness = 0.25f,
            BaseColorTint = new Vector4(0.2f, 0.4f, 0.6f, 1f),
        };
        Guid parentId = project.RegisterAsset(await parent.SaveAsync(Path.Combine(project.RootPath, "parent.material.asset")));
        using MaterialAsset child = new StandardMaterialAsset { Name = "child" };
        child.GetBlock<MaterialBlock>().Parent = new(parentId);
        _ = new StandardMaterialParameters(child.GetBlock<MaterialBlock>()) { Roughness = 0.8f };
        Guid childId = project.RegisterAsset(await child.SaveAsync(Path.Combine(project.RootPath, "child.material.asset")));
        await using var loader = new AssetLoader(project.CreateStorage());
        await using MaterialAsset opened = await MaterialAsset.OpenAsync(loader, new AssetId<MaterialAsset>(childId));
        Assert.Equal([parentId], opened.Describe().Dependencies);
        Assert.Equal([childId], project.Manifest.GetReferencers(parentId));
        Assert.Equal(new AssetId<MaterialAsset>(parentId), opened.GetBlock<MaterialBlock>().Parent);
        string copied = directory.File("copy.material.asset");
        await opened.SaveAsync(copied);
        await using MaterialAsset copy = await MaterialAsset.OpenAsync(copied);
        Assert.Equal(opened.Id, copy.Id);
        Assert.Equal([parentId], copy.Describe().Dependencies);
        using Material first = await opened.CreateAsync<StandardMaterial>(loader);
        using Material second = await copy.CreateAsync<StandardMaterial>(loader);
        StandardMaterial a = Assert.IsType<StandardMaterial>(first);
        StandardMaterial b = Assert.IsType<StandardMaterial>(second);
        using Material shared = await copy.CreateAsync<StandardMaterial>(loader);
        Assert.Same(b.AlbedoMap, Assert.IsType<StandardMaterial>(shared).AlbedoMap);
        shared.Dispose();
        Assert.Equal(0.8f, a.Roughness);
        Assert.Equal(new Vector4(0.2f, 0.4f, 0.6f, 1f), a.BaseColorTint);
        Assert.Same(a.AlbedoMap, b.AlbedoMap);
        await opened.DisposeAsync();
        await copy.DisposeAsync();
        first.Dispose();
        Assert.True(b.AlbedoMap!.TryGetTile(TextureTiles.ChunkKey(0, 0, 0), out var bytes));
        Assert.Equal(new byte[] { 19 }, bytes.ToArray());
        await loader.DisposeAsync();
        Assert.True(b.AlbedoMap.IsDisposed);
        Assert.False(second.IsDisposed);
    }

    [Fact]
    public async Task CyclicParentsFailWhenInstantiatingAndDoNotChangeAnExistingMaterial()
    {
        using var directory = new AssetTestDirectory();
        var project = new AssetProject(directory.Path, []);
        Guid id = Guid.NewGuid();
        using MaterialAsset file = new StandardMaterialAsset(id)
        {
            Name = "cycle",
        };
        file.GetBlock<MaterialBlock>().Parent = new(id);
        project.RegisterAsset(await file.SaveAsync(Path.Combine(project.RootPath, "cycle.material.asset")));
        await using var loader = new AssetLoader(project.CreateStorage());
        await using MaterialAsset opened = await MaterialAsset.OpenAsync(loader, new AssetId<MaterialAsset>(id));
        using var material = new StandardMaterial { Name = "before", Roughness = 0.4f };
        await Assert.ThrowsAsync<InvalidDataException>(() => opened.UpdateAsync(material, loader).AsTask());
        Assert.Equal("before", material.Name);
        Assert.Equal(0.4f, material.Roughness);
    }

    [Fact]
    public async Task ThrowingObserverDoesNotDestroyCommittedTexturesOrSkipOtherObservers()
    {
        using var directory = new AssetTestDirectory();
        var project = new AssetProject(directory.Path, []);
        using var content = new TextureBlock
        {
            Dimension = TextureDimension.Texture2D,
            Width = 1, Height = 1, Depth = 1, MipLevelCount = 1, ArrayLayerCount = 1,
            Format = Format.R8UNorm, SampledFormat = Format.R8UNorm,
            SampledDimension = TextureViewDimension.Texture2D,
            MipTiles = [new TextureMipTile { Width = 1, Height = 1, ChunkKey = TextureTiles.ChunkKey(0, 0, 0), DecodedLength = 1 }],
        };
        content.SetBytes(content.MipTiles[0].ChunkKey, new byte[] { 73 });
        using var pixels = new TextureAsset { Name = "replacement pixels" };
        pixels.SetBlock(content);
        Guid id = project.RegisterAsset(await pixels.SaveAsync(Path.Combine(project.RootPath, "replacement.texture.asset")));
        using MaterialAsset file = new StandardMaterialAsset { Name = "after" };
        _ = new StandardMaterialParameters(file.GetBlock<MaterialBlock>())
        {
            AlbedoMap = new(id), Roughness = 0.7f,
        };
        await using var loader = new AssetLoader(project.CreateStorage());
        using var material = new StandardMaterial { Name = "before" };
        int notifications = 0;
        material.Changed += _ => throw new InvalidOperationException("test observer failure");
        material.Changed += _ => notifications++;

        await Assert.ThrowsAsync<AggregateException>(() => file.UpdateAsync(material, loader).AsTask());
        Assert.Equal(1, notifications);
        Assert.Equal("after", material.Name);
        Assert.Equal(0.7f, material.Roughness);
        Assert.True(material.AlbedoMap!.TryGetTile(TextureTiles.ChunkKey(0, 0, 0), out var bytes));
        Assert.Equal(new byte[] { 73 }, bytes.ToArray());
    }

    private static async ValueTask<AssetId<TextureAsset>> SaveColorAsync(
        AssetProject project, AssetTestDirectory directory, string name, byte[] rgba)
    {
        using var pixels = new TextureBlock
        {
            Dimension = TextureDimension.Texture2D,
            Width = 1, Height = 1, Depth = 1, MipLevelCount = 1, ArrayLayerCount = 1,
            Format = Format.R8G8B8A8UNorm, SampledFormat = Format.R8G8B8A8UNorm,
            SampledDimension = TextureViewDimension.Texture2D,
            MipTiles = [new TextureMipTile
            {
                Width = 1, Height = 1, RowPitch = 4, SlicePitch = 4,
                ChunkKey = TextureTiles.ChunkKey(0, 0, 0), DecodedLength = 4,
            }],
        };
        pixels.SetBytes(pixels.MipTiles[0].ChunkKey, rgba);
        using var asset = new TextureAsset { Name = name };
        asset.SetBlock(pixels);
        return new(project.RegisterAsset(await asset.SaveAsync(directory.File(name + ".texture.asset"))));
    }
}
