using SomeEngine.Assets.Schema;
using SomeEngine.Resources;
using SomeEngine.Render.Components;
using SomeEngine.Render.Instances;

namespace SomeEngine.Render.Tests;

public sealed class RenderMeshInstanceSetTests
{
    [Fact]
    public void BufferedSetExposesEditedRowsAndCopiesMaterialMembership()
    {
        Material[] materials = [TestAssets.Material(1)];
        Material original = materials[0];
        using var set = RenderMeshInstanceSet.CreateBuffered(TestAssets.Mesh(1), materials, 4);
        materials[0] = TestAssets.Material(2);
        Assert.Same(original, set.Materials[0]);
        Assert.Equal(0, set.Count);
        set.Buffer!.SetCount(3);
        Assert.Equal(3, set.Count);
        ulong revision = set.Revision;
        set.SetMaterials(materials);
        Assert.Same(materials[0], set.Materials[0]);
        Assert.True(set.Revision > revision);
        set.SetBoundsExpansion(2.5f);
        Assert.Equal(2.5f, set.BoundsExpansion);
    }

    [Fact]
    public void ClosingTheSetClosesTheBufferItCreated()
    {
        var set = RenderMeshInstanceSet.CreateBuffered(TestAssets.Mesh(2), []);
        var buffer = set.Buffer!;
        set.Dispose();
        set.Dispose();
        Assert.Throws<ObjectDisposedException>(() => buffer.Add());
        Assert.Throws<ObjectDisposedException>(() => set.SetBoundsExpansion(1));
    }
}
