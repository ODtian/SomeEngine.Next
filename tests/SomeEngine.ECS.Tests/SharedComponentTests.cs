using SomeEngine.ECS.Archetypes;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
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
        var archetypes = world.AllArchetypes.ToArray().Where(
            static candidate =>
                candidate.HasComponent(ComponentMetadata<Position>.Id) &&
                candidate.HasComponent(ComponentMetadata<SceneId>.Id)).ToArray();
        Assert.Single(archetypes);
        // 同 chunk 意味着只有 1 个 chunk
        Assert.Equal(1, archetypes[0].Chunks.Length);
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
        var archetypes = world.AllArchetypes.ToArray().Where(
            static candidate =>
                candidate.HasComponent(ComponentMetadata<Position>.Id) &&
                candidate.HasComponent(ComponentMetadata<SceneId>.Id)).ToArray();
        Assert.Single(archetypes);
        Assert.Equal(2, archetypes[0].Chunks.Length);
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

        var archetypes = world.AllArchetypes.ToArray().Where(
            static candidate =>
                candidate.HasComponent(ComponentMetadata<Position>.Id) &&
                candidate.HasComponent(ComponentMetadata<SceneId>.Id)).ToArray();
        Assert.Single(archetypes);
        // 空 chunk 应被回收，只剩 1 个
        Assert.Equal(1, archetypes[0].Chunks.Length);
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
        world.ExecuteQuery(query, cursor =>
        {
            foreach (var row in cursor.RowsWithShared(new SceneId { Value = 10 }))
                results.Add(row.Read<Position>().X);
        });

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
        world.ExecuteQuery(query, cursor =>
        {
            foreach (var _ in cursor.RowsWithShared(new SceneId { Value = 999 }))
                count++;
        });

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

        int warmCount = CountSceneRows(world, query);
        Assert.Equal(1, warmCount);

        long before = GC.GetAllocatedBytesForCurrentThread();
        int count = CountSceneRows(world, query);
        long after = GC.GetAllocatedBytesForCurrentThread();

        Assert.Equal(1, count);
        Assert.Equal(0, after - before);
    }

    [Fact]
    [Trait("Category", "Performance")]
    public void QueryRowsWithWorldBoundSharedFilter_Warmed_DoesNotAllocate()
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

        var filter = new QuerySharedFilter(
            world,
            ComponentMetadata<SceneId>.Id,
            sharedIndex);
        int warmCount = CountSceneRows(world, query, filter);
        Assert.Equal(1, warmCount);

        long before = GC.GetAllocatedBytesForCurrentThread();
        int count = CountSceneRows(world, query, filter);
        long after = GC.GetAllocatedBytesForCurrentThread();

        Assert.Equal(1, count);
        Assert.Equal(0, after - before);
    }

    [Fact]
    public void PrecomputedSharedFilter_RejectsAnotherWorld()
    {
        var owner = new World();
        Entity ownerEntity = owner.CreateEntity();
        owner.AddShared(ownerEntity, new SceneId { Value = 10 });
        Assert.True(owner.Shared.TryIndex(
            ComponentMetadata<SceneId>.Id,
            new SceneId { Value = 10 },
            out int sharedIndex));
        var filter = new QuerySharedFilter(
            owner,
            ComponentMetadata<SceneId>.Id,
            sharedIndex);

        var other = new World();
        Entity otherEntity = other.CreateEntity();
        other.AddShared(otherEntity, new SceneId { Value = 10 });
        QueryHandle query = other.Query(
            other.QueryDefinition().Shared<SceneId>());

        Assert.Throws<InvalidOperationException>(() =>
            other.ExecuteQuery(query, cursor =>
            {
                foreach (var _ in cursor.RowsWithShared(filter))
                {
                }
            }));
    }

    [Fact]
    public void SharedBucket_IndexesOnlyOpenChunksAndReusesAReopenedChunk()
    {
        var world = new World();

        Entity firstEntity = world.CreateEntity<Position>(new Position { X = 0 });
        world.AddShared(firstEntity, new SceneId { Value = 7 });

        Archetype archetype = Assert.Single(
            world.AllArchetypes.ToArray(),
            static candidate =>
                candidate.HasComponent(ComponentMetadata<Position>.Id) &&
                candidate.HasComponent(ComponentMetadata<SceneId>.Id));
        Assert.Equal(1, archetype.Chunks.Length);
        Chunk firstChunk = archetype.Chunks[0];

        for (int index = 1; index < firstChunk.Capacity; index++)
        {
            Entity entity = world.CreateEntity<Position>(new Position { X = index });
            world.AddShared(entity, new SceneId { Value = 7 });
        }

        SharedChunkBucket bucket = archetype.GetOnlySharedChunkBucket();
        Assert.Equal(1, bucket.ChunkCount);
        Assert.Equal(0, bucket.OpenChunkCount);

        Entity spill = world.CreateEntity<Position>(new Position { X = -1 });
        world.AddShared(spill, new SceneId { Value = 7 });
        Assert.Equal(1, bucket.OpenChunkCount);
        Chunk spillChunk = bucket.OpenChunkAt(0);
        Assert.NotSame(firstChunk, spillChunk);
        Assert.Equal(2, bucket.ChunkCount);

        world.DestroyEntity(firstEntity);
        Assert.Equal(2, bucket.OpenChunkCount);
        Assert.Contains(firstChunk, bucket.OpenChunkSpan.ToArray());

        int reopenedCount = firstChunk.Count;
        Entity replacement = world.CreateEntity<Position>(new Position { X = -2 });
        world.AddShared(replacement, new SceneId { Value = 7 });

        Assert.Equal(reopenedCount + 1, firstChunk.Count);
        Assert.True(firstChunk.IsFull);
        Assert.DoesNotContain(firstChunk, bucket.OpenChunkSpan.ToArray());
        Assert.Equal(1, bucket.OpenChunkCount);
    }

    private static int CountSceneRows(World world, QueryHandle query)
    {
        int count = 0;
        world.ExecuteQuery(query, ref count, static (QueryCursor cursor, ref int state) =>
        {
            foreach (var _ in cursor.RowsWithShared(new SceneId { Value = 10 }))
                state++;
        });
        return count;
    }

    private static int CountSceneRows(
        World world,
        QueryHandle query,
        QuerySharedFilter filter)
    {
        var state = (Filter: filter, Count: 0);
        world.ExecuteQuery(
            query,
            ref state,
            static (QueryCursor cursor, ref (QuerySharedFilter Filter, int Count) state) =>
            {
                foreach (var _ in cursor.RowsWithShared(state.Filter))
                    state.Count++;
            });
        return state.Count;
    }
}
