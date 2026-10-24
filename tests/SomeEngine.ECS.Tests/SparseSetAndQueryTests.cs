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
    public void SparseEntityCopyCanBeModifiedIndependently()
    {
        var world = new World();
        Entity source = world.CreateEntity();
        world.AddSparse(source, new Damage { Amount = 10 });
        Entity copy = world.CloneEntity(source);
        world.ReplaceSparse(copy, new Damage { Amount = 99 });
        Assert.Equal(10, world.ReadSparse<Damage>(source).Amount);
        Assert.Equal(99, world.ReadSparse<Damage>(copy).Amount);
        world.RemoveSparse<Damage>(copy);
        Assert.True(world.HasSparse<Damage>(source));
        Assert.False(world.HasSparse<Damage>(copy));
    }

    [Fact]
    [Trait("Category", "Performance")]
    public void RepeatedSparseReadsInvokeEveryCallback()
    {
        var world = new World();
        Entity entity = world.CreateEntity();
        world.AddSparse(entity, new Damage { Amount = 10 });
        int visits = 0;
        for (int i = 0; i < 128; i++)
            CountSparse(world, ref visits);

        for (int i = 0; i < 1_000; i++)
            CountSparse(world, ref visits);

        Assert.Equal(1_128, visits);
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
