using SomeEngine.ECS.Systems;
using SomeEngine.Render.Instances;
using SomeEngine.Render.Systems;

namespace SomeEngine.Render.Tests;

public sealed class RenderInstanceSetTests
{
    [Fact]
    public void UserSetOwnsLogicalRowsWithoutRequiringRenderEntities()
    {
        RenderInstancePropertyLayout layout = CreateLayout();
        using var set = new RenderInstanceSet(layout);

        Assert.Same(layout, set.Layout);
        Assert.Equal(0, set.Count);
        Assert.Equal(1ul, set.Revision);
        Assert.False(set.HasPublishedData);
        Assert.Null(set.Current);
        Assert.IsAssignableFrom<ISystem<RenderPrepareSystemContext>>(set);
        Assert.IsAssignableFrom<IRenderInstanceBatchSource<RenderInstanceSingleGroup>>(set);

        RenderInstanceSetWriter writer = static _ => { };
        set.SetData(2_097_152, writer);
        Assert.Equal(2_097_152, set.Count);
        Assert.Equal(2ul, set.Revision);

        set.Invalidate();
        Assert.Equal(2_097_152, set.Count);
        Assert.Equal(3ul, set.Revision);

        set.Clear();
        Assert.Equal(0, set.Count);
        Assert.Equal(4ul, set.Revision);
    }

    [Fact]
    public void UserSetValidatesMutationInputsAndClosesAfterDispose()
    {
        RenderInstancePropertyLayout layout = CreateLayout();
        Assert.Throws<ArgumentNullException>(() => new RenderInstanceSet(null!));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new RenderInstanceSet(layout, -1, static _ => { }));
        Assert.Throws<ArgumentNullException>(
            () => new RenderInstanceSet(layout, 1, null!));

        var set = new RenderInstanceSet(layout, 1, static _ => { });
        Assert.Throws<ArgumentOutOfRangeException>(() => set.SetData(-1, static _ => { }));
        Assert.Throws<ArgumentNullException>(() => set.SetData(1, null!));
        set.Dispose();
        set.Dispose();
        Assert.Throws<ObjectDisposedException>(() => set.SetData(1, static _ => { }));
        Assert.Throws<ObjectDisposedException>(set.Invalidate);
    }

    private static RenderInstancePropertyLayout CreateLayout()
    {
        var builder = new RenderInstancePropertyLayoutBuilder();
        _ = builder.Register<uint>(
            "SomeEngine.Render.Tests",
            new RenderInstancePropertyKey("someengine.tests.instance_value"),
            new RenderInstancePropertyEncoding(
                "someengine.tests.uint32.v1",
                valueSize: sizeof(uint),
                storageAlignment: sizeof(uint),
                storageStride: sizeof(uint),
                metadataWordCount: 1));
        return builder.Freeze();
    }
}
