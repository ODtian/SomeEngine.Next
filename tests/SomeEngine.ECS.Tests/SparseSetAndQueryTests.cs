using SomeEngine.ECS;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Sparse;
using SomeEngine.ECS.Registry;
using SomeEngine.ECS.Serialization;
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
    public void AddSparse_HasSparse_ReadSparse()
    {
        var world = new World();
        var e = world.CreateEntity();
        world.AddSparse(e, new Damage { Amount = 25 });
        Assert.True(world.HasSparse<Damage>(e));
        Assert.Equal(25, world.ReadSparse<Damage>(e).Amount);
        Assert.Throws<InvalidOperationException>(
            () => world.AddSparse(e, new Damage { Amount = 30 }));

        world.ReplaceSparse(e, new Damage { Amount = 30 });
        Assert.Equal(30, world.ReadSparse<Damage>(e).Amount);
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
        Assert.Equal(1f, world.Read<Position>(e).X);
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
    public void ExecuteSparseRead_DenseIteration()
    {
        var world = new World();
        var e1 = world.CreateEntity();
        var e2 = world.CreateEntity();
        world.AddSparse(e1, new Damage { Amount = 10 });
        world.AddSparse(e2, new Damage { Amount = 20 });

        int sum = 0;
        int count = 0;
        world.ExecuteSparseRead<Damage, int>(
            ref sum,
            static (ReadOnlySpan<Entity> entities, ReadOnlySpan<Damage> values, ref int total) =>
            {
                Assert.Equal(entities.Length, values.Length);
                foreach (Damage damage in values)
                    total += damage.Amount;
            });
        world.ExecuteSparseRead<Damage>((entities, _) => count = entities.Length);

        Assert.Equal(2, count);
        Assert.Equal(30, sum);
    }

    [Fact]
    public void ExecuteSparseWrite_PreservesEntityValueAlignmentAndBlocksStructuralMutation()
    {
        var world = new World();
        Entity first = world.CreateEntity();
        Entity second = world.CreateEntity();
        Entity third = world.CreateEntity();
        world.AddSparse(first, new Damage { Amount = 10 });
        world.AddSparse(second, new Damage { Amount = 20 });

        world.ExecuteSparseWrite<Damage>((entities, values) =>
        {
            Assert.Equal(entities.Length, values.Length);
            for (int i = 0; i < entities.Length; i++)
                values[i].Amount += entities[i] == first ? 1 : 2;

            Assert.Throws<InvalidOperationException>(
                () => world.AddSparse(third, new Damage { Amount = 30 }));
            Assert.Throws<InvalidOperationException>(() => world.RemoveSparse<Damage>(first));
        });

        Assert.Equal(11, world.ReadSparse<Damage>(first).Amount);
        Assert.Equal(22, world.ReadSparse<Damage>(second).Amount);
        Assert.False(world.HasSparse<Damage>(third));
    }

    [Fact]
    public void FaultedSparseWrite_KeepsPartialValuesAndReleasesBorrow()
    {
        var world = new World();
        Entity first = world.CreateEntity();
        Entity second = world.CreateEntity();
        Entity later = world.CreateEntity();
        world.AddSparse(first, new Damage { Amount = 10 });
        world.AddSparse(second, new Damage { Amount = 20 });

        Assert.Throws<ProbeException>(() =>
            world.ExecuteSparseWrite<Damage>((_, values) =>
            {
                values[0].Amount = 99;
                throw new ProbeException();
            }));

        Assert.Equal(99, world.ReadSparse<Damage>(first).Amount);

        world.AddSparse(later, new Damage { Amount = 30 });
        Assert.Equal(30, world.ReadSparse<Damage>(later).Amount);
    }

    [Fact]
    public void Destroy_RemovesDenseSparseRowAndReusedIndexCanBeAddedAgain()
    {
        var world = new World();
        Entity original = world.CreateEntity();
        world.AddSparse(original, new Damage { Amount = 10 });

        world.DestroyEntity(original);

        int count = -1;
        world.ExecuteSparseRead<Damage>((entities, values) =>
        {
            count = entities.Length;
            Assert.Empty(values.ToArray());
        });
        Assert.Equal(0, count);

        Entity reused = world.CreateEntity();
        Assert.Equal(original.Index, reused.Index);
        Assert.NotEqual(original, reused);
        world.AddSparse(reused, new Damage { Amount = 20 });

        world.ExecuteSparseRead<Damage>((entities, values) =>
        {
            Assert.Equal([reused], entities.ToArray());
            Assert.Equal([20], values.ToArray().Select(static value => value.Amount));
        });
    }

    [Fact]
    public void DetachedSparseOwnerClone_WritesDoNotAffectSourceStorage()
    {
        var world = new World();
        Entity sourceEntity = world.CreateEntity();
        world.AddSparse(sourceEntity, new Damage { Amount = 10 });

        SomeEngine.ECS.Owners.Sparse candidate = world.Sparse.CloneDetached();
        SparseSet<Damage> sourceSet = world.Sparse.Set<Damage>();
        SparseSet<Damage> candidateSet = candidate.Set<Damage>();
        Assert.NotSame(sourceSet, candidateSet);
        Assert.Same(sourceSet.BackingIdentity, candidateSet.BackingIdentity);
        Assert.Equal(0, candidateSet.DetachCount);

        candidateSet.Replace(sourceEntity, new Damage { Amount = 99 });
        Assert.NotSame(sourceSet.BackingIdentity, candidateSet.BackingIdentity);
        Assert.Equal(1, candidateSet.DetachCount);
        Entity candidateOnly = TestEntity.Create(sourceEntity.Index + 10_000, generation: 7);
        candidateSet.Add(candidateOnly, new Damage { Amount = 20 });

        Assert.Equal(10, world.ReadSparse<Damage>(sourceEntity).Amount);
        Assert.Equal(1, world.Sparse.Set<Damage>().Count);
        Assert.Equal(99, candidateSet.Read(sourceEntity).Amount);
        Assert.Equal(20, candidateSet.Read(candidateOnly).Amount);
        Assert.Equal(2, candidateSet.Count);
    }

    [Fact]
    [Trait("Category", "Performance")]
    public void ExecuteSparseRead_WarmedStaticStateCallbackAllocatesZeroBytes()
    {
        var world = new World();
        Entity entity = world.CreateEntity();
        world.AddSparse(entity, new Damage { Amount = 10 });
        int visits = 0;
        for (int i = 0; i < 128; i++)
            CountSparse(world, ref visits);

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1_000; i++)
            CountSparse(world, ref visits);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(1_128, visits);
        Assert.Equal(0, allocated);
    }

    private static void CountSparse(World world, ref int visits)
    {
        world.ExecuteSparseRead<Damage, int>(
            ref visits,
            static (
                ReadOnlySpan<Entity> entities,
                ReadOnlySpan<Damage> _,
                ref int count) => count += entities.Length);
    }

    private sealed class ProbeException : Exception;
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
        var query = world.Query(world.QueryDefinition().All<Position>());

        Assert.Equal([e], CollectEntities(world, query));
    }

    [Fact]
    public void Without_ExcludesArchetype()
    {
        var world = new World();
        var positionOnly = world.CreateEntity(new Position { X = 1, Y = 2 });
        _ = world.Spawn(new PhysicsBundle
        {
            Position = new Position { X = 3, Y = 4 },
            Velocity = new Velocity { X = 5, Y = 6 },
        });

        var query = world.Query(
            world.QueryDefinition()
                .All<Position>()
                .None<Velocity>());

        Assert.Equal([positionOnly], CollectEntities(world, query));
    }

    [Fact]
    public void MultipleWith_ANDSemantics()
    {
        var world = new World();
        _ = world.CreateEntity(new Position { X = 1, Y = 2 });
        var both = world.Spawn(new PhysicsBundle
        {
            Position = new Position { X = 3, Y = 4 },
            Velocity = new Velocity { X = 5, Y = 6 },
        });

        var query = world.Query(
            world.QueryDefinition()
                .All<Position>()
                .All<Velocity>());

        Assert.Equal([both], CollectEntities(world, query));
    }

    [Fact]
    public void EmptyRequired_MatchesAllArchetypes()
    {
        var world = new World();
        world.CreateEntity(new Position { X = 1, Y = 2 });
        world.CreateEntity(new Velocity { X = 3, Y = 4 });

        var query = world.Query(world.QueryDefinition());

        Assert.Equal(2, CountRows(world, query));
    }

    [Fact]
    public void NonMatchingArchetype_NotInList()
    {
        var world = new World();
        world.CreateEntity(new Health { Value = 100 });
        var query = world.Query(world.QueryDefinition().All<Position>());

        Assert.Equal(0, CountRows(world, query));
    }

    [Fact]
    public void DynamicUpdate_NewArchetypeAfterBuild()
    {
        var world = new World();
        var query = world.Query(world.QueryDefinition().All<Position>());
        Assert.Equal(0, CountRows(world, query));

        world.CreateEntity(new Position { X = 1, Y = 2 });
        Assert.Equal(1, CountRows(world, query));
    }

    [Fact]
    public void ExistingArchetypes_IncludedOnBuild()
    {
        var world = new World();
        world.CreateEntity(new Position { X = 1, Y = 2 });
        var query = world.Query(world.QueryDefinition().All<Position>());
        Assert.Equal(1, CountRows(world, query));
    }

    [Fact]
    public void QueryIteration_CanReadChunkData()
    {
        var world = new World();
        world.CreateEntity(new Position { X = 10, Y = 20 });
        world.CreateEntity(new Position { X = 30, Y = 40 });

        var query = world.Query(world.QueryDefinition().Read<Position>());

        int entityCount = 0;
        float sumX = 0;
        world.ExecuteQuery(query, cursor =>
        {
            foreach (var row in cursor.Rows)
            {
                sumX += row.Read<Position>().X;
                entityCount++;
            }
        });
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

        var query = world.Query(
            world.QueryDefinition()
                .All<Position>()
                .All<PlayerTag>());

        Assert.Equal([e1], CollectEntities(world, query));
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

        var q1 = world.Query(world.QueryDefinition().All<Position>());
        var q2 = world.Query(
            world.QueryDefinition()
                .All<Position>()
                .All<Velocity>());

        Assert.Equal(2, CountRows(world, q1));
        Assert.Equal(1, CountRows(world, q2));
    }

    [Fact]
    public void Without_DynamicUpdate_NewExcludedArchetype_IsNotAdded()
    {
        var world = new World();
        world.CreateEntity(new Position { X = 1, Y = 2 });

        var query = world.Query(
            world.QueryDefinition()
                .All<Position>()
                .None<Velocity>());
        Assert.Equal(1, CountRows(world, query));

        world.Spawn(new PhysicsBundle
        {
            Position = new Position { X = 3, Y = 4 },
            Velocity = new Velocity { X = 5, Y = 6 },
        });

        Assert.Equal(1, CountRows(world, query));
    }

    private static int CountRows(World world, QueryHandle query)
    {
        int count = 0;
        world.ExecuteQuery(query, ref count, static (QueryCursor cursor, ref int state) =>
        {
            foreach (var _ in cursor.Rows)
                state++;
        });
        return count;
    }

    private static Entity[] CollectEntities(World world, QueryHandle query)
    {
        var entities = new List<Entity>();
        world.ExecuteQuery(query, cursor =>
        {
            foreach (var row in cursor.Rows)
                entities.Add(row.Entity);
        });
        return entities.ToArray();
    }
}
