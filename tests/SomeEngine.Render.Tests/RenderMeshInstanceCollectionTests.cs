using SomeEngine.Assets;
using SomeEngine.Assets.Schema;
using SomeEngine.Render.Instances;

namespace SomeEngine.Render.Tests;

public sealed class RenderMeshInstanceCollectionTests
{
    [Fact]
    public void HandlesRemainStableAndSlotReuseChangesGeneration()
    {
        using var collection = new RenderMeshInstanceCollection();
        using RenderMeshInstanceSet first = CreateSet(1);
        using RenderMeshInstanceSet second = CreateSet(2);
        RenderMeshInstanceHandle firstHandle = collection.Add(first);
        RenderMeshInstanceHandle secondHandle = collection.Add(second);

        Assert.True(collection.Contains(firstHandle));
        Assert.Same(first, collection.GetRequired(firstHandle));
        Assert.Equal(2, collection.Count);
        Assert.True(collection.Remove(firstHandle));
        Assert.False(collection.Contains(firstHandle));

        using RenderMeshInstanceSet replacement = CreateSet(3);
        RenderMeshInstanceHandle replacementHandle = collection.Add(replacement);
        Assert.NotEqual(firstHandle, replacementHandle);
        Assert.True(collection.Contains(replacementHandle));
        Assert.True(collection.Contains(secondHandle));
    }

    [Fact]
    public void SnapshotFreezesMembershipAndOwnsSetSnapshots()
    {
        using var collection = new RenderMeshInstanceCollection();
        using RenderMeshInstanceSet first = CreateSet(4);
        RenderMeshInstanceHandle firstHandle = collection.Add(first);
        using RenderMeshInstanceCollectionSnapshot snapshot = collection.Capture();

        Assert.Equal(1, snapshot.Entries.Length);
        Assert.Equal(firstHandle, snapshot.Entries[0].Handle);
        Assert.Equal(4, snapshot.Entries[0].Snapshot.Count);

        using RenderMeshInstanceSet second = CreateSet(2);
        _ = collection.Add(second);
        Assert.Equal(1, snapshot.Entries.Length);
        Assert.Equal(2, collection.Count);
    }

    [Fact]
    public void CollectionDisposesOnlyExplicitlyOwnedSets()
    {
        var owned = CreateSet(1);
        using var external = CreateSet(1);
        var collection = new RenderMeshInstanceCollection();
        RenderMeshInstanceHandle ownedHandle = collection.Add(owned, ownsSet: true);
        _ = collection.Add(external);

        Assert.True(collection.Remove(ownedHandle));
        Assert.Throws<ObjectDisposedException>(() => _ = owned.Count);
        Assert.Equal(1, external.Count);
        collection.Dispose();
        Assert.Equal(1, external.Count);
    }

    private static RenderMeshInstanceSet CreateSet(int count) => new(
        TestAssets.Mesh(101),
        [TestAssets.Material(102)],
        count,
        static (_, current, previous) =>
        {
            current.Clear();
            previous.Clear();
        });
}
