using SomeEngine.ECS;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Registry;
using Xunit;

namespace SomeEngine.ECS.Tests;

public class QueryScopeIterationTests
{
    [Fact]
    public void QueryScope_Rows_ReadMatchingData()
    {
        using var world = new World();
        world.Create(new Position { X = 1, Y = 2 });
        world.Create(new Position { X = 3, Y = 4 });
        world.Create(new Velocity { X = 100, Y = 200 });

        var query = world.Query(new QueryDescription().WithStored<Position>());

        int count = 0;
        float sumX = 0;
        {
            using QueryScope cursor = query.OpenRead();
            foreach (QueryChunk rowChunk in cursor)
                for (int row = 0; row < rowChunk.Count; row++)
                {
                    count++;
                    sumX += rowChunk.Read<Position>()[row].X;
                }

        }

        Assert.Equal(2, count);
        Assert.Equal(4f, sumX);
    }

    [Fact]
    public void QueryScope_Chunks_ReadWriteCanMutate()
    {
        using var world = new World();
        var entity = world.Create(new Position { X = 1, Y = 2 });

        var query = world.Query(new QueryDescription().WithStored<Position>());

        {
            using QueryScope cursor = query.Open();
            foreach (var chunk in cursor)
                chunk.Write<Position>()[0].X = 99;

        }

        Assert.Equal(99f, world.Read<Position>(entity).X);
    }

    [Fact]
    public void QueryScopeRetainsItsObservationVersion()
    {
        using var world = new World();
        world.Create(new Position { X = 1, Y = 2 });
        const ulong last = 7;
        using QueryScope scope = world.Query<Position>().Since(last).OpenRead();
        Assert.Equal(last, scope.SinceVersion);
        foreach (QueryChunk chunk in scope) Assert.Equal(1, chunk.Read<Position>()[0].X);
    }

    [Fact]
    public void QueryScopeEarlyBreakAndFollowingMutationDoesNotThrow()
    {
        using var world = new World();
        world.Create(new Position { X = 1, Y = 2 });

        var query = world.Query(new QueryDescription().WithStored<Position>());
        {
            using QueryScope cursor = query.OpenRead();
            foreach (var _ in cursor)
                break;

        }

        world.Add(world.Create(), new Velocity { X = 1, Y = 2 });
    }

    [Fact]
    public void QueryScope_Rows_ReadsMultipleComponents()
    {
        using var world = new World();
        world.Spawn(new PhysicsBundle { Position = new() { X = 1 }, Velocity = new() { X = 5 } });
        world.Spawn(new PhysicsBundle { Position = new() { X = 3 }, Velocity = new() { X = 7 } });

        var query = world.Query(
            new QueryDescription().WithStored<Position>().WithStored<Velocity>());

        float sumVx = 0;
        {
            using QueryScope cursor = query.OpenRead();
            foreach (QueryChunk rowChunk in cursor)
                for (int row = 0; row < rowChunk.Count; row++)
                    sumVx += rowChunk.Read<Velocity>()[row].X;

        }

        Assert.Equal(12f, sumVx);
    }

    [Fact]
    public void WritableQueryMakesItsComponentVisibleToChangedFilter()
    {
        using var world = new World();
        var entity = world.Create(new Position { X = 1, Y = 2 });
        ulong baseline = world.AcquireSystemVersion();
        var query = world.Query(new QueryDescription().WithStored<Position>());
        {
            using QueryScope cursor = query.Open();
            foreach (QueryChunk rowChunk in cursor)
                for (int row = 0; row < rowChunk.Count; row++)
                    rowChunk.Write<Position>(row).X += 1;

        }
        Assert.Equal(1, CountChangedRows<Position>(world, baseline));
    }

    [Fact]
    public void ReadOnlyQueryPreservesChangedFilterBaseline()
    {
        using var world = new World();
        var entity = world.Create(new Position { X = 1, Y = 2 });
        ulong baseline = world.AcquireSystemVersion();
        var query = world.Query(new QueryDescription().WithStored<Position>());
        {
            using QueryScope cursor = query.OpenRead();
            foreach (QueryChunk rowChunk in cursor)
                for (int row = 0; row < rowChunk.Count; row++)
                    Assert.Equal(1, rowChunk.Read<Position>()[row].X);

        }
        Assert.Equal(0, CountChangedRows<Position>(world, baseline));
    }

    [Fact]
    public void MixedQueryChangesOnlyItsWritableComponent()
    {
        using var world = new World();
        var entity = world.Spawn(new PhysicsBundle { Position = new() { X = 1 }, Velocity = new() { X = 10 } });
        ulong baseline = world.AcquireSystemVersion();
        var query = world.Query(new QueryDescription().WithStored<Position>().WithStored<Velocity>());
        {
            using QueryScope cursor = query.Open();
            foreach (QueryChunk rowChunk in cursor)
                for (int row = 0; row < rowChunk.Count; row++)
                    rowChunk.Write<Position>(row).X += rowChunk.Read<Velocity>()[row].X;

        }
        Assert.Equal(11, world.Read<Position>(entity).X);
        Assert.Equal(1, CountChangedRows<Position>(world, baseline));
        Assert.Equal(0, CountChangedRows<Velocity>(world, baseline));
    }

    [Fact]
    public void WithAny_MatchesArchetypeWithAtLeastOne()
    {
        using var world = new World();
        world.Create(new Position { X = 1, Y = 2 });
        world.Create(new Velocity { X = 3, Y = 4 });
        world.Create(new Health { Value = 100 });

        var query = world.Query(
            new QueryDescription().WithAny<Position>().WithAny<Velocity>());

        Assert.Equal(2, CountRows(world, query));
    }

    [Fact]
    public void EnabledAndDisabled_FiltersRows()
    {
        using var world = new World();
        var enabledEntity = world.Create(new VisibilityState { Value = 1 });
        var disabledEntity = world.Create(new VisibilityState { Value = 2 });
        world.Disable<VisibilityState>(disabledEntity);

        var enabledQuery = world.Query(
            new QueryDescription().WithStored<VisibilityState>().With<VisibilityState>());
        var disabledQuery = world.Query(
            new QueryDescription().WithStored<VisibilityState>().WithDisabled<VisibilityState>());

        Assert.Equal([enabledEntity], CollectEntities(world, enabledQuery));
        Assert.Equal([disabledEntity], CollectEntities(world, disabledQuery));
    }

    [Fact]
    public void QueryScope_ChunkRowIndices_FilterRowsWithoutQueryRow()
    {
        using var world = new World();
        var enabledEntity = world.Create(new VisibilityState { Value = 1 });
        var disabledEntity = world.Create(new VisibilityState { Value = 2 });
        world.Disable<VisibilityState>(disabledEntity);

        var query = world.Query(
            new QueryDescription().WithStored<VisibilityState>().With<VisibilityState>());

        int count = 0;
        {
            using QueryScope cursor = query.Open();
            foreach (QueryChunk chunk in cursor)
            {
                for (int row = 0; row < chunk.Count; row++)
                {
                    Assert.Equal(enabledEntity, chunk.Entities[row]);
                    ref VisibilityState state = ref chunk.Write<VisibilityState>(row);
                    state.Value = 9;

                    count++;
                }
            }

        }

        world.Disable<VisibilityState>(enabledEntity);

        Assert.Equal(1, count);
        Assert.Equal(9, world.Read<VisibilityState>(enabledEntity).Value);
        Assert.False(world.IsEnabled<VisibilityState>(enabledEntity));
        Assert.False(world.IsEnabled<VisibilityState>(disabledEntity));
    }

    [Fact]
    public void Query_IsReusable()
    {
        using var world = new World();
        world.Create(new Position { X = 1, Y = 2 });

        var query = world.Query(new QueryDescription().WithStored<Position>());

        Assert.Equal(1, CountRows(world, query));
        Assert.Equal(1, CountRows(world, query));
    }

    [Fact]
    public void ExecuteReadWrite_MultiChunkPlanVisitsEveryRow()
    {
        using var world = new World();
        CreatePositionVelocity(world, 1, 10);

        var query = world.Query(
            new QueryDescription().WithStored<Position>().WithStored<Velocity>());

        for (int i = 0; i < 1000; i++)
            CreatePositionVelocity(world, i + 2, i + 20);

        int count = 0;
        using (QueryScope scope = query.OpenRead())
            foreach (QueryChunk chunk in scope) count += chunk.Count;

        Assert.Equal(1001, count);
    }

    [Fact]
    public void QueryDescription_ProducesReusableRuntimeHandle()
    {
        using var world = new World();
        world.Create(new Position { X = 1, Y = 2 });

        var query = world.Query(
            new QueryDescription().WithStored<Position>().Without<Velocity>());

        Assert.Equal(1, CountRows(world, query));
    }

    private static int CountChangedRows<T>(World world, ulong baseline) where T : struct, IAddedComponent
    {
        var query = world.Query(new QueryDescription().WithChanged<T>(0));
        int count = 0;
        {
            using QueryScope cursor = query.Since(baseline).OpenRead();
            foreach (QueryChunk rowChunk in cursor)
                for (int row = 0; row < rowChunk.Count; row++)
                    count++;

        }
        return count;
    }

    private static int CountRows(World world, Query query) => query.Count;

    private static Entity CreatePositionVelocity(World world, float x, float vx)
    {
        var entity = world.Create(new Position { X = x, Y = x + 1 });
        world.Add(entity, new Velocity { X = vx, Y = vx + 1 });
        return entity;
    }

    private static Entity[] CollectEntities(World world, Query query)
    {
        var entities = new List<Entity>();
        {
            using QueryScope cursor = query.OpenRead();
            foreach (QueryChunk rowChunk in cursor)
                for (int row = 0; row < rowChunk.Count; row++)
                    entities.Add(rowChunk.Entities[row]);

        }
        return entities.ToArray();
    }
}
