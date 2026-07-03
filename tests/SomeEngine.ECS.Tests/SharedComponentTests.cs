using SomeEngine.ECS.Components;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Registry;
using Xunit;

namespace SomeEngine.ECS.Tests;

// 测试用 shared component 类型
public struct SceneId : SomeEngine.ECS.Components.ISharedComponent, IEquatable<SceneId>
{
    public int Value;

    public bool Equals(SceneId other) => Value == other.Value;
    public override bool Equals(object? obj) => obj is SceneId s && Equals(s);
    public override int GetHashCode() => Value;
}

public struct RenderGroup : SomeEngine.ECS.Components.ISharedComponent, IEquatable<RenderGroup>
{
    public int GroupId;
    public string Material;

    public bool Equals(RenderGroup other) => GroupId == other.GroupId && Material == other.Material;
    public override bool Equals(object? obj) => obj is RenderGroup r && Equals(r);
    public override int GetHashCode() => HashCode.Combine(GroupId, Material);
}

public class SharedComponentTests
{
    [Fact]
    public void SharedComponent_Kind_IsSharedComponent()
    {
        Assert.Equal(StoragePath.Shared, ComponentMetadata<SceneId>.Storage);
    }

    [Fact]
    public void SetShared_AddsToArchetype()
    {
        var world = new World();
        var entity = world.CreateEntity();

        world.AddShared(entity, new SceneId { Value = 1 });

        Assert.True(world.HasShared<SceneId>(entity));
    }

    [Fact]
    public void AddShared_ExistingSharedComponent_Throws()
    {
        var world = new World();
        var entity = world.CreateEntity();

        world.AddShared(entity, new SceneId { Value = 1 });

        Assert.Throws<InvalidOperationException>(() =>
            world.AddShared(entity, new SceneId { Value = 2 }));
        Assert.Equal(1, world.GetShared<SceneId>(entity).Value);
    }

    [Fact]
    public void GetShared_ReturnsCorrectValue()
    {
        var world = new World();
        var entity = world.CreateEntity();

        world.AddShared(entity, new SceneId { Value = 42 });

        var scene = world.GetShared<SceneId>(entity);
        Assert.Equal(42, scene.Value);
    }

    [Fact]
    public void SetShared_UpdatesValue()
    {
        var world = new World();
        var entity = world.CreateEntity();

        world.AddShared(entity, new SceneId { Value = 1 });
        world.ReplaceShared(entity, new SceneId { Value = 2 });

        Assert.Equal(2, world.GetShared<SceneId>(entity).Value);
    }

    [Fact]
    public void SharedComponent_DoesNotOccupyColumn()
    {
        var world = new World();
        var entity = world.CreateEntity<Position>(new Position { X = 1, Y = 2 });

        world.AddShared(entity, new SceneId { Value = 10 });

        // Position 应该仍然可读
        Assert.Equal(1, world.Read<Position>(entity).X);
        Assert.Equal(2, world.Read<Position>(entity).Y);
    }

    [Fact]
    public void SharedComponent_PreservesComponentsOnMigration()
    {
        var world = new World();
        var entity = world.CreateEntity<Position>(new Position { X = 5, Y = 10 });

        world.AddShared(entity, new SceneId { Value = 1 });
        world.Add(entity, new Health { Value = 100 });

        Assert.Equal(5, world.Read<Position>(entity).X);
        Assert.Equal(100, world.Read<Health>(entity).Value);
        Assert.Equal(1, world.GetShared<SceneId>(entity).Value);
    }

    [Fact]
    public void SharedComponent_MultipleEntities_SameValue()
    {
        var world = new World();
        var e1 = world.CreateEntity();
        var e2 = world.CreateEntity();

        world.AddShared(e1, new SceneId { Value = 1 });
        world.AddShared(e2, new SceneId { Value = 1 });

        Assert.Equal(1, world.GetShared<SceneId>(e1).Value);
        Assert.Equal(1, world.GetShared<SceneId>(e2).Value);
    }

    [Fact]
    public void SharedComponent_MultipleEntities_DifferentValues()
    {
        var world = new World();
        var e1 = world.CreateEntity();
        var e2 = world.CreateEntity();

        world.AddShared(e1, new SceneId { Value = 1 });
        world.AddShared(e2, new SceneId { Value = 2 });

        Assert.Equal(1, world.GetShared<SceneId>(e1).Value);
        Assert.Equal(2, world.GetShared<SceneId>(e2).Value);
    }

    [Fact]
    public void GetShared_ThrowsIfNotPresent()
    {
        var world = new World();
        var entity = world.CreateEntity();

        Assert.Throws<InvalidOperationException>(() =>
            world.GetShared<SceneId>(entity));
    }

    [Fact]
    public void SharedComponent_DestroyEntity_CleansUpIndex()
    {
        var world = new World();
        var entity = world.CreateEntity();
        world.AddShared(entity, new SceneId { Value = 42 });

        world.DestroyEntity(entity);

        // 创建新 entity（可能复用同一 index）
        var newEntity = world.CreateEntity();

        // 新 entity 不应携带旧的 shared value
        Assert.False(world.HasShared<SceneId>(newEntity));
    }

    [Fact]
    public void HasShared_DeadEntity_ReturnsFalse()
    {
        var world = new World();
        var entity = world.CreateEntity();
        world.AddShared(entity, new SceneId { Value = 42 });

        world.DestroyEntity(entity);

        Assert.False(world.HasShared<SceneId>(entity));
    }

    [Fact]
    public void SharedComponent_DestroyEntity_MultipleShared_CleansUpAll()
    {
        var world = new World();
        var entity = world.CreateEntity();
        world.AddShared(entity, new SceneId { Value = 1 });
        world.AddShared(entity, new RenderGroup { GroupId = 5, Material = "metal" });

        world.DestroyEntity(entity);

        var newEntity = world.CreateEntity();
        Assert.False(world.HasShared<SceneId>(newEntity));
        Assert.False(world.HasShared<RenderGroup>(newEntity));
    }
    [Fact]
    public void SharedComponent_SameValue_SameChunk()
    {
        var world = new World();
        var e1 = world.CreateEntity<Position>(new Position { X = 1 });
        var e2 = world.CreateEntity<Position>(new Position { X = 2 });

        world.AddShared(e1, new SceneId { Value = 1 });
        world.AddShared(e2, new SceneId { Value = 1 });

        // 同 shared value → 应在同一 chunk
        var query = world.CreateQuery().With<Position>().With<SceneId>().Build();
        var archetypes = query.Archetypes;
        Assert.Single(archetypes);
        // 同 chunk 意味着只有 1 个 chunk
        Assert.Single(archetypes[0].Chunks);
    }

    [Fact]
    public void SharedComponent_DifferentValues_DifferentChunks()
    {
        var world = new World();
        var e1 = world.CreateEntity<Position>(new Position { X = 1 });
        var e2 = world.CreateEntity<Position>(new Position { X = 2 });

        world.AddShared(e1, new SceneId { Value = 1 });
        world.AddShared(e2, new SceneId { Value = 2 });

        // 不同 shared value → 不同 chunk
        var query = world.CreateQuery().With<Position>().With<SceneId>().Build();
        var archetypes = query.Archetypes;
        Assert.Single(archetypes);
        Assert.Equal(2, archetypes[0].Chunks.Count);
    }

    [Fact]
    public void SharedComponent_ChangeValue_MigratesToCorrectChunk()
    {
        var world = new World();
        var e1 = world.CreateEntity<Position>(new Position { X = 1 });
        var e2 = world.CreateEntity<Position>(new Position { X = 2 });

        world.AddShared(e1, new SceneId { Value = 1 });
        world.AddShared(e2, new SceneId { Value = 2 });

        // 改 e2 到 value=1，应迁移到 e1 的 chunk
        world.ReplaceShared(e2, new SceneId { Value = 1 });

        var query = world.CreateQuery().With<Position>().With<SceneId>().Build();
        var archetypes = query.Archetypes;
        Assert.Single(archetypes);
        // 空 chunk 应被回收，只剩 1 个
        Assert.Single(archetypes[0].Chunks);
        Assert.Equal(2, archetypes[0].Chunks[0].Count);

        // 数据应保留
        Assert.Equal(1, world.Read<Position>(e1).X);
        Assert.Equal(2, world.Read<Position>(e2).X);
        Assert.Equal(1, world.GetShared<SceneId>(e1).Value);
        Assert.Equal(1, world.GetShared<SceneId>(e2).Value);
    }

    [Fact]
    public void QueryWithSharedFilter_OnlyMatchingChunksIterated()
    {
        var world = new World();
        var e1 = world.CreateEntity<Position>(new Position { X = 1 });
        var e2 = world.CreateEntity<Position>(new Position { X = 2 });
        var e3 = world.CreateEntity<Position>(new Position { X = 3 });

        world.AddShared(e1, new SceneId { Value = 10 });
        world.AddShared(e2, new SceneId { Value = 20 });
        world.AddShared(e3, new SceneId { Value = 10 });

        // 查询 SceneId.Value == 10 的 entity
        var query = world.Query(
            world.QueryDefinition()
                .Read<Position>()
                .Shared<SceneId>());

        var results = new List<float>();
        foreach (var row in world.RunQuery(query).RowsWithShared(new SceneId { Value = 10 }))
            results.Add(row.Read<Position>().X);

        Assert.Equal(2, results.Count);
        Assert.Contains(1f, results);
        Assert.Contains(3f, results);
        Assert.DoesNotContain(2f, results);
    }

    [Fact]
    public void QueryWithSharedFilter_NoMatchingChunks_NoIteration()
    {
        var world = new World();
        var e1 = world.CreateEntity<Position>(new Position { X = 1 });
        world.AddShared(e1, new SceneId { Value = 42 });

        var query = world.Query(
            world.QueryDefinition()
                .Read<Position>()
                .Shared<SceneId>());

        int count = 0;
        foreach (var _ in world.RunQuery(query).RowsWithShared(new SceneId { Value = 999 }))
            count++;

        Assert.Equal(0, count);
    }

    [Fact]
    [Trait("Category", "Performance")]
    public void QueryRowsWithSharedValue_Warmed_DoesNotAllocate()
    {
        var world = new World();
        var e1 = world.CreateEntity<Position>(new Position { X = 1 });
        var e2 = world.CreateEntity<Position>(new Position { X = 2 });
        world.AddShared(e1, new SceneId { Value = 10 });
        world.AddShared(e2, new SceneId { Value = 20 });

        var query = world.Query(
            world.QueryDefinition()
                .Read<Position>()
                .Shared<SceneId>());

        int warmCount = 0;
        foreach (var _ in world.RunQuery(query).RowsWithShared(new SceneId { Value = 10 }))
            warmCount++;
        Assert.Equal(1, warmCount);

        long before = GC.GetAllocatedBytesForCurrentThread();
        int count = 0;
        foreach (var _ in world.RunQuery(query).RowsWithShared(new SceneId { Value = 10 }))
            count++;
        long after = GC.GetAllocatedBytesForCurrentThread();

        Assert.Equal(1, count);
        Assert.Equal(0, after - before);
    }

    [Fact]
    [Trait("Category", "Performance")]
    public void QueryRowsWithSharedSpan_Warmed_DoesNotAllocate()
    {
        var world = new World();
        var e1 = world.CreateEntity<Position>(new Position { X = 1 });
        var e2 = world.CreateEntity<Position>(new Position { X = 2 });
        world.AddShared(e1, new SceneId { Value = 10 });
        world.AddShared(e2, new SceneId { Value = 20 });

        var query = world.Query(
            world.QueryDefinition()
                .Read<Position>()
                .Shared<SceneId>());

        Assert.True(world.Shared.TryIndex(
            ComponentMetadata<SceneId>.Id,
            new SceneId { Value = 10 },
            out int sharedIndex));

        Span<QuerySharedFilter> filters = stackalloc QuerySharedFilter[1];
        filters[0] = new QuerySharedFilter(ComponentMetadata<SceneId>.Id, sharedIndex);

        int warmCount = 0;
        foreach (var _ in world.RunQuery(query).RowsWithShared(filters))
            warmCount++;
        Assert.Equal(1, warmCount);

        long before = GC.GetAllocatedBytesForCurrentThread();
        int count = 0;
        foreach (var _ in world.RunQuery(query).RowsWithShared(filters))
            count++;
        long after = GC.GetAllocatedBytesForCurrentThread();

        Assert.Equal(1, count);
        Assert.Equal(0, after - before);
    }
}
