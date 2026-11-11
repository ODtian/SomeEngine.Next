using SomeEngine.ECS;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
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
        var e = world.Create();
        world.AddSparse(e, new Damage { Amount = 25 });
        Assert.True(world.HasSparse<Damage>(e));
        Assert.Equal(25, world.ReadSparse<Damage>(e).Amount);
        world.ReplaceSparse(e, new Damage { Amount = 30 });
        Assert.Equal(30, world.ReadSparse<Damage>(e).Amount);
    }

    [Fact]
    public void RemoveSparse_HasBecomesFalse()
    {
        var world = new World();
        var e = world.Create();
        world.SetSparse(e, new Damage { Amount = 10 });
        world.RemoveSparse<Damage>(e);
        Assert.False(world.HasSparse<Damage>(e));
    }

    [Fact]
    public void ReplaceSparse_Missing_Throws()
    {
        using var world = new World();
        var entity = world.Create();
        Assert.Throws<InvalidOperationException>(() => world.ReplaceSparse(entity, new Damage { Amount = 10 }));
        Assert.False(world.HasSparse<Damage>(entity));
    }

    [Fact]
    public void Sparse_DoesNotAffectArchetype()
    {
        var world = new World();
        var e = world.Create(new Position { X = 1, Y = 2 });
        world.SetSparse(e, new Damage { Amount = 50 });
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
        var e = world.Create();
        world.SetSparse(e, new Damage { Amount = 10 });
        world.Destroy(e);
        Assert.False(world.HasSparse<Damage>(e));
    }

    [Fact]
    public void ExecuteSparseRead_DenseIteration()
    {
        var world = new World();
        var e1 = world.Create();
        var e2 = world.Create();
        world.SetSparse(e1, new Damage { Amount = 10 });
        world.SetSparse(e2, new Damage { Amount = 20 });

        int sum = 0;
        int count = 0;
        {
            var sparseScope = world.OpenSparseRead<Damage>();
            try
            {
                ReadOnlySpan<Entity> entities = sparseScope.Entities;
ReadOnlySpan<Damage> values = sparseScope.Values;

                Assert.Equal(entities.Length, values.Length);
                foreach (Damage damage in values)
                    sum += damage.Amount;

            }
            finally { sparseScope.Dispose(); }
        };
        {
            var sparseScope = world.OpenSparseRead<Damage>();
            try
            {
                ReadOnlySpan<Entity> entities = sparseScope.Entities;
ReadOnlySpan<Damage> _ = sparseScope.Values;
count = entities.Length;
            }
            finally { sparseScope.Dispose(); }
        };

        Assert.Equal(2, count);
        Assert.Equal(30, sum);
    }

    [Fact]
    public void ExecuteSparseWrite_PreservesEntityValueAlignmentAndBlocksStructuralMutation()
    {
        var world = new World();
        Entity first = world.Create();
        Entity second = world.Create();
        Entity third = world.Create();
        world.SetSparse(first, new Damage { Amount = 10 });
        world.SetSparse(second, new Damage { Amount = 20 });

        {
            var sparseScope = world.OpenSparseWrite<Damage>();
            try
            {
                ReadOnlySpan<Entity> entities = sparseScope.Entities;
Span<Damage> values = sparseScope.Values;

            Assert.Equal(entities.Length, values.Length);
            for (int i = 0; i < entities.Length; i++)
                values[i].Amount += entities[i] == first ? 1 : 2;

            Assert.Throws<InvalidOperationException>(
                () => world.SetSparse(third, new Damage { Amount = 30 }));
            Assert.Throws<InvalidOperationException>(() => world.RemoveSparse<Damage>(first));

            }
            finally { sparseScope.Dispose(); }
        };

        Assert.Equal(11, world.ReadSparse<Damage>(first).Amount);
        Assert.Equal(22, world.ReadSparse<Damage>(second).Amount);
        Assert.False(world.HasSparse<Damage>(third));
    }

    [Fact]
    public void FaultedSparseWrite_KeepsPartialValuesAndReleasesBorrow()
    {
        var world = new World();
        Entity first = world.Create();
        Entity second = world.Create();
        Entity later = world.Create();
        world.SetSparse(first, new Damage { Amount = 10 });
        world.SetSparse(second, new Damage { Amount = 20 });

        Assert.Throws<ProbeException>((Action)(() =>
            {
            var sparseScope = world.OpenSparseWrite<Damage>();
            try
            {
                Span<Damage> values = sparseScope.Values;

                values[0].Amount = 99;
                throw new ProbeException();

            }
            finally { sparseScope.Dispose(); }
        }));

        Assert.Equal(99, world.ReadSparse<Damage>(first).Amount);

        world.SetSparse(later, new Damage { Amount = 30 });
        Assert.Equal(30, world.ReadSparse<Damage>(later).Amount);
    }

    [Fact]
    public void Destroy_RemovesDenseSparseRowAndReusedIndexCanBeAddedAgain()
    {
        var world = new World();
        Entity original = world.Create();
        world.SetSparse(original, new Damage { Amount = 10 });

        world.Destroy(original);

        int count = -1;
        {
            var sparseScope = world.OpenSparseRead<Damage>();
            try
            {
                ReadOnlySpan<Entity> entities = sparseScope.Entities;
ReadOnlySpan<Damage> values = sparseScope.Values;

            count = entities.Length;
            Assert.Empty(values.ToArray());

            }
            finally { sparseScope.Dispose(); }
        };
        Assert.Equal(0, count);

        Entity reused = world.Create();
        Assert.Equal(original.Id, reused.Id);
        Assert.NotEqual(original, reused);
        world.SetSparse(reused, new Damage { Amount = 20 });

        {
            var sparseScope = world.OpenSparseRead<Damage>();
            try
            {
                ReadOnlySpan<Entity> entities = sparseScope.Entities;
ReadOnlySpan<Damage> values = sparseScope.Values;

            Assert.Equal([reused], entities.ToArray());
            Assert.Equal([20], values.ToArray().Select(static value => value.Amount));

            }
            finally { sparseScope.Dispose(); }
        };
    }

    [Fact]
    public void SparseEntityCopyCanBeModifiedIndependently()
    {
        var world = new World();
        Entity source = world.Create();
        world.SetSparse(source, new Damage { Amount = 10 });
        Entity copy = world.Clone(source);
        world.SetSparse(copy, new Damage { Amount = 99 });
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
        Entity entity = world.Create();
        world.SetSparse(entity, new Damage { Amount = 10 });
        int visits = 0;
        for (int i = 0; i < 128; i++)
            CountSparse(world, ref visits);

        for (int i = 0; i < 1_000; i++)
            CountSparse(world, ref visits);

        Assert.Equal(1_128, visits);
    }

    private static void CountSparse(World world, ref int visits)
    {
        {
            var sparseScope = world.OpenSparseRead<Damage>();
            try
            {
                ReadOnlySpan<Entity> entities = sparseScope.Entities;
ReadOnlySpan<Damage> _ = sparseScope.Values;
visits += entities.Length;
            }
            finally { sparseScope.Dispose(); }
        };
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
        var e = world.Create(new Position { X = 1, Y = 2 });
        var query = world.Query(new QueryDescription().WithStored<Position>());

        Assert.Equal([e], CollectEntities(world, query));
    }

    [Fact]
    public void Without_ExcludesArchetype()
    {
        var world = new World();
        var positionOnly = world.Create(new Position { X = 1, Y = 2 });
        _ = world.Spawn(new PhysicsBundle
        {
            Position = new Position { X = 3, Y = 4 },
            Velocity = new Velocity { X = 5, Y = 6 },
        });

        var query = world.Query(
            new QueryDescription().WithStored<Position>().Without<Velocity>());

        Assert.Equal([positionOnly], CollectEntities(world, query));
    }

    [Fact]
    public void MultipleWith_ANDSemantics()
    {
        var world = new World();
        _ = world.Create(new Position { X = 1, Y = 2 });
        var both = world.Spawn(new PhysicsBundle
        {
            Position = new Position { X = 3, Y = 4 },
            Velocity = new Velocity { X = 5, Y = 6 },
        });

        var query = world.Query(
            new QueryDescription().WithStored<Position>().WithStored<Velocity>());

        Assert.Equal([both], CollectEntities(world, query));
    }

    [Fact]
    public void EmptyRequired_MatchesAllArchetypes()
    {
        var world = new World();
        world.Create(new Position { X = 1, Y = 2 });
        world.Create(new Velocity { X = 3, Y = 4 });

        var query = world.Query(new QueryDescription());

        Assert.Equal(2, CountRows(world, query));
    }

    [Fact]
    public void NonMatchingArchetype_NotInList()
    {
        var world = new World();
        world.Create(new Health { Value = 100 });
        var query = world.Query(new QueryDescription().WithStored<Position>());

        Assert.Equal(0, CountRows(world, query));
    }

    [Fact]
    public void DynamicUpdate_NewArchetypeAfterBuild()
    {
        var world = new World();
        var query = world.Query(new QueryDescription().WithStored<Position>());
        Assert.Equal(0, CountRows(world, query));

        world.Create(new Position { X = 1, Y = 2 });
        Assert.Equal(1, CountRows(world, query));
    }

    [Fact]
    public void ExistingArchetypes_IncludedOnBuild()
    {
        var world = new World();
        world.Create(new Position { X = 1, Y = 2 });
        var query = world.Query(new QueryDescription().WithStored<Position>());
        Assert.Equal(1, CountRows(world, query));
    }

    [Fact]
    public void QueryIteration_CanReadChunkData()
    {
        var world = new World();
        world.Create(new Position { X = 10, Y = 20 });
        world.Create(new Position { X = 30, Y = 40 });

        var query = world.Query(new QueryDescription().WithStored<Position>());

        int entityCount = 0;
        float sumX = 0;
        {
            using QueryScope cursor = (query).OpenRead();
            foreach (QueryChunk rowChunk in cursor)
                for (int row = 0; row < rowChunk.Count; row++)
            {
                sumX += rowChunk.Read<Position>()[row].X;
                entityCount++;
            }

        };
        Assert.Equal(2, entityCount);
        Assert.Equal(40f, sumX);
    }

    [Fact]
    public void WithTag_MatchesTaggedArchetype()
    {
        var world = new World();
        var e1 = world.Create(new Position { X = 1, Y = 2 });
        world.Add(e1, default(PlayerTag));

        var e2 = world.Create(new Position { X = 3, Y = 4 });

        var query = world.Query(
            new QueryDescription().WithStored<Position>().WithStored<PlayerTag>());

        Assert.Equal([e1], CollectEntities(world, query));
    }

    [Fact]
    public void QueryCache_MultipleQueries_Independent()
    {
        var world = new World();
        world.Create(new Position { X = 1, Y = 2 });
        world.Spawn(new PhysicsBundle
        {
            Position = new Position { X = 3, Y = 4 },
            Velocity = new Velocity { X = 5, Y = 6 },
        });

        var q1 = world.Query(new QueryDescription().WithStored<Position>());
        var q2 = world.Query(
            new QueryDescription().WithStored<Position>().WithStored<Velocity>());

        Assert.Equal(2, CountRows(world, q1));
        Assert.Equal(1, CountRows(world, q2));
    }

    [Fact]
    public void Without_DynamicUpdate_NewExcludedArchetype_IsNotAdded()
    {
        var world = new World();
        world.Create(new Position { X = 1, Y = 2 });

        var query = world.Query(
            new QueryDescription().WithStored<Position>().Without<Velocity>());
        Assert.Equal(1, CountRows(world, query));

        world.Spawn(new PhysicsBundle
        {
            Position = new Position { X = 3, Y = 4 },
            Velocity = new Velocity { X = 5, Y = 6 },
        });

        Assert.Equal(1, CountRows(world, query));
    }

    private static int CountRows(World world, Query query)
    {
        int count = 0;
        {
            using QueryScope scope = query.OpenRead();
            foreach (QueryChunk chunk in scope) count += chunk.Count;

        };
        return count;
    }

    private static Entity[] CollectEntities(World world, Query query)
    {
        var entities = new List<Entity>();
        {
            using QueryScope cursor = (query).OpenRead();
            foreach (QueryChunk rowChunk in cursor)
                for (int row = 0; row < rowChunk.Count; row++)
                entities.Add(rowChunk.Entities[row]);

        };
        return entities.ToArray();
    }
}
