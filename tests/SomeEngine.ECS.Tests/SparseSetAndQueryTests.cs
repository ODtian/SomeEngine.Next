using SomeEngine.ECS;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Sparse;
using SomeEngine.ECS.Registry;
using Xunit;

namespace SomeEngine.ECS.Tests;

// Damage, Buff 已在 ComponentMetadataTests.cs 中定义

// ════════════════════════════════════════════════════
// SparseSet 单元测试
// ════════════════════════════════════════════════════

public class SparseSetTests
{
    private static Entity MakeEntity(int index, int generation = 0) => TestEntity.Create(index, generation);

    [Fact]
    public void Add_Has_Get()
    {
        var set = new SparseSet<Damage>();
        var e = MakeEntity(1);
        set.Add(e, new Damage { Amount = 42 });
        Assert.True(set.Has(e));
        Assert.Equal(42, set.Get(e).Amount);
    }

    [Fact]
    public void Add_GrowsFromZeroInitialCapacity()
    {
        var set = new SparseSet<Damage>(0);
        var e = MakeEntity(1);

        set.Add(e, new Damage { Amount = 42 });

        Assert.True(set.Has(e));
        Assert.Equal(42, set.Get(e).Amount);
    }

    [Fact]
    public void Add_Duplicate_Throws()
    {
        var set = new SparseSet<Damage>();
        var e = MakeEntity(1);
        set.Add(e, new Damage { Amount = 1 });
        Assert.Throws<InvalidOperationException>(() => set.Add(e, new Damage { Amount = 2 }));
    }

    [Fact]
    public void Remove_HasBecomesFalse()
    {
        var set = new SparseSet<Damage>();
        var e = MakeEntity(1);
        set.Add(e, new Damage { Amount = 10 });
        set.Remove(e);
        Assert.False(set.Has(e));
    }

    [Fact]
    public void Remove_NonExistent_Throws()
    {
        var set = new SparseSet<Damage>();
        var e = MakeEntity(1);
        Assert.Throws<InvalidOperationException>(() => set.Remove(e));
    }

    [Fact]
    public void SwapRemove_MovedEntityStillAccessible()
    {
        var set = new SparseSet<Damage>();
        var e1 = MakeEntity(1);
        var e2 = MakeEntity(2);
        var e3 = MakeEntity(3);
        set.Add(e1, new Damage { Amount = 10 });
        set.Add(e2, new Damage { Amount = 20 });
        set.Add(e3, new Damage { Amount = 30 });

        set.Remove(e1); // e3 should swap to position 0

        Assert.True(set.Has(e2));
        Assert.True(set.Has(e3));
        Assert.Equal(20, set.Get(e2).Amount);
        Assert.Equal(30, set.Get(e3).Amount);
        Assert.Equal(2, set.Count);
    }

    [Fact]
    public void HighIndexEntity_AllocatesPage()
    {
        var set = new SparseSet<Damage>();
        var e = MakeEntity(5000); // page = 5000 >> 12 = 1
        set.Add(e, new Damage { Amount = 99 });
        Assert.True(set.Has(e));
        Assert.Equal(99, set.Get(e).Amount);
    }

    [Fact]
    public void Count_TracksAddRemove()
    {
        var set = new SparseSet<Damage>();
        Assert.Equal(0, set.Count);
        var e1 = MakeEntity(1);
        var e2 = MakeEntity(2);
        set.Add(e1, new Damage { Amount = 1 });
        set.Add(e2, new Damage { Amount = 2 });
        Assert.Equal(2, set.Count);
        set.Remove(e1);
        Assert.Equal(1, set.Count);
    }

    [Fact]
    public void DenseEntities_DenseData_Correct()
    {
        var set = new SparseSet<Damage>();
        var e1 = MakeEntity(1);
        var e2 = MakeEntity(2);
        set.Add(e1, new Damage { Amount = 10 });
        set.Add(e2, new Damage { Amount = 20 });

        var entities = set.DenseEntities;
        var data = set.DenseData;
        Assert.Equal(2, entities.Length);
        Assert.Equal(2, data.Length);
        // 验证数据（顺序可能是 e1,e2）
        int sum = data[0].Amount + data[1].Amount;
        Assert.Equal(30, sum);
    }

    [Fact]
    public void BulkAddRemove_DataConsistency()
    {
        var set = new SparseSet<Damage>();
        var entities = new Entity[100];
        for (int i = 0; i < 100; i++)
        {
            entities[i] = MakeEntity(i + 1);
            set.Add(entities[i], new Damage { Amount = i * 10 });
        }

        // 移除偶数
        for (int i = 0; i < 100; i += 2)
            set.Remove(entities[i]);

        Assert.Equal(50, set.Count);

        // 验证奇数还在
        for (int i = 1; i < 100; i += 2)
        {
            Assert.True(set.Has(entities[i]));
            Assert.Equal(i * 10, set.Get(entities[i]).Amount);
        }
    }

    [Fact]
    public void Get_Ref_ModifiesInPlace()
    {
        var set = new SparseSet<Damage>();
        var e = MakeEntity(1);
        set.Add(e, new Damage { Amount = 5 });
        ref var d = ref set.Get(e);
        d.Amount = 99;
        Assert.Equal(99, set.Get(e).Amount);
    }

    [Fact]
    public void Read_ReturnsCopy()
    {
        var set = new SparseSet<Damage>();
        var e = MakeEntity(1);
        set.Add(e, new Damage { Amount = 5 });
        var copy = set.Read(e);
        copy.Amount = 999;
        Assert.Equal(5, set.Get(e).Amount);
    }

    [Fact]
    public void NullEntity_Has_ReturnsFalse()
    {
        var set = new SparseSet<Damage>();
        Assert.False(set.Has(Entity.Null));
    }

    [Fact]
    public void StaleGeneration_HasReturnsFalse()
    {
        var set = new SparseSet<Damage>();
        var original = MakeEntity(1, generation: 0);
        var stale = MakeEntity(1, generation: 1);

        set.Add(original, new Damage { Amount = 1 });

        Assert.True(set.Has(original));
        Assert.False(set.Has(stale));
    }

    [Fact]
    public void NullEntity_Add_Throws()
    {
        var set = new SparseSet<Damage>();
        Assert.Throws<InvalidOperationException>(() =>
            set.Add(Entity.Null, new Damage { Amount = 1 }));
    }

    [Fact]
    public void NullEntity_GetAndRemove_Throw()
    {
        var set = new SparseSet<Damage>();
        Assert.Throws<InvalidOperationException>(() => set.Get(Entity.Null));
        Assert.Throws<InvalidOperationException>(() => set.Remove(Entity.Null));
    }
}

// ════════════════════════════════════════════════════
// World SparseSet 集成测试
// ════════════════════════════════════════════════════

public class WorldSparseTests
{
    [Fact]
    public void AddSparse_HasSparse_GetSparse()
    {
        var world = new World();
        var e = world.CreateEntity();
        world.AddSparse(e, new Damage { Amount = 25 });
        Assert.True(world.HasSparse<Damage>(e));
        Assert.Equal(25, world.GetSparse<Damage>(e).Amount);
        Assert.Throws<InvalidOperationException>(
            () => world.AddSparse(e, new Damage { Amount = 30 }));

        world.ReplaceSparse(e, new Damage { Amount = 30 });
        Assert.Equal(30, world.GetSparse<Damage>(e).Amount);
    }

    [Fact]
    public void RemoveSparse_HasBecomesFalse()
    {
        var world = new World();
        var e = world.CreateEntity();
        world.AddSparse(e, new Damage { Amount = 10 });
        world.RemoveSparse<Damage>(e);
        Assert.False(world.HasSparse<Damage>(e));
    }

    [Fact]
    public void ReplaceSparse_Missing_Throws()
    {
        var world = new World();
        var e = world.CreateEntity();

        Assert.Throws<InvalidOperationException>(
            () => world.ReplaceSparse(e, new Damage { Amount = 10 }));
    }

    [Fact]
    public void Sparse_DoesNotAffectArchetype()
    {
        var world = new World();
        var e = world.CreateEntity(new Position { X = 1, Y = 2 });
        world.AddSparse(e, new Damage { Amount = 50 });
        // Archetype 不变：仍有 Position
        Assert.True(world.Has<Position>(e));
        Assert.Equal(1f, world.Get<Position>(e).X);
        // Sparse 独立
        Assert.True(world.HasSparse<Damage>(e));
        world.RemoveSparse<Damage>(e);
        Assert.True(world.Has<Position>(e)); // Archetype 不变
    }

    [Fact]
    public void HasSparse_DeadEntity_ReturnsFalse()
    {
        var world = new World();
        var e = world.CreateEntity();
        world.AddSparse(e, new Damage { Amount = 10 });
        world.DestroyEntity(e);
        Assert.False(world.HasSparse<Damage>(e));
    }

    [Fact]
    public void GetSparseSet_DirectIteration()
    {
        var world = new World();
        var e1 = world.CreateEntity();
        var e2 = world.CreateEntity();
        world.AddSparse(e1, new Damage { Amount = 10 });
        world.AddSparse(e2, new Damage { Amount = 20 });

        var sparseSet = world.GetSparseSet<Damage>();
        Assert.Equal(2, sparseSet.Count);
        int sum = 0;
        foreach (var d in sparseSet.DenseData)
            sum += d.Amount;
        Assert.Equal(30, sum);
    }
}

// ════════════════════════════════════════════════════
// QueryCache 测试
// ════════════════════════════════════════════════════

public class QueryCacheTests
{
    [Fact]
    public void With_MatchesArchetypeContainingComponent()
    {
        var world = new World();
        var e = world.CreateEntity(new Position { X = 1, Y = 2 });
        var query = world.CreateQuery().With<Position>().Build();
        Assert.True(query.Archetypes.Count >= 1);
        // 该 archetype 应包含 Position
        Assert.Contains(query.Archetypes, a => a.HasComponent(ComponentMetadata<Position>.Id));
    }

    [Fact]
    public void Without_ExcludesArchetype()
    {
        var world = new World();
        world.CreateEntity(new Position { X = 1, Y = 2 });
        world.Spawn(new PhysicsBundle
        {
            Position = new Position { X = 3, Y = 4 },
            Velocity = new Velocity { X = 5, Y = 6 },
        });

        var query = world.CreateQuery().With<Position>().Without<Velocity>().Build();

        // 只匹配 [Position] 不匹配 [Position, Velocity]
        foreach (var arch in query.Archetypes)
        {
            Assert.True(arch.HasComponent(ComponentMetadata<Position>.Id));
            Assert.False(arch.HasComponent(ComponentMetadata<Velocity>.Id));
        }
    }

    [Fact]
    public void MultipleWith_ANDSemantics()
    {
        var world = new World();
        world.CreateEntity(new Position { X = 1, Y = 2 });
        world.Spawn(new PhysicsBundle
        {
            Position = new Position { X = 3, Y = 4 },
            Velocity = new Velocity { X = 5, Y = 6 },
        });

        var query = world.CreateQuery().With<Position>().With<Velocity>().Build();

        foreach (var arch in query.Archetypes)
        {
            Assert.True(arch.HasComponent(ComponentMetadata<Position>.Id));
            Assert.True(arch.HasComponent(ComponentMetadata<Velocity>.Id));
        }
        Assert.Single(query.Archetypes);
    }

    [Fact]
    public void EmptyRequired_MatchesAllArchetypes()
    {
        var world = new World();
        world.CreateEntity(new Position { X = 1, Y = 2 });
        world.CreateEntity(new Velocity { X = 3, Y = 4 });

        var query = world.CreateQuery().Build();

        // 应匹配所有 archetype（含空 archetype）
        Assert.True(query.Archetypes.Count >= 3); // empty + [Position] + [Velocity]
    }

    [Fact]
    public void NonMatchingArchetype_NotInList()
    {
        var world = new World();
        world.CreateEntity(new Health { Value = 100 });
        var query = world.CreateQuery().With<Position>().Build();

        foreach (var arch in query.Archetypes)
            Assert.True(arch.HasComponent(ComponentMetadata<Position>.Id));
    }

    [Fact]
    public void DynamicUpdate_NewArchetypeAfterBuild()
    {
        var world = new World();
        var query = world.CreateQuery().With<Position>().Build();
        Assert.Empty(query.Archetypes); // 还没有 Position archetype

        world.CreateEntity(new Position { X = 1, Y = 2 });
        Assert.Single(query.Archetypes); // 自动包含
    }

    [Fact]
    public void ExistingArchetypes_IncludedOnBuild()
    {
        var world = new World();
        world.CreateEntity(new Position { X = 1, Y = 2 });
        var query = world.CreateQuery().With<Position>().Build();
        Assert.True(query.Archetypes.Count >= 1);
    }

    [Fact]
    public void QueryIteration_CanReadChunkData()
    {
        var world = new World();
        world.CreateEntity(new Position { X = 10, Y = 20 });
        world.CreateEntity(new Position { X = 30, Y = 40 });

        var query = world.CreateQuery().With<Position>().Build();

        int entityCount = 0;
        float sumX = 0;
        foreach (var arch in query.Archetypes)
        {
            int colIdx = arch.Column(ComponentMetadata<Position>.Id);
            foreach (var chunk in arch.Chunks)
            {
                for (int i = 0; i < chunk.Count; i++)
                {
                    var pos = chunk.ReadComponent<Position>(colIdx, i);
                    sumX += pos.X;
                    entityCount++;
                }
            }
        }
        Assert.Equal(2, entityCount);
        Assert.Equal(40f, sumX);
    }

    [Fact]
    public void WithTag_MatchesTaggedArchetype()
    {
        var world = new World();
        var e1 = world.CreateEntity(new Position { X = 1, Y = 2 });
        world.AddTag<PlayerTag>(e1);

        var e2 = world.CreateEntity(new Position { X = 3, Y = 4 });

        var query = world.CreateQuery().With<Position>().With<PlayerTag>().Build();
        Assert.Single(query.Archetypes);
    }

    [Fact]
    public void QueryCache_MultipleQueries_Independent()
    {
        var world = new World();
        world.CreateEntity(new Position { X = 1, Y = 2 });
        world.Spawn(new PhysicsBundle
        {
            Position = new Position { X = 3, Y = 4 },
            Velocity = new Velocity { X = 5, Y = 6 },
        });

        var q1 = world.CreateQuery().With<Position>().Build();
        var q2 = world.CreateQuery().With<Position>().With<Velocity>().Build();

        Assert.Equal(2, q1.Archetypes.Count); // [Pos] + [Pos,Vel]
        Assert.Single(q2.Archetypes); // [Pos,Vel]
    }

    [Fact]
    public void Without_DynamicUpdate_NewExcludedArchetype_IsNotAdded()
    {
        var world = new World();
        world.CreateEntity(new Position { X = 1, Y = 2 });

        var query = world.CreateQuery().With<Position>().Without<Velocity>().Build();
        Assert.Single(query.Archetypes);

        world.Spawn(new PhysicsBundle
        {
            Position = new Position { X = 3, Y = 4 },
            Velocity = new Velocity { X = 5, Y = 6 },
        });

        Assert.Single(query.Archetypes);
        foreach (var arch in query.Archetypes)
        {
            Assert.True(arch.HasComponent(ComponentMetadata<Position>.Id));
            Assert.False(arch.HasComponent(ComponentMetadata<Velocity>.Id));
        }
    }
}
