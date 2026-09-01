using SomeEngine.Assets.Schema;

namespace SomeEngine.Assets.Tests.Assets;

public sealed class MaterialReloadTests
{
    [Fact]
    public async Task ReloadChangesTheExistingObjectAtTheExplicitUpdateBoundary()
    {
        var current = new Material
        {
            AssetGuid = "00112233445566778899aabbccddeeff",
            Name = "before",
            Passes = [],
            Textures = [],
            Scalars = [],
        };
        var replacement = new Material
        {
            AssetGuid = current.AssetGuid,
            Name = "after",
            Passes = [],
            Textures = [],
            Scalars = [],
        };

        _ = current.Slots;
        _ = replacement.Slots;
        ulong revision = current.Revision;
        await Material.ApplyReloadAsync(current, replacement, default);
        Assert.Equal("after", current.Name);
        Assert.Equal(revision + 1, current.Revision);
    }
}
