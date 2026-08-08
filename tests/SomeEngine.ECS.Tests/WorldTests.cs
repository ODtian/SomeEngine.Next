using SomeEngine.ECS;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Registry;
using Xunit;

namespace SomeEngine.ECS.Tests;

public struct SortLow : SomeEngine.ECS.IComponent
{
    public int Value;
}

public struct SortHigh : SomeEngine.ECS.IComponent
{
    public int Value;
}

public class WorldTests
{
    // ════════════════════════════════════════════════
    // 实体生命周期
    // ════════════════════════════════════════════════

    [Fact]
    public void CreateEntity_ReturnsAlive()
    {
        var world = new World();
        var e = world.CreateEntity();
        Assert.True(world.IsAlive(e));
    }

    [Fact]
    public void CreateEntity_WithOneComponent_HasAndRead()
    {
        var world = new World();
        var pos = new Position { X = 1.5f, Y = 2.5f };
        var e = world.CreateEntity(pos);
        Assert.True(world.Has<Position>(e));
        Assert.Equal(1.5f, world.Read<Position>(e).X);
        Assert.Equal(2.5f, world.Read<Position>(e).Y);
    }

    [Fact]
    public void Spawn_WithTwoComponents_BothAccessible()
    {
        var world = new World();
        var pos = new Position { X = 10, Y = 20 };
        var vel = new Velocity { X = 3, Y = 4 };
        var e = world.Spawn(new PhysicsBundle { Position = pos, Velocity = vel });
        Assert.True(world.Has<Position>(e));
        Assert.True(world.Has<Velocity>(e));
        Assert.Equal(10f, world.Read<Position>(e).X);
        Assert.Equal(3f, world.Read<Velocity>(e).X);
    }

    [Fact]
    public void Spawn_SortBundle_UsesSameArchetype()
    {
        _ = ComponentMetadata<SortLow>.Id;
        _ = ComponentMetadata<SortHigh>.Id;
        Assert.True(ComponentMetadata<SortLow>.Id < ComponentMetadata<SortHigh>.Id);

        var world = new World();
        world.Spawn(new SortBundle { Low = new SortLow { Value = 1 }, High = new SortHigh { Value = 2 } });
        world.Spawn(new SortBundle { Low = new SortLow { Value = 4 }, High = new SortHigh { Value = 3 } });

        var archetype = Assert.Single(
            world.AllArchetypes.ToArray(),
            static candidate =>
                candidate.HasComponent(ComponentMetadata<SortLow>.Id) &&
                candidate.HasComponent(ComponentMetadata<SortHigh>.Id));

        int entityCount = 0;
        foreach (var chunk in archetype.Chunks)
            entityCount += chunk.Count;

        Assert.Equal(2, entityCount);
    }

    [Fact]
    public void Spawn_WithThreeComponents()
    {
        var world = new World();
        var e = world.Spawn(new MotionHealthBundle
        {
            Position = new Position { X = 1, Y = 2 },
            Velocity = new Velocity { X = 3, Y = 4 },
            Health = new Health { Value = 100 },
        });
        Assert.True(world.Has<Position>(e));
        Assert.True(world.Has<Velocity>(e));
        Assert.True(world.Has<Health>(e));
        Assert.Equal(100, world.Read<Health>(e).Value);
    }

    [Fact]
    public void DestroyEntity_MakesNotAlive()
    {
        var world = new World();
        var e = world.CreateEntity();
        world.DestroyEntity(e);
        Assert.False(world.IsAlive(e));
    }

    [Fact]
    public void DestroyEntity_StaleEntity_Throws()
    {
        var world = new World();
        var e = world.CreateEntity();
        world.DestroyEntity(e);
        Assert.Throws<InvalidOperationException>(() => world.DestroyEntity(e));
    }

    [Fact]
    public void EntityCount_TracksCreateAndDestroy()
    {
        var world = new World();
        Assert.Equal(0, world.EntityCount);
        var e1 = world.CreateEntity();
        var e2 = world.CreateEntity();
        Assert.Equal(2, world.EntityCount);
        world.DestroyEntity(e1);
        Assert.Equal(1, world.EntityCount);
    }

    [Fact]
    public void EntityCount_TracksBulkCreateAndDestroy()
    {
        var world = new World();
        var entities = new List<Entity>();

        for (int i = 0; i < 100; i++)
            entities.Add(world.CreateEntity());

        Assert.Equal(100, world.EntityCount);

        for (int i = 0; i < 30; i++)
            world.DestroyEntity(entities[i]);

        Assert.Equal(70, world.EntityCount);
    }

    // ════════════════════════════════════════════════
    // Add / Remove
    // ════════════════════════════════════════════════

    [Fact]
    public void Add_Component_HasAndGet()
    {
        var world = new World();
        var e = world.CreateEntity();
        world.Add(e, new Position { X = 5, Y = 10 });
        Assert.True(world.Has<Position>(e));
        Assert.Equal(5f, world.Read<Position>(e).X);
    }

    [Fact]
    public void Add_Duplicate_IsIdempotent()
    {
        var world = new World();
        var e = world.CreateEntity(new Position { X = 1, Y = 2 });
        Assert.Throws<InvalidOperationException>(() =>
            world.Add(e, new Position { X = 3, Y = 4 }));
        Assert.Equal(1f, world.Read<Position>(e).X);
        Assert.Equal(2f, world.Read<Position>(e).Y);
    }

    [Fact]
    public void Remove_Component_HasBecomesFalse()
    {
        var world = new World();
        var e = world.CreateEntity(new Position { X = 1, Y = 2 });
        world.Remove<Position>(e);
        Assert.False(world.Has<Position>(e));
    }

    [Fact]
    public void Remove_NonExistent_Throws()
    {
        var world = new World();
        var e = world.CreateEntity();
        Assert.Throws<InvalidOperationException>(() => world.Remove<Position>(e));
    }

    [Fact]
    public void Add_Remove_Add_Cycle()
    {
        var world = new World();
        var e = world.CreateEntity();
        world.Add(e, new Position { X = 1, Y = 2 });
        world.Remove<Position>(e);
        world.Add(e, new Position { X = 3, Y = 4 });
        Assert.Equal(3f, world.Read<Position>(e).X);
    }

    [Fact]
    public void AddTag_HasTag()
    {
        var world = new World();
        var e = world.CreateEntity();
        world.AddTag<PlayerTag>(e);
        Assert.True(world.Has<PlayerTag>(e));
    }

    [Fact]
    public void RemoveTag_HasBecomesFalse()
    {
        var world = new World();
        var e = world.CreateEntity();
        world.AddTag<PlayerTag>(e);
        world.RemoveTag<PlayerTag>(e);
        Assert.False(world.Has<PlayerTag>(e));
    }

    [Fact]
    public void AddTag_Duplicate_IsIdempotent()
    {
        var world = new World();
        var e = world.CreateEntity();
        world.AddTag<PlayerTag>(e);
        Assert.Throws<InvalidOperationException>(() => world.AddTag<PlayerTag>(e));
        Assert.True(world.Has<PlayerTag>(e));
    }

    // ════════════════════════════════════════════════
    // Read / Replace / runtime-owned write
    // ════════════════════════════════════════════════

    [Fact]
    public void ExecuteQuery_WriteRef_ModifiesInPlace()
    {
        var world = new World();
        var e = world.CreateEntity(new Position { X = 1, Y = 2 });
        var query = world.Query(world.QueryDefinition().ReadWrite<Position>());
        world.ExecuteQuery(query, cursor =>
        {
            foreach (var row in cursor.Rows)
                row.ReadWrite<Position>().X = 99;
        });
        Assert.Equal(99f, world.Read<Position>(e).X);
    }

    [Fact]
    public void Set_UpdatesValue()
    {
        var world = new World();
        var e = world.CreateEntity(new Position { X = 1, Y = 2 });
        world.Replace(e, new Position { X = 50, Y = 60 });
        Assert.Equal(50f, world.Read<Position>(e).X);
        Assert.Equal(60f, world.Read<Position>(e).Y);
    }

    [Fact]
    public void Read_ReturnsCopy()
    {
        var world = new World();
        var e = world.CreateEntity(new Position { X = 1, Y = 2 });
        var copy = world.Read<Position>(e);
        copy.X = 999;
        Assert.Equal(1f, world.Read<Position>(e).X); // 不受影响
    }

    [Fact]
    public void Read_NonExistent_Throws()
    {
        var world = new World();
        var e = world.CreateEntity();
        Assert.Throws<InvalidOperationException>(() => world.Read<Position>(e));
    }

    // ════════════════════════════════════════════════
    // MoveEntity 数据完整性
    // ════════════════════════════════════════════════

    [Fact]
    public void AddVelocity_PositionPreserved()
    {
        var world = new World();
        var e = world.CreateEntity(new Position { X = 42, Y = 84 });
        world.Add(e, new Velocity { X = 5, Y = 6 });
        Assert.Equal(42f, world.Read<Position>(e).X);
        Assert.Equal(84f, world.Read<Position>(e).Y);
        Assert.Equal(5f, world.Read<Velocity>(e).X);
    }

    [Fact]
    public void RemovePosition_VelocityPreserved()
    {
        var world = new World();
        var e = world.Spawn(new PhysicsBundle
        {
            Position = new Position { X = 1, Y = 2 },
            Velocity = new Velocity { X = 7, Y = 8 },
        });
        world.Remove<Position>(e);
        Assert.False(world.Has<Position>(e));
        Assert.Equal(7f, world.Read<Velocity>(e).X);
    }

    [Fact]
    public void ChainedAdd_AllDataPreserved()
    {
        var world = new World();
        var e = world.CreateEntity(new Position { X = 1, Y = 2 });
        world.Add(e, new Velocity { X = 3, Y = 4 });
        world.Add(e, new Health { Value = 100 });
        Assert.Equal(1f, world.Read<Position>(e).X);
        Assert.Equal(3f, world.Read<Velocity>(e).X);
        Assert.Equal(100, world.Read<Health>(e).Value);
    }

    [Fact]
    public void DestroyMiddleEntity_OtherEntitiesIntact()
    {
        var world = new World();
        var e1 = world.CreateEntity(new Position { X = 1, Y = 1 });
        var e2 = world.CreateEntity(new Position { X = 2, Y = 2 });
        var e3 = world.CreateEntity(new Position { X = 3, Y = 3 });
        world.DestroyEntity(e2);
        Assert.Equal(1f, world.Read<Position>(e1).X);
        Assert.Equal(3f, world.Read<Position>(e3).X);
    }

    // ════════════════════════════════════════════════
    // Tag + Component 数据完整性
    // ════════════════════════════════════════════════

    [Fact]
    public void AddTag_DoesNotAffectComponentData()
    {
        var world = new World();
        var e = world.CreateEntity(new Position { X = 10, Y = 20 });
        world.AddTag<PlayerTag>(e);
        Assert.Equal(10f, world.Read<Position>(e).X);
        Assert.True(world.Has<PlayerTag>(e));
    }

    // ════════════════════════════════════════════════
    // Chunk 管理
    // ════════════════════════════════════════════════

    [Fact]
    public void ManyEntities_AutoCreateNewChunk()
    {
        var world = new World();
        // Position = 8 bytes, Entity = 8 bytes -> row = 16 bytes -> capacity = 131072
        const int capacity = 131072;
        const int entityCount = capacity + 1;
        var entities = new List<Entity>();
        for (int i = 0; i < entityCount; i++) // 需要超过 capacity
        {
            entities.Add(world.CreateEntity(new Position { X = i, Y = i * 2 }));
        }
        Assert.Equal(entityCount, world.EntityCount);
        // 验证最后一个的数据
        Assert.Equal(capacity, world.Read<Position>(entities[capacity]).X);
    }

    [Fact]
    public void DestroyAll_KeepsLastEmptyChunk()
    {
        var world = new World();
        var entities = new List<Entity>();
        for (int i = 0; i < 5; i++)
            entities.Add(world.CreateEntity(new Position { X = i, Y = i }));

        for (int i = 0; i < 5; i++)
            world.DestroyEntity(entities[i]);

        Assert.Equal(0, world.EntityCount);
        // 不崩溃，能继续创建
        var e = world.CreateEntity(new Position { X = 99, Y = 99 });
        Assert.Equal(99f, world.Read<Position>(e).X);
    }

    [Fact]
    public void ChunkRecycle_ThenCreate_ReallocatesCorrectly()
    {
        var world = new World();
        var entities = new List<Entity>();

        entities.Add(world.CreateEntity(new Position { X = 0, Y = 0 }));

        var archetype = Assert.Single(
            world.AllArchetypes.ToArray(),
            static candidate => candidate.HasComponent(ComponentMetadata<Position>.Id));
        int maxChunkCapacity = archetype.MaxChunkRows;

        for (int i = 1; i <= maxChunkCapacity; i++)
            entities.Add(world.CreateEntity(new Position { X = i, Y = i * 2 }));

        Assert.Equal(2, archetype.Chunks.Length);

        world.DestroyEntity(entities[maxChunkCapacity]);
        Assert.Equal(1, archetype.Chunks.Length);

        var recreated = world.CreateEntity(new Position { X = 2048, Y = 4096 });
        Assert.True(world.IsAlive(recreated));
        Assert.Equal(2048f, world.Read<Position>(recreated).X);
        Assert.Equal(2, archetype.Chunks.Length);
    }

    // ════════════════════════════════════════════════
    // 迭代保护
    // ════════════════════════════════════════════════

    [Fact]
    public void IterationGuard_Add_Throws()
    {
        var world = new World();
        var e = world.CreateEntity(new Velocity());
        var query = world.Query(world.QueryDefinition().Read<Velocity>());
        world.ExecuteQuery(query, _ =>
        {
            Assert.Throws<InvalidOperationException>(() =>
                world.Add(e, new Position { X = 1, Y = 2 }));
        });
    }

    [Fact]
    public void IterationGuard_Remove_Throws()
    {
        var world = new World();
        var e = world.CreateEntity(new Position { X = 1, Y = 2 });
        var query = world.Query(world.QueryDefinition().Read<Position>());
        world.ExecuteQuery(query, _ =>
        {
            Assert.Throws<InvalidOperationException>(() => world.Remove<Position>(e));
        });
    }

    [Fact]
    public void IterationGuard_Destroy_Throws()
    {
        var world = new World();
        var e = world.CreateEntity(new Position());
        var query = world.Query(world.QueryDefinition().Read<Position>());
        world.ExecuteQuery(query, _ =>
        {
            Assert.Throws<InvalidOperationException>(() => world.DestroyEntity(e));
        });
    }

    [Fact]
    public void IterationGuard_AfterExecuteQuery_StructuralChangesWorkAgain()
    {
        var world = new World();
        var e = world.CreateEntity(new Velocity());
        var query = world.Query(world.QueryDefinition().Read<Velocity>());

        world.ExecuteQuery(query, _ =>
        {
            Assert.Throws<InvalidOperationException>(() =>
                world.Add(e, new Position { X = 1, Y = 2 }));
        });

        world.Add(e, new Position { X = 3, Y = 4 });
        Assert.True(world.Has<Position>(e));

        world.Remove<Position>(e);
        Assert.False(world.Has<Position>(e));

        world.DestroyEntity(e);
        Assert.False(world.IsAlive(e));
    }

    // ════════════════════════════════════════════════
    // 边界条件
    // ════════════════════════════════════════════════

    [Fact]
    public void Stress_1000Entities_CreateDestroyAlternate()
    {
        var world = new World();
        var entities = new List<Entity>();

        for (int i = 0; i < 1000; i++)
        {
            entities.Add(world.CreateEntity(new Position { X = i, Y = -i }));
        }

        // 销毁偶数 entity
        for (int i = 0; i < 1000; i += 2)
        {
            world.DestroyEntity(entities[i]);
        }

        Assert.Equal(500, world.EntityCount);

        // 验证奇数 entity 数据完整
        for (int i = 1; i < 1000; i += 2)
        {
            Assert.True(world.IsAlive(entities[i]));
            Assert.Equal((float)i, world.Read<Position>(entities[i]).X);
        }
    }

    [Fact]
    public void SwapRemove_MovedEntity_StillAccessible()
    {
        var world = new World();
        var e1 = world.CreateEntity(new Position { X = 10, Y = 20 });
        var e2 = world.CreateEntity(new Position { X = 30, Y = 40 });
        var e3 = world.CreateEntity(new Position { X = 50, Y = 60 });

        // Destroy e1 → e3 被 swap 到 e1 的位置（如果在同一 chunk）
        world.DestroyEntity(e1);

        // e2 和 e3 仍然可以访问
        Assert.Equal(30f, world.Read<Position>(e2).X);
        Assert.Equal(50f, world.Read<Position>(e3).X);
    }

    [Fact]
    public void Has_DeadEntity_ReturnsFalse()
    {
        var world = new World();
        var e = world.CreateEntity(new Position { X = 1, Y = 2 });
        world.DestroyEntity(e);
        Assert.False(world.Has<Position>(e));
    }

    [Fact]
    public void CreateEntity_FourComponents()
    {
        var world = new World();
        var e = world.Spawn(new FullComponentBundle
        {
            Position = new Position { X = 1, Y = 2 },
            Velocity = new Velocity { X = 3, Y = 4 },
            Health = new Health { Value = 50 },
            PureUnmanaged = new PureUnmanaged { A = 7, B = 8 },
        });
        Assert.True(world.Has<Position>(e));
        Assert.True(world.Has<Velocity>(e));
        Assert.True(world.Has<Health>(e));
        Assert.True(world.Has<PureUnmanaged>(e));
        Assert.Equal(1f, world.Read<Position>(e).X);
        Assert.Equal(50, world.Read<Health>(e).Value);
        Assert.Equal(7, world.Read<PureUnmanaged>(e).A);
    }

    [Fact]
    public void MultipleWorlds_Independent()
    {
        var w1 = new World();
        var w2 = new World();
        var e1 = w1.CreateEntity(new Position { X = 1, Y = 2 });
        var e2 = w2.CreateEntity(new Position { X = 3, Y = 4 });
        Assert.Equal(1f, w1.Read<Position>(e1).X);
        Assert.Equal(3f, w2.Read<Position>(e2).X);
    }
}
