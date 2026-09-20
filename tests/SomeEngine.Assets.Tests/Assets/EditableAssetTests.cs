using SomeEngine.Assets.Schema;
using SomeEngine.Graphics;
using Texture = SomeEngine.Assets.Schema.Texture;
using TextureViewDimension = SomeEngine.Graphics.TextureViewDimension;

namespace SomeEngine.Assets.Tests.Assets;

public sealed class EditableAssetTests
{
    [Fact]
    public void MaterialDefaultsAndIndependentWritesUseTheDeclaredProperties()
    {
        using var first = new StandardMaterial.Asset { Name = "first" };
        using var second = first.Duplicate();
        Assert.Equal(0.5f, first.Roughness);
        Assert.False(first.HasOverride(nameof(StandardMaterial.Roughness)));
        second.Roughness = 0;
        second.Name = "second";
        Assert.Equal(0.5f, first.Roughness);
        Assert.Equal("first", first.Name);
        Assert.Equal(0, second.Roughness);
        Assert.True(second.HasOverride(nameof(StandardMaterial.Roughness)));
        Assert.True(second.Reset(nameof(StandardMaterial.Roughness)));
        Assert.Equal(0.5f, second.Roughness);
    }

    [Fact]
    public async Task MaterialSaveWritesCurrentOverridesAndResetRestoresParentValue()
    {
        using var directory = new AssetTestDirectory();
        var project = new AssetProject(directory.Path, []);
        using var parent = new StandardMaterial.Asset { Name = "parent", Roughness = 0.25f };
        project.RegisterAsset(await parent.SaveAsync(directory.File("parent.material.asset")));
        using var child = new StandardMaterial.Asset { Name = "child", Parent = parent.Id, Roughness = 0 };
        project.RegisterAsset(await child.SaveAsync(directory.File("child.material.asset")));
        await using var loader = new AssetLoader(project.CreateStorage());
        using var opened = await StandardMaterial.Asset.OpenAsync(loader, child.Id);
        using var zero = await opened.LoadAsync(loader);
        Assert.Equal(0, zero.Roughness);
        Assert.Equal(new[] { parent.Id.Value }, opened.Describe().Dependencies);
        opened.Reset(nameof(StandardMaterial.Roughness));
        opened.Name = "edited";
        AssetEntry saved = await opened.SaveAsync(directory.File("edited.material.asset"));
        using var reloaded = await StandardMaterial.Asset.OpenAsync(saved.Path);
        using var inherited = await reloaded.LoadAsync(loader);
        Assert.Equal(0.25f, inherited.Roughness);
        Assert.Equal("edited", reloaded.Name);
        Assert.Equal(reloaded.SourceVersion, saved.Version);
        Assert.Equal(reloaded.Describe().Dependencies, saved.Dependencies);
    }

    [Fact]
    public async Task TextureMetadataCanBeEditedAndSavedWhileItsPayloadStaysInTheSource()
    {
        using var directory = new AssetTestDirectory();
        using var texture = new Texture.Asset
        {
            Name = "before", Dimension = TextureDimension.Texture2D,
            Width = 1, Height = 1, Depth = 1, MipLevelCount = 1, ArrayLayerCount = 1,
            Format = Format.R8UNorm, SampledFormat = Format.R8UNorm,
            SampledDimension = TextureViewDimension.Texture2D,
            MipTiles = [new TextureMipTile { Width = 1, Height = 1, Payload = new byte[] { 37 } }],
        };
        AssetEntry original = await texture.SaveAsync(directory.File("pixels.texture.asset"));
        using var opened = await Texture.Asset.OpenAsync(original.Path);
        using var branch = opened.Duplicate();
        branch.Name = "after";
        Assert.Null(branch.MipTiles![0].Payload);
        await branch.SaveAsync(directory.File("edited.texture.asset"));
        using var reloaded = await Texture.Asset.OpenAsync(directory.File("edited.texture.asset"));
        await using var value = await reloaded.LoadAsync();
        using var bytes = await value.AcquireMipTileAsync(0, 0, 0);
        Assert.Equal(new byte[] { 37 }, bytes.Memory.ToArray());
        Assert.Equal("after", value.Name);
        Assert.Equal("before", opened.Name);
    }
}
