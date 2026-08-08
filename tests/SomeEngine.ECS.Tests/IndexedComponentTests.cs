using SomeEngine.ECS;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Hooks;
using Xunit;

namespace SomeEngine.ECS.Tests;

public struct IndexedName : SomeEngine.ECS.Components.IIndexedComponent<string>
{
    public string Value;

    public string GetKey() => Value;
}

public class IndexedComponentTests
{
    [Fact]
    public void CapturedBucketGeneration_SurvivesAddReplaceAndRemove()
    {
        var world = new World();
        var first = world.CreateEntity(new IndexedName { Value = "shared" });

        ReadOnlySpan<Entity> beforeAdd =
            world.GetByIndex<IndexedName, string>("shared");
        var second = world.CreateEntity(new IndexedName { Value = "shared" });

        Assert.Equal(new[] { first }, beforeAdd.ToArray());
        Assert.Equal(
            new[] { first, second },
            world.GetByIndex<IndexedName, string>("shared").ToArray());

        ReadOnlySpan<Entity> beforeReplace =
            world.GetByIndex<IndexedName, string>("shared");
        world.Replace(first, new IndexedName { Value = "moved" });

        Assert.Equal(new[] { first, second }, beforeReplace.ToArray());
        Assert.Equal(
            new[] { second },
            world.GetByIndex<IndexedName, string>("shared").ToArray());
        Assert.Equal(
            new[] { first },
            world.GetByIndex<IndexedName, string>("moved").ToArray());

        ReadOnlySpan<Entity> beforeRemove =
            world.GetByIndex<IndexedName, string>("shared");
        world.Remove<IndexedName>(second);

        Assert.Equal(new[] { second }, beforeRemove.ToArray());
        Assert.Empty(world.GetByIndex<IndexedName, string>("shared").ToArray());
    }

    [Fact]
    public void CapturedBucketGeneration_SurvivesDirtyRebuild()
    {
        var world = new World();
        var entity = world.CreateEntity(new IndexedName { Value = "before" });
        ReadOnlySpan<Entity> captured =
            world.GetByIndex<IndexedName, string>("before");

        var query = world.Query(world.QueryDefinition().ReadWrite<IndexedName>());
        world.ExecuteQuery(query, cursor =>
        {
            foreach (var row in cursor.Rows)
                row.ReadWrite<IndexedName>().Value = "after";
        });

        Assert.Empty(world.GetByIndex<IndexedName, string>("before").ToArray());
        Assert.Equal(
            new[] { entity },
            world.GetByIndex<IndexedName, string>("after").ToArray());
        Assert.Equal(new[] { entity }, captured.ToArray());
    }

    [Fact]
    public void WarmedUnchangedBucket_ReadsAllocateZeroBytes()
    {
        var world = new World();
        world.CreateEntity(new IndexedName { Value = "warm" });

        int observed = 0;
        for (int i = 0; i < 32; i++)
            observed += world.GetByIndex<IndexedName, string>("warm").Length;

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 10_000; i++)
            observed += world.GetByIndex<IndexedName, string>("warm").Length;
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(10_032, observed);
        Assert.Equal(0, allocated);
    }

    [Fact]
    public async Task ConcurrentReadersAndSingleReplaceWriter_ObserveImmutableBuckets()
    {
        var world = new World();
        var entity = world.CreateEntity(new IndexedName { Value = "alpha" });

        // Materialize both keys before concurrency so the test exercises only
        // COW publication, not live-chunk backfill during an active writer.
        _ = world.GetByIndex<IndexedName, string>("alpha").Length;
        _ = world.GetByIndex<IndexedName, string>("beta").Length;

        const int replacements = 10_000;
        using var start = new ManualResetEventSlim(false);
        Task writer = Task.Run(() =>
        {
            start.Wait();
            for (int i = 0; i < replacements; i++)
            {
                world.Replace(
                    entity,
                    new IndexedName { Value = (i & 1) == 0 ? "beta" : "alpha" });
            }
        });

        Task[] readers = Enumerable.Range(0, 3).Select(_ => Task.Run(() =>
        {
            start.Wait();
            for (int iteration = 0; iteration < replacements; iteration++)
            {
                VerifyBucket(world.GetByIndex<IndexedName, string>("alpha"), entity);
                VerifyBucket(world.GetByIndex<IndexedName, string>("beta"), entity);
            }
        })).ToArray();

        start.Set();
        await Task.WhenAll([writer, .. readers]);

        Assert.Equal(
            new[] { entity },
            world.GetByIndex<IndexedName, string>("alpha").ToArray());
        Assert.Empty(world.GetByIndex<IndexedName, string>("beta").ToArray());
    }

    [Fact]
    public void LazyIndexBackfills()
    {
        var world = new World();
        var entity1 = world.CreateEntity(new IndexedName { Value = "alpha" });
        var entity2 = world.CreateEntity(new IndexedName { Value = "beta" });

        Assert.Equal(new[] { entity1 }, world.GetByIndex<IndexedName, string>("alpha").ToArray());
        Assert.Equal(new[] { entity2 }, world.GetByIndex<IndexedName, string>("beta").ToArray());
    }

    [Fact]
    public void CreateIndexesEntity()
    {
        var world = new World();

        Assert.Equal(0, world.GetByIndex<IndexedName, string>("late").Length);

        var entity = world.CreateEntity(new IndexedName { Value = "late" });

        Assert.Equal(new[] { entity }, world.GetByIndex<IndexedName, string>("late").ToArray());
    }

    [Fact]
    public void SharedKeyBucket()
    {
        var world = new World();
        var entity1 = world.CreateEntity(new IndexedName { Value = "shared" });
        var entity2 = world.CreateEntity(new IndexedName { Value = "shared" });

        Assert.Equal(
            new[] { entity1, entity2 },
            world.GetByIndex<IndexedName, string>("shared").ToArray());
    }

    [Fact]
    public void IndexTracksMutations()
    {
        var world = new World();
        var entity = world.CreateEntity();

        Assert.Equal(0, world.GetByIndex<IndexedName, string>("start").Length);

        world.Add(entity, new IndexedName { Value = "start" });
        Assert.Equal(new[] { entity }, world.GetByIndex<IndexedName, string>("start").ToArray());

        world.Replace(entity, new IndexedName { Value = "next" });
        Assert.Equal(0, world.GetByIndex<IndexedName, string>("start").Length);
        Assert.Equal(new[] { entity }, world.GetByIndex<IndexedName, string>("next").ToArray());

        world.Remove<IndexedName>(entity);
        Assert.Equal(0, world.GetByIndex<IndexedName, string>("next").Length);
    }

    [Fact]
    public void DestroyDropsIndex()
    {
        var world = new World();
        var entity = world.CreateEntity(new IndexedName { Value = "gone" });

        Assert.Equal(new[] { entity }, world.GetByIndex<IndexedName, string>("gone").ToArray());

        world.DestroyEntity(entity);

        Assert.Equal(0, world.GetByIndex<IndexedName, string>("gone").Length);
    }

    [Fact]
    public void ReplaceRefreshesIndex()
    {
        var world = new World();
        var entity = world.CreateEntity(new IndexedName { Value = "old" });

        Assert.Equal(new[] { entity }, world.GetByIndex<IndexedName, string>("old").ToArray());

        IndexedName component = world.Read<IndexedName>(entity);
        component.Value = "new";
        world.Replace(entity, component);

        Assert.Empty(world.GetByIndex<IndexedName, string>("old").ToArray());
        Assert.Equal(new[] { entity }, world.GetByIndex<IndexedName, string>("new").ToArray());
    }

    [Fact]
    public void QueryRowRefWriteRefreshesIndex()
    {
        var world = new World();
        var entity = world.CreateEntity(new IndexedName { Value = "old" });
        Assert.Equal(new[] { entity }, world.GetByIndex<IndexedName, string>("old").ToArray());

        var query = world.Query(world.QueryDefinition().ReadWrite<IndexedName>());
        world.ExecuteQuery(query, cursor =>
        {
            foreach (var row in cursor.Rows)
                row.ReadWrite<IndexedName>().Value = "row";
        });

        Assert.Empty(world.GetByIndex<IndexedName, string>("old").ToArray());
        Assert.Equal(new[] { entity }, world.GetByIndex<IndexedName, string>("row").ToArray());
    }

    [Fact]
    public void QueryChunkSpanWriteRefreshesIndex()
    {
        var world = new World();
        var entity = world.CreateEntity(new IndexedName { Value = "old" });
        Assert.Equal(new[] { entity }, world.GetByIndex<IndexedName, string>("old").ToArray());

        var query = world.Query(world.QueryDefinition().ReadWrite<IndexedName>());
        world.ExecuteQuery(query, cursor =>
        {
            foreach (var chunk in cursor.Chunks)
                chunk.ReadWrite<IndexedName>()[0].Value = "chunk";
        });

        Assert.Empty(world.GetByIndex<IndexedName, string>("old").ToArray());
        Assert.Equal(new[] { entity }, world.GetByIndex<IndexedName, string>("chunk").ToArray());
    }

    [Fact]
    public void QueryPairSpanWriteRefreshesIndex()
    {
        var world = new World();
        var entity = world.CreateEntity(new IndexedName { Value = "old" });
        world.Add(entity, new Position { X = 1, Y = 2 });
        Assert.Equal(new[] { entity }, world.GetByIndex<IndexedName, string>("old").ToArray());

        var query = world.Query(
            world.QueryDefinition()
                .ReadWrite<IndexedName>()
                .Read<Position>());
        world.ExecuteReadWrite<IndexedName, Position>(query, chunks =>
        {
            foreach (var chunk in chunks)
                chunk.Write[0].Value = "pair";
        });

        Assert.Empty(world.GetByIndex<IndexedName, string>("old").ToArray());
        Assert.Equal(new[] { entity }, world.GetByIndex<IndexedName, string>("pair").ToArray());
    }

    [Fact]
    public void HookSeesIndex()
    {
        var world = new World();
        var entity = world.CreateEntity();
        bool indexed = false;

        Assert.Equal(0, world.GetByIndex<IndexedName, string>("hook").Length);
        world.Hooks<IndexedName>().OnInsert((DeferredWorld worldArg, Entity entityArg, in IndexedName component) =>
        {
            indexed = worldArg.GetByIndex<IndexedName, string>(component.Value).ToArray().Contains(entityArg);
        });

        world.Add(entity, new IndexedName { Value = "hook" });

        Assert.True(indexed);
    }

    private static void VerifyBucket(ReadOnlySpan<Entity> bucket, Entity expected)
    {
        if (bucket.Length > 1)
            throw new InvalidOperationException("An index bucket contained a duplicate entity.");
        if (bucket.Length == 1 && bucket[0] != expected)
            throw new InvalidOperationException("An index bucket contained an unexpected entity.");
    }
}
