using System.Numerics;
using SomeEngine.Assets.Schema;
using SomeEngine.Graphics;
using SomeEngine.Serialization.Streaming;
using Texture = SomeEngine.Assets.Schema.Texture;
using TextureViewDimension = SomeEngine.Graphics.TextureViewDimension;

namespace SomeEngine.Assets.Tests.Assets;

public sealed class MaterialFileTests
{
    [Fact]
    public async Task ParentAndOverridesRoundTripWithoutCreatingObjectsDuringOpen()
    {
        using var directory = new AssetTestDirectory();
        var project = new AssetProject(directory.Path, []);
        using Asset<Texture> textureFile = new TextureData
        {
            Name = "material texture", Dimension = TextureDimension.Texture2D,
            Width = 1, Height = 1, Depth = 1, MipLevelCount = 1, ArrayLayerCount = 1,
            Format = Format.R8UNorm, SampledFormat = Format.R8UNorm,
            SampledDimension = TextureViewDimension.Texture2D,
            MipTiles = [new TextureMipTile { Width = 1, Height = 1, Payload = new byte[] { 19 } }],
        }.ToAsset();
        Guid textureId = project.CreateAsset("color.texture.asset", textureFile);
        using Asset<Material> parent = new StandardMaterialData
        {
            Name = "parent", AlbedoMap = new(textureId), Roughness = 0.25f,
            BaseColorTint = new Vector4(0.2f, 0.4f, 0.6f, 1f),
        }.ToAsset();
        Guid parentId = project.CreateAsset("parent.material.asset", parent);
        using Asset<Material> child = new MaterialData
        {
            Name = "child", Parent = new(parentId),
            Parameters = [new("Roughness", MaterialParameterKind.Float) { Float = 0.8f }],
        }.ToAsset();
        Guid childId = project.CreateAsset("child.material.asset", child);
        await using var loader = new AssetLoader(project.CreateStorage());
        await using Asset<Material> opened = await loader.OpenAsync(new AssetId<Material>(childId));
        Assert.Equal([parentId], opened.Dependencies);
        Assert.Equal([childId], project.Manifest.GetReferencers(parentId));
        Assert.Equal(new AssetId<Material>(parentId), opened.GetFieldsGenerated<MaterialData>().Parent);
        string copied = directory.File("copy.material.asset");
        await AssetWriter.WriteAsync(opened, copied);
        await using Asset<Material> copy = await AssetLoader.OpenFileAsync<Material>(copied);
        Assert.Equal(opened.Id, copy.Id);
        Assert.Equal([parentId], copy.Dependencies);
        using Material first = await Material.CreateAsync(opened, loader);
        using Material second = await Material.CreateAsync(copy, loader);
        StandardMaterial a = Assert.IsType<StandardMaterial>(first);
        StandardMaterial b = Assert.IsType<StandardMaterial>(second);
        Assert.Equal(0.8f, a.Roughness);
        Assert.Equal(new Vector4(0.2f, 0.4f, 0.6f, 1f), a.BaseColorTint);
        Assert.NotSame(a.AlbedoMap, b.AlbedoMap);
        await opened.DisposeAsync();
        await copy.DisposeAsync();
        await loader.DisposeAsync();
        first.Dispose();
        using ResidentChunkLease bytes = await b.AlbedoMap!.AcquireMipTileAsync(0, 0, 0);
        Assert.Equal(new byte[] { 19 }, bytes.Memory.ToArray());
    }

    [Fact]
    public async Task CyclicParentsFailWhenInstantiatingAndDoNotChangeAnExistingMaterial()
    {
        using var directory = new AssetTestDirectory();
        var project = new AssetProject(directory.Path, []);
        Guid id = Guid.NewGuid();
        using Asset<Material> file = new MaterialData
        {
            AssetGuid = id.ToString(), Name = "cycle", Parent = new(id),
        }.ToAsset();
        project.CreateAsset("cycle.material.asset", file);
        await using var loader = new AssetLoader(project.CreateStorage());
        await using Asset<Material> opened = await loader.OpenAsync(new AssetId<Material>(id));
        using var material = new StandardMaterial { Name = "before", Roughness = 0.4f };
        await Assert.ThrowsAsync<InvalidDataException>(() => material.UpdateAsync(opened, loader).AsTask());
        Assert.Equal("before", material.Name);
        Assert.Equal(0.4f, material.Roughness);
    }

    [Fact]
    public async Task ThrowingObserverDoesNotDestroyCommittedTexturesOrSkipOtherObservers()
    {
        using var directory = new AssetTestDirectory();
        var project = new AssetProject(directory.Path, []);
        using Asset<Texture> pixels = new TextureData
        {
            Name = "replacement pixels", Dimension = TextureDimension.Texture2D,
            Width = 1, Height = 1, Depth = 1, MipLevelCount = 1, ArrayLayerCount = 1,
            Format = Format.R8UNorm, SampledFormat = Format.R8UNorm,
            SampledDimension = TextureViewDimension.Texture2D,
            MipTiles = [new TextureMipTile { Width = 1, Height = 1, Payload = new byte[] { 73 } }],
        }.ToAsset();
        Guid id = project.CreateAsset("replacement.texture.asset", pixels);
        using Asset<Material> file = new StandardMaterialData
        {
            Name = "after", AlbedoMap = new(id), Roughness = 0.7f,
        }.ToAsset();
        await using var loader = new AssetLoader(project.CreateStorage());
        using var material = new StandardMaterial { Name = "before" };
        int notifications = 0;
        material.Changed += _ => throw new InvalidOperationException("test observer failure");
        material.Changed += _ => notifications++;

        await Assert.ThrowsAsync<AggregateException>(() => material.UpdateAsync(file, loader).AsTask());
        Assert.Equal(1, notifications);
        Assert.Equal("after", material.Name);
        Assert.Equal(0.7f, material.Roughness);
        await loader.DisposeAsync();
        using ResidentChunkLease bytes = await material.AlbedoMap!.AcquireMipTileAsync(0, 0, 0);
        Assert.Equal(new byte[] { 73 }, bytes.Memory.ToArray());
    }
}
