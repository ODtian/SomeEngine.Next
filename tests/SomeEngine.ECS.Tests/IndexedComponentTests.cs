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
    public void RefWriteRefreshesIndex()
    {
        var world = new World();
        var entity = world.CreateEntity(new IndexedName { Value = "old" });

        Assert.Equal(new[] { entity }, world.GetByIndex<IndexedName, string>("old").ToArray());

        ref var component = ref world.Get<IndexedName>(entity);
        component.Value = "new";

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
        foreach (var row in world.RunQuery(query).Rows)
            row.ReadWrite<IndexedName>().Value = "row";

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
        foreach (var chunk in world.RunQuery(query).Chunks)
            chunk.ReadWrite<IndexedName>()[0].Value = "chunk";

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
        foreach (var chunk in world.RunReadWrite<IndexedName, Position>(query))
            chunk.Write[0].Value = "pair";

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
}
