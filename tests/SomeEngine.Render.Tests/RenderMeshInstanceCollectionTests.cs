using SomeEngine.Assets;
using SomeEngine.Assets.Schema;
using SomeEngine.Resources;
using SomeEngine.Render.Instances;

namespace SomeEngine.Render.Tests;

public sealed class RenderMeshInstanceCollectionTests
{
    [Fact]
    public void HandlesIdentifyTheirCollectionAndCannotReviveAfterRemoval()
    {
        using var left = new RenderMeshInstanceCollection();
        using var right = new RenderMeshInstanceCollection();
        using var first = RenderMeshInstanceSet.CreateBuffered(TestAssets.Mesh(1), []);
        using var second = RenderMeshInstanceSet.CreateBuffered(TestAssets.Mesh(2), []);
        var firstId = left.Add(first);
        var rightId = right.Add(second);
        Assert.NotEqual(firstId, rightId);
        Assert.False(right.Contains(firstId));
        Assert.False(right.Remove(firstId));
        Assert.Same(second, right.GetRequired(rightId));
        Assert.True(left.Remove(firstId));
        var replacement = left.Add(second);
        Assert.NotEqual(firstId, replacement);
        Assert.False(left.TryGet(firstId, out _));
        Assert.Same(second, left.GetRequired(replacement));
    }

    [Fact]
    public void DuplicateSetIsRejectedAndBorrowedSetsSurviveCollectionDisposal()
    {
        var collection = new RenderMeshInstanceCollection();
        using var set = RenderMeshInstanceSet.CreateBuffered(TestAssets.Mesh(3), []);
        var id = collection.Add(set);
        Assert.Throws<ArgumentException>(() => collection.Add(set));
        Assert.Equal(1, collection.Count);
        Assert.Same(set, collection.GetRequired(id));
        collection.Dispose();
        set.Buffer!.Add();
        Assert.Equal(1, set.Count);
    }

    [Fact]
    public void RemovingAnOwnedSetClosesItsBuffer()
    {
        using var collection = new RenderMeshInstanceCollection();
        var set = RenderMeshInstanceSet.CreateBuffered(TestAssets.Mesh(4), []);
        var buffer = set.Buffer!;
        var id = collection.Add(set, ownsSet: true);
        Assert.True(collection.Remove(id));
        Assert.Throws<ObjectDisposedException>(() => buffer.Add());
        Assert.False(collection.Remove(id));
    }
}
