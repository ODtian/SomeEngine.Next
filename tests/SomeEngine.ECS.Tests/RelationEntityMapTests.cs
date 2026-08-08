using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Relations;

namespace SomeEngine.ECS.Tests;

public sealed class RelationEntityMapTests
{
    [Fact]
    public void CloneDetached_SharesUntouchedPages_AndDetachesOnlyWrittenPage()
    {
        Entity firstPage = new(1, 0);
        Entity secondPage = new(257, 1);
        Entity secondPagePeer = new(258, 1);
        var source = new RelationEntityMap<int>();
        source.Add(firstPage, 10);
        source.Add(secondPage, 20);

        RelationEntityMap<int> clone = source.CloneDetached();

        Assert.Same(source.BackingIdentity, clone.BackingIdentity);
        Assert.Same(source.PageBackingIdentity(firstPage), clone.PageBackingIdentity(firstPage));
        Assert.Same(source.PageBackingIdentity(secondPage), clone.PageBackingIdentity(secondPage));

        clone.Add(secondPagePeer, 30);

        Assert.NotSame(source.BackingIdentity, clone.BackingIdentity);
        Assert.Same(source.PageBackingIdentity(firstPage), clone.PageBackingIdentity(firstPage));
        Assert.NotSame(source.PageBackingIdentity(secondPage), clone.PageBackingIdentity(secondPage));
        Assert.False(source.ContainsKey(secondPagePeer));
        Assert.Equal(30, clone[secondPagePeer]);
        Assert.Equal(20, source[secondPage]);
    }

    [Fact]
    public void CloneDetached_SourceWriteAlsoDetaches_AndPreservesClonePreimage()
    {
        Entity original = new(1, 1);
        Entity sourceOnly = new(2, 1);
        var source = new RelationEntityMap<int>();
        source.Add(original, 10);
        RelationEntityMap<int> clone = source.CloneDetached();
        object sharedPage = source.PageBackingIdentity(original)!;

        source.Add(sourceOnly, 20);

        Assert.NotSame(source.BackingIdentity, clone.BackingIdentity);
        Assert.NotSame(sharedPage, source.PageBackingIdentity(original));
        Assert.Same(sharedPage, clone.PageBackingIdentity(original));
        Assert.False(clone.ContainsKey(sourceOnly));
        Assert.Equal(10, clone[original]);
    }

    [Fact]
    public void RecycledSlot_RejectsStaleGeneration_AndAcceptsReplacementAfterRemoval()
    {
        Entity oldEntity = new(17, 1);
        Entity recycledEntity = new(17, 2);
        var map = new RelationEntityMap<int>();
        map.Add(oldEntity, 10);

        Assert.False(map.ContainsKey(recycledEntity));
        Assert.False(map.TryGetValue(recycledEntity, out _));
        Assert.Throws<InvalidOperationException>(() => map.Add(recycledEntity, 20));

        Assert.True(map.Remove(oldEntity));
        map.Add(recycledEntity, 20);

        Assert.False(map.ContainsKey(oldEntity));
        Assert.Equal(20, map[recycledEntity]);
    }

    [Fact]
    public void Enumeration_VisitsOnlyOccupiedEntities_AfterSwapRemoval()
    {
        Entity first = new(1, 1);
        Entity removed = new(2, 1);
        Entity third = new(513, 4);
        var map = new RelationEntityMap<int>();
        map.Add(first, 10);
        map.Add(removed, 20);
        map.Add(third, 30);
        Assert.True(map.Remove(removed));

        var entries = new List<KeyValuePair<Entity, int>>();
        foreach (KeyValuePair<Entity, int> entry in map)
            entries.Add(entry);

        Assert.Equal(2, map.Count);
        Assert.Contains(new KeyValuePair<Entity, int>(first, 10), entries);
        Assert.Contains(new KeyValuePair<Entity, int>(third, 30), entries);
        Assert.DoesNotContain(entries, static entry => entry.Key.Index == 2);
    }
}
