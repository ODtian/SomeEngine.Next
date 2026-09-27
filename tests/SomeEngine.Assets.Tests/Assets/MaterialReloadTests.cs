using SomeEngine.Assets.Schema;
using SomeEngine.Resources;

namespace SomeEngine.Assets.Tests.Assets;

public sealed class MaterialReloadTests
{
    [Fact]
    public async Task MaterialsKeepDirectShaderReferencesWithoutTakingTheirLifetime()
    {
        using var directory = new AssetTestDirectory();
        await using var loader = new AssetLoader(new LooseAssetStorage(directory.Path, new AssetManifest()));
        using var shader = new Shader { Name = "shared shader" };
        using MaterialAsset file = new StandardMaterialAsset();
        file.GetBlock<MaterialBlock>().Shader = new(Guid.NewGuid());
        using Material first = await file.CreateAsync(loader, (_, _) => ValueTask.FromResult(shader), null);
        using Material second = await file.CreateAsync(loader, (_, _) => ValueTask.FromResult(shader), null);
        Assert.Same(shader, first.Shader);
        Assert.Same(shader, second.Shader);
        first.Dispose();
        Assert.False(shader.IsDisposed);
        ulong revision = second.Revision;
        using var replacement = new Shader { Name = "changed shader" };
        second.Shader = replacement;
        Assert.Same(replacement, second.Shader);
        Assert.Equal(revision + 1, second.Revision);
        second.Dispose();
        Assert.False(shader.IsDisposed);
    }

    [Fact]
    public async Task ReloadChangesTheExistingObjectAtTheExplicitUpdateBoundary()
    {
        using var directory = new AssetTestDirectory();
        await using var loader = new AssetLoader(new LooseAssetStorage(directory.Path, new AssetManifest()));
        using var current = new StandardMaterial { Name = "before" };
        using MaterialAsset file = new StandardMaterialAsset { Name = "after" };
        _ = new StandardMaterialParameters(file.GetBlock<MaterialBlock>()) { Roughness = 0.75f };
        ulong revision = current.Revision;
        int notifications = 0;
        current.Changed += material =>
        {
            Assert.Same(current, material);
            Assert.Equal("after", material.Name);
            Assert.Equal(0.75f, current.Roughness);
            notifications++;
        };
        await file.UpdateAsync(current, loader);
        Assert.Equal("after", current.Name);
        Assert.Equal(revision + 1, current.Revision);
        Assert.Equal(1, notifications);
    }

    [Fact]
    public void SlotWritesNotifyAfterTheValueChangesAndIgnoreEqualValues()
    {
        var material = new StandardMaterial { Roughness = 0.25f };
        int notifications = 0;
        material.Changed += changed =>
        {
            Assert.Same(material, changed);
            Assert.Equal(0.75f, material.Roughness);
            notifications++;
        };
        material.Roughness = 0.25f;
        material.Roughness = 0.75f;
        material.Roughness = 0.75f;
        Assert.Equal(1, notifications);
    }
}
