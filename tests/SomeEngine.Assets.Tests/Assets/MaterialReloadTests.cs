using SomeEngine.Assets.Schema;
using SomeEngine.Resources;

namespace SomeEngine.Assets.Tests.Assets;

public sealed class MaterialReloadTests
{
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
    [Fact]
    public void CapturedMaterialValuesRemainUnchangedAfterSourceEdits()
    {
        var material = new StandardMaterial { Name = "before", Roughness = 0.25f };
        MaterialData before = material.CaptureData();
        uint roughness = Assert.Single(before.Properties, property => property.Name == "Roughness").Slot;
        uint metallic = Assert.Single(before.Properties, property => property.Name == "MetallicFactor").Slot;
        material.Name = "after";
        material.Roughness = 0.75f;
        MaterialData after = material.CaptureData();
        Assert.Equal("before", before.Name);
        Assert.Equal(0.25f, before.GetValue(roughness));
        Assert.Equal("after", after.Name);
        Assert.Equal(0.75f, after.GetValue(roughness));
        Assert.Equal(before.MaterialId, after.MaterialId);
        Assert.True(after.GetPropertyRevision(roughness) > before.GetPropertyRevision(roughness));
        Assert.Equal(before.GetPropertyRevision(metallic), after.GetPropertyRevision(metallic));
    }
}
