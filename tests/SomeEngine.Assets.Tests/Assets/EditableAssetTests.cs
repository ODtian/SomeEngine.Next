using SomeEngine.Assets.Schema;
using SomeEngine.Resources;
using SomeEngine.Graphics;
using Texture = SomeEngine.Resources.Texture;
using TextureViewDimension = SomeEngine.Graphics.TextureViewDimension;

namespace SomeEngine.Assets.Tests.Assets;

public sealed class EditableAssetTests
{
    [Fact]
    public void MaterialDefaultsAndIndependentWritesUseTheDeclaredProperties()
    {
        using var first = new StandardMaterialAsset { Name = "first" };
        using var second = first.Duplicate();
        var firstValues = new StandardMaterialParameters(first.GetBlock<MaterialBlock>());
        var secondValues = new StandardMaterialParameters(second.GetBlock<MaterialBlock>());
        Assert.Equal(0.5f, firstValues.Roughness);
        Assert.False(first.GetBlock<MaterialBlock>().HasOverride(nameof(StandardMaterial.Roughness)));
        secondValues.Roughness = 0;
        second.Name = "second";
        Assert.Equal(0.5f, firstValues.Roughness);
        Assert.Equal("first", first.Name);
        Assert.Equal(0, secondValues.Roughness);
        Assert.True(second.GetBlock<MaterialBlock>().HasOverride(nameof(StandardMaterial.Roughness)));
        Assert.True(second.GetBlock<MaterialBlock>().Reset(nameof(StandardMaterial.Roughness)));
        Assert.Equal(0.5f, secondValues.Roughness);
    }

    [Fact]
    public async Task MaterialSaveWritesCurrentOverridesAndResetRestoresParentValue()
    {
        using var directory = new AssetTestDirectory();
        var project = new AssetProject(directory.Path, []);
        using var parent = new StandardMaterialAsset { Name = "parent" };
        _ = new StandardMaterialParameters(parent.GetBlock<MaterialBlock>()) { Roughness = 0.25f };
        project.RegisterAsset(await parent.SaveAsync(directory.File("parent.material.asset")));
        using var child = new StandardMaterialAsset { Name = "child" };
        child.GetBlock<MaterialBlock>().Parent = parent.Reference;
        _ = new StandardMaterialParameters(child.GetBlock<MaterialBlock>()) { Roughness = 0 };
        project.RegisterAsset(await child.SaveAsync(directory.File("child.material.asset")));
        await using var loader = new AssetLoader(project.CreateStorage());
        using var opened = await StandardMaterialAsset.OpenAsync(loader, child.Reference);
        using var zero = await opened.CreateAsync(loader);
        Assert.Equal(0, zero.Roughness);
        Assert.Equal(new[] { parent.Id }, opened.Describe().Dependencies);
        opened.GetBlock<MaterialBlock>().Reset(nameof(StandardMaterial.Roughness));
        opened.Name = "edited";
        AssetEntry saved = await opened.SaveAsync(directory.File("edited.material.asset"));
        using var reloaded = await StandardMaterialAsset.OpenAsync(saved.Path);
        using var inherited = await reloaded.CreateAsync(loader);
        Assert.Equal(0.25f, inherited.Roughness);
        Assert.Equal("edited", reloaded.Name);
        Assert.Equal(reloaded.SourceVersion, saved.Version);
        Assert.Equal(reloaded.Describe().Dependencies, saved.Dependencies);
    }

    [Fact]
    public async Task TextureMetadataCanBeEditedAndSavedWhileItsPayloadStaysInTheSource()
    {
        using var directory = new AssetTestDirectory();
        using var pixels = new TextureBlock
        {
            Dimension = TextureDimension.Texture2D,
            Width = 1, Height = 1, Depth = 1, MipLevelCount = 1, ArrayLayerCount = 1,
            Format = Format.R8UNorm, SampledFormat = Format.R8UNorm,
            SampledDimension = TextureViewDimension.Texture2D,
            MipTiles = [new TextureMipTile { Width = 1, Height = 1, ChunkKey = TextureTiles.ChunkKey(0, 0, 0), DecodedLength = 1 }],
        };
        pixels.SetBytes(pixels.MipTiles[0].ChunkKey, new byte[] { 37 });
        using var texture = new TextureAsset { Name = "before" };
        texture.SetBlock(pixels);
        AssetEntry original = await texture.SaveAsync(directory.File("pixels.texture.asset"));
        using var opened = await TextureAsset.OpenAsync(original.Path);
        using var branch = opened.Duplicate();
        branch.Name = "after";
        Assert.Empty(branch.GetBlock<TextureBlock>().Chunks.Keys);
        await branch.SaveAsync(directory.File("edited.texture.asset"));
        using var reloaded = await TextureAsset.OpenAsync(directory.File("edited.texture.asset"));
        var value = await reloaded.GetBlock<TextureBlock>().CreateAsync(reloaded.Name);
        Assert.True(value.TryGetTile(TextureTiles.ChunkKey(0, 0, 0), out var bytes));
        Assert.Equal(new byte[] { 37 }, bytes.ToArray());
        Assert.Equal("after", value.Name);
        Assert.Equal("before", opened.Name);
    }
}
