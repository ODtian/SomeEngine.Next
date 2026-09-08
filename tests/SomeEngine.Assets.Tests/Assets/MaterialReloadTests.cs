using SomeEngine.Assets.Schema;

namespace SomeEngine.Assets.Tests.Assets;

public sealed class MaterialReloadTests
{
    [Fact]
    public async Task ReloadChangesTheExistingObjectAtTheExplicitUpdateBoundary()
    {
        var current = new StandardMaterial
        {
            AssetGuid = "00112233445566778899aabbccddeeff",
            Name = "before",
        };
        var replacement = new StandardMaterial
        {
            AssetGuid = current.AssetGuid,
            Name = "after",
        };

        _ = current.Slots;
        _ = replacement.Slots;
        ulong revision = current.Revision;
        int notifications = 0;
        current.Changed += material =>
        {
            Assert.Same(current, material);
            Assert.Equal(revision + 1, material.Revision);
            Assert.Equal("after", material.Name);
            notifications++;
        };
        await Material.ApplyReloadAsync(current, replacement, default);
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
