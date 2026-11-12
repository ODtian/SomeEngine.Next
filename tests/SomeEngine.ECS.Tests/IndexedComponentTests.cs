using SomeEngine.ECS;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;
using Xunit;

namespace SomeEngine.ECS.Tests;

public struct IndexedName : SomeEngine.ECS.IIndexedComponent<string>, IAddedComponent
{
    public string Value;

    public string GetKey() => Value;
}

public class IndexedComponentTests
{
    [Fact]
    public void CopiedIndexMembersSurviveAddReplaceAndRemove()
    {
        var world = new World();
        var first = world.Create(new IndexedName { Value = "shared" });

        ReadOnlySpan<Entity> beforeAdd =
            ReadIndexedEntities<IndexedName, string>(world, "shared");
        var second = world.Create(new IndexedName { Value = "shared" });

        Assert.Equal(new[] { first }, beforeAdd.ToArray());
        Assert.Equal(
            new[] { first, second },
            ReadIndexedEntities<IndexedName, string>(world, "shared").ToArray());

        ReadOnlySpan<Entity> beforeReplace =
            ReadIndexedEntities<IndexedName, string>(world, "shared");
        world.Set(first, new IndexedName { Value = "moved" });

        Assert.Equal(new[] { first, second }, beforeReplace.ToArray());
        Assert.Equal(
            new[] { second },
            ReadIndexedEntities<IndexedName, string>(world, "shared").ToArray());
        Assert.Equal(
            new[] { first },
            ReadIndexedEntities<IndexedName, string>(world, "moved").ToArray());

        ReadOnlySpan<Entity> beforeRemove =
            ReadIndexedEntities<IndexedName, string>(world, "shared");
        world.Remove<IndexedName>(second);

        Assert.Equal(new[] { second }, beforeRemove.ToArray());
        Assert.Empty(ReadIndexedEntities<IndexedName, string>(world, "shared").ToArray());
    }

    [Fact]
    public void CopiedIndexMembersSurviveTypedIndexUpdates()
    {
        using var world = new World();
        Entity entity = world.Create(new IndexedName { Value = "before" });
        Entity[] captured = ReadIndexedEntities<IndexedName, string>(world, "before");
        world.Set(entity, new IndexedName { Value = "after" });
        Assert.Equal([entity], ReadIndexedEntities<IndexedName, string>(world, "after"));
        Assert.Equal([entity], captured);
    }

    [Fact]
    public void RepeatedIndexLookupsReturnMatchingEntities()
    {
        var world = new World();
        world.Create(new IndexedName { Value = "warm" });

        int observed = 0;
        for (int i = 0; i < 32; i++)
            observed += ReadIndexedEntities<IndexedName, string>(world, "warm").Length;

        for (int i = 0; i < 10_000; i++)
            observed += ReadIndexedEntities<IndexedName, string>(world, "warm").Length;

        Assert.Equal(10_032, observed);
    }

    [Fact]
    public async Task ConcurrentReadersAndSingleReplaceWriterObserveCompleteIndexMembers()
    {
        using var scheduler = new Scheduler(new JobConfiguration { WorkerCount = 0 });
        using var world = new World(execution: new WorldJobs(scheduler));
        var entity = world.Create(new IndexedName { Value = "alpha" });

        _ = ReadIndexedEntities<IndexedName, string>(world, "alpha").Length;
        _ = ReadIndexedEntities<IndexedName, string>(world, "beta").Length;

        const int replacements = 10_000;
        using var start = new ManualResetEventSlim(false);
        Task writer = Task.Run(() =>
        {
            start.Wait();
            for (int i = 0; i < replacements; i++)
            {
                world.Set(
                    entity,
                    new IndexedName { Value = (i & 1) == 0 ? "beta" : "alpha" });
            }
        });

        Task[] readers = Enumerable.Range(0, 3).Select(_ => Task.Run(() =>
        {
            start.Wait();
            for (int iteration = 0; iteration < replacements; iteration++)
            {
                VerifyBucket(ReadIndexedEntities<IndexedName, string>(world, "alpha"), entity);
                VerifyBucket(ReadIndexedEntities<IndexedName, string>(world, "beta"), entity);
            }
        })).ToArray();

        start.Set();
        await Task.WhenAll([writer, .. readers]);

        Assert.Equal(
            new[] { entity },
            ReadIndexedEntities<IndexedName, string>(world, "alpha").ToArray());
        Assert.Empty(ReadIndexedEntities<IndexedName, string>(world, "beta").ToArray());
    }

    [Fact]
    public void LazyIndexBackfills()
    {
        var world = new World();
        var entity1 = world.Create(new IndexedName { Value = "alpha" });
        var entity2 = world.Create(new IndexedName { Value = "beta" });

        Assert.Equal(new[] { entity1 }, ReadIndexedEntities<IndexedName, string>(world, "alpha").ToArray());
        Assert.Equal(new[] { entity2 }, ReadIndexedEntities<IndexedName, string>(world, "beta").ToArray());
    }

    [Fact]
    public void CreateIndexesEntity()
    {
        var world = new World();

        Assert.Empty(ReadIndexedEntities<IndexedName, string>(world, "late"));

        var entity = world.Create(new IndexedName { Value = "late" });

        Assert.Equal(new[] { entity }, ReadIndexedEntities<IndexedName, string>(world, "late").ToArray());
    }

    [Fact]
    public void SharedKeyBucket()
    {
        var world = new World();
        var entity1 = world.Create(new IndexedName { Value = "shared" });
        var entity2 = world.Create(new IndexedName { Value = "shared" });

        Assert.Equal(
            new[] { entity1, entity2 },
            ReadIndexedEntities<IndexedName, string>(world, "shared").ToArray());
    }

    [Fact]
    public void IndexTracksMutations()
    {
        var world = new World();
        var entity = world.Create();

        Assert.Empty(ReadIndexedEntities<IndexedName, string>(world, "start"));

        world.Add(entity, new IndexedName { Value = "start" });
        Assert.Equal(new[] { entity }, ReadIndexedEntities<IndexedName, string>(world, "start").ToArray());

        world.Set(entity, new IndexedName { Value = "next" });
        Assert.Empty(ReadIndexedEntities<IndexedName, string>(world, "start"));
        Assert.Equal(new[] { entity }, ReadIndexedEntities<IndexedName, string>(world, "next").ToArray());

        world.Remove<IndexedName>(entity);
        Assert.Empty(ReadIndexedEntities<IndexedName, string>(world, "next"));
    }

    [Fact]
    public void DestroyDropsIndex()
    {
        var world = new World();
        var entity = world.Create(new IndexedName { Value = "gone" });

        Assert.Equal(new[] { entity }, ReadIndexedEntities<IndexedName, string>(world, "gone").ToArray());

        world.Destroy(entity);

        Assert.Empty(ReadIndexedEntities<IndexedName, string>(world, "gone"));
    }

    [Fact]
    public void ReplaceRefreshesIndex()
    {
        var world = new World();
        var entity = world.Create(new IndexedName { Value = "old" });

        Assert.Equal(new[] { entity }, ReadIndexedEntities<IndexedName, string>(world, "old").ToArray());

        IndexedName component = world.Read<IndexedName>(entity);
        component.Value = "new";
        world.Set(entity, component);

        Assert.Empty(ReadIndexedEntities<IndexedName, string>(world, "old").ToArray());
        Assert.Equal(new[] { entity }, ReadIndexedEntities<IndexedName, string>(world, "new").ToArray());
    }

    [Fact]
    public void IndexedColumnWritesUseTheTypedIndexSetter()
    {
        using var world = new World();
        Entity entity = world.Create(new IndexedName { Value = "old" });
        ValueIndex<IndexedName, string> index = world.Index<IndexedName, string>();
        using (QueryScope scope = world.Query<IndexedName>().Open())
            foreach (QueryChunk chunk in scope)
                Assert.Throws<InvalidOperationException>(() => chunk.Write<IndexedName>(0).Value = "direct");
        world.Set(entity, new IndexedName { Value = "row" });
        Assert.Empty(ReadIndexedEntities<IndexedName, string>(world, "old"));
        Assert.Equal([entity], ReadIndexedEntities<IndexedName, string>(world, "row"));
    }

    [Fact]
    public void IndexedChunkReadsKeepTheirValuesBeforeFollowingTypedUpdates()
    {
        using var world = new World();
        Entity entity = world.Create(new IndexedName { Value = "old" });
        Assert.Equal([entity], ReadIndexedEntities<IndexedName, string>(world, "old"));
        using (QueryScope scope = world.Query<IndexedName>().OpenRead())
            foreach (QueryChunk chunk in scope) Assert.Equal("old", chunk.Read<IndexedName>()[0].Value);
        world.Set(entity, new IndexedName { Value = "chunk" });
        Assert.Empty(ReadIndexedEntities<IndexedName, string>(world, "old"));
        Assert.Equal([entity], ReadIndexedEntities<IndexedName, string>(world, "chunk"));
    }

    [Fact]
    public void TypedIndexUpdatesPreserveOtherComponentsFromTheSameEntity()
    {
        using var world = new World();
        Entity entity = world.Create(new IndexedName { Value = "old" }, new Position { X = 1, Y = 2 });
        ValueIndex<IndexedName, string> index = world.Index<IndexedName, string>();
        world.Set(entity, new IndexedName { Value = "pair" });
        Assert.Empty(ReadIndexedEntities<IndexedName, string>(world, "old"));
        Assert.Equal([entity], ReadIndexedEntities<IndexedName, string>(world, "pair"));
        Assert.Equal(1, world.Read<Position>(entity).X);
        Assert.Equal(2, world.Read<Position>(entity).Y);
    }

    private static void VerifyBucket(ReadOnlySpan<Entity> bucket, Entity expected)
    {
        if (bucket.Length > 1)
            throw new InvalidOperationException("An index bucket contained a duplicate entity.");
        if (bucket.Length == 1 && bucket[0] != expected)
            throw new InvalidOperationException("An index bucket contained an unexpected entity.");
    }
}
