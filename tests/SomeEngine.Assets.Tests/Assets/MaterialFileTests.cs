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
    public async Task ParentAndOverridesRoundTripWithoutCreatingObjectsDuringOpen()
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
        content.Chunks.Set(content.MipTiles[0].ChunkKey, new byte[] { 19 });
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
        using Material first = await opened.CreateAsync(loader);
        using Material second = await copy.CreateAsync(loader);
        StandardMaterial a = Assert.IsType<StandardMaterial>(first);
        StandardMaterial b = Assert.IsType<StandardMaterial>(second);
        using Material shared = await copy.CreateAsync(loader, null, (_, _) => ValueTask.FromResult(b.AlbedoMap!));
        Assert.Same(b.AlbedoMap, Assert.IsType<StandardMaterial>(shared).AlbedoMap);
        shared.Dispose();
        Assert.Equal(0.8f, a.Roughness);
        Assert.Equal(new Vector4(0.2f, 0.4f, 0.6f, 1f), a.BaseColorTint);
        Assert.NotSame(a.AlbedoMap, b.AlbedoMap);
        await opened.DisposeAsync();
        await copy.DisposeAsync();
        await loader.DisposeAsync();
        first.Dispose();
        Assert.True(b.AlbedoMap!.TryRetainTile(TextureTiles.ChunkKey(0, 0, 0), out var bytes));
        using (bytes) Assert.Equal(new byte[] { 19 }, bytes.ToArray());
    }

    [Fact]
    public async Task CyclicParentsFailWhenInstantiatingAndDoNotChangeAnExistingMaterial()
    {
        using var directory = new AssetTestDirectory();
        var project = new AssetProject(directory.Path, []);
        Guid id = Guid.NewGuid();
        using MaterialAsset file = new StandardMaterialAsset(SomeEngine.Serialization.Containers.PageArena.Shared, id)
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
        content.Chunks.Set(content.MipTiles[0].ChunkKey, new byte[] { 73 });
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
        await loader.DisposeAsync();
        Assert.True(material.AlbedoMap!.TryRetainTile(TextureTiles.ChunkKey(0, 0, 0), out var bytes));
        using (bytes) Assert.Equal(new byte[] { 73 }, bytes.ToArray());
    }
}
