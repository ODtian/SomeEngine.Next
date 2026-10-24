using SomeEngine.ECS;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Registry;
using Xunit;

namespace SomeEngine.ECS.Tests;

public class ChangeTrackingTests
{
    [Fact]
    public void AcquireSystemTick_ReturnsThenIncrements()
    {
        var world = new World();
        uint firstVersion = world.AcquireSystemTick();
        uint secondVersion = world.AcquireSystemTick();
        Assert.Equal(firstVersion + 1, secondVersion);
    }

    [Fact]
    public void AcquireSystemVersion_ReturnsTheNewlyAdvancedWriteVersion()
    {
        var world = new World();
        uint baseline = world.CurrentTick;

        uint version = world.AcquireSystemVersion();

        Assert.True(version > baseline);
        Assert.Equal(version, world.CurrentTick);
    }

    [Fact]
    public void AutomaticWritableQueryVersionIsNewerThanTheAdmittedPredecessor()
    {
        var world = new World();
        Entity entity = world.CreateEntity(new Position { X = 1 });
        QueryHandle query = world.Query(
            world.QueryDefinition().ReadWrite<Position>());

        _ = world.AcquireSystemVersion();
        world.Replace(entity, new Position { X = 2 });
        uint predecessorVersion = world.CurrentTick;

        world.ExecuteQuery(query, lastSystemVersion: 0, cursor =>
        {
            foreach (QueryRow row in cursor.Rows)
                row.ReadWrite<Position>().X++;
        });

        Assert.Equal(3, world.Read<Position>(entity).X);
        QueryHandle changed = world.Query(world.QueryDefinition().Changed<Position>());
        Assert.Equal(1, CountRows(world, changed, predecessorVersion, world.CurrentTick));
    }

    [Fact]
    public void AutomaticReadOnlyQueryDoesNotAcquireAWriteVersion()
    {
        var world = new World();
        _ = world.CreateEntity(new Position { X = 1 });
        QueryHandle query = world.Query(world.QueryDefinition().Read<Position>());
        uint tickBefore = world.CurrentTick;

        world.ExecuteQuery(query, lastSystemVersion: 0, cursor =>
        {
            foreach (QueryRow row in cursor.Rows)
                _ = row.Read<Position>();
        });

        Assert.Equal(tickBefore, world.CurrentTick);
    }

    [Fact]
    public void ReplacingComponentMakesItVisibleToChangedQuery()
    {
        var world = new World();
        Entity entity = world.CreateEntity(new Position { X = 1 });
        uint before = world.AcquireSystemTick();
        world.Replace(entity, new Position { X = 99 });
        QueryHandle query = world.Query(world.QueryDefinition().Changed<Position>());
        Assert.Equal(1, CountRows(world, query, before, world.CurrentTick));
        Assert.Equal(99, world.Read<Position>(entity).X);
    }

    [Fact]
    public void AddingComponentMakesItVisibleToChangedQuery()
    {
        var world = new World();
        Entity entity = world.CreateEntity();
        uint before = world.AcquireSystemTick();
        world.Add(entity, new Position { X = 7 });
        QueryHandle query = world.Query(world.QueryDefinition().Changed<Position>());
        Assert.Equal(1, CountRows(world, query, before, world.CurrentTick));
        Assert.Equal(7, world.Read<Position>(entity).X);
    }

    [Fact]
    public void Changed_Filter_SkipsUnchangedChunks()
    {
        var world = new World();
        world.CreateEntity(new Position { X = 1, Y = 2 });

        uint lastVersion = world.AcquireSystemTick();

        var query = world.Query(
            world.QueryDefinition()
                .Read<Position>()
                .Changed<Position>());

        int count = 0;
        world.ExecuteQuery(query, lastVersion, ref count, CountRows);

        Assert.Equal(0, count);
    }

    [Fact]
    public void Changed_Filter_IncludesRecentlyModified()
    {
        var world = new World();
        var entity = world.CreateEntity(new Position { X = 1, Y = 2 });

        uint lastVersion = world.AcquireSystemTick();
        world.Replace(entity, new Position { X = 99, Y = 100 });

        var query = world.Query(
            world.QueryDefinition()
                .Read<Position>()
                .Changed<Position>());

        int count = 0;
        world.ExecuteQuery(query, lastVersion, ref count, CountRows);

        Assert.Equal(1, count);
    }

    [Fact]
    public void ChangedSameRow()
    {
        var world = new World();
        var first = world.CreateEntity(new Position { X = 1, Y = 2 });
        world.Add(first, new Velocity { X = 1, Y = 1 });
        var second = world.CreateEntity(new Position { X = 3, Y = 4 });
        world.Add(second, new Velocity { X = 2, Y = 2 });

        uint lastVersion = world.AcquireSystemTick();
        world.Replace(first, new Position { X = 9, Y = 9 });
        world.Replace(second, new Velocity { X = 8, Y = 8 });

        var query = world.Query(
            world.QueryDefinition()
                .Read<Position>()
                .Read<Velocity>()
                .Changed<Position>()
                .Changed<Velocity>());

        Assert.Equal(0, CountRows(world, query, lastVersion, world.CurrentTick));
        Assert.Equal(0, CountChunks(world, query, lastVersion, world.CurrentTick));

        world.Replace(first, new Velocity { X = 7, Y = 7 });

        int count = 0;
        world.ExecuteQuery(query, lastVersion, cursor =>
        {
            foreach (var row in cursor.Rows)
            {
                Assert.Equal(first, row.Entity);
                count++;
            }
        });

        Assert.Equal(1, count);
        Assert.Equal(1, CountChunks(world, query, lastVersion, world.CurrentTick));
    }

    [Fact]
    public void Added_Filter_IncludesRecentlyAdded()
    {
        var world = new World();
        var entity = world.CreateEntity();

        uint lastVersion = world.AcquireSystemTick();
        world.Add(entity, new Position { X = 1, Y = 2 });

        var query = world.Query(
            world.QueryDefinition()
                .Read<Position>()
                .Added<Position>());

        int count = 0;
        world.ExecuteQuery(query, lastVersion, cursor =>
        {
            foreach (var row in cursor.Rows)
            {
                Assert.Equal(entity, row.Entity);
                count++;
            }
        });

        Assert.Equal(1, count);
    }

    [Fact]
    public void ChunkWriteMarksRowsAndChunkChanged()
    {
        var world = new World();
        world.CreateEntity(new Position { X = 1, Y = 2 });

        uint lastVersion = world.AcquireSystemTick();
        var writeQuery = world.Query(world.QueryDefinition().ReadWrite<Position>());
        world.ExecuteQuery(writeQuery, cursor =>
        {
            foreach (var chunk in cursor.Chunks)
                chunk.ReadWrite<Position>()[0].X = 3;
        });

        var changedQuery = world.Query(
            world.QueryDefinition()
                .Read<Position>()
                .Changed<Position>());
        var chunkQuery = world.Query(
            world.QueryDefinition()
                .Read<Position>()
                .ChunkChanged<Position>());

        Assert.Equal(1, CountRows(world, changedQuery, lastVersion, world.CurrentTick));
        Assert.Equal(1, CountRows(world, chunkQuery, lastVersion, world.CurrentTick));
    }

    [Fact]
    public void ChunkSpanRejectsRowFilter()
    {
        var world = new World();
        var entity = world.CreateEntity(new Position { X = 1, Y = 2 });

        uint lastVersion = world.AcquireSystemTick();
        world.Replace(entity, new Position { X = 3, Y = 4 });

        var query = world.Query(
            world.QueryDefinition()
                .Read<Position>()
                .Changed<Position>());

        int count = 0;
        world.ExecuteQuery(query, lastVersion, cursor =>
        {
            foreach (var chunk in cursor.Chunks)
            {
                InvalidOperationException? error = null;
                try
                {
                    _ = chunk.Read<Position>();
                }
                catch (InvalidOperationException exception)
                {
                    error = exception;
                }
                Assert.NotNull(error);
                foreach (var row in chunk.Rows)
                {
                    Assert.Equal(entity, row.Entity);
                    count++;
                }
            }
        });

        Assert.Equal(1, count);
    }

    [Fact]
    public void ChunkFiltersAnd()
    {
        var world = new World();
        var entity = world.CreateEntity(new Position { X = 1, Y = 2 });
        world.Add(entity, new Velocity { X = 3, Y = 4 });

        uint lastVersion = world.AcquireSystemTick();
        var positionQuery = world.Query(world.QueryDefinition().ReadWrite<Position>());
        world.ExecuteQuery(positionQuery, cursor =>
        {
            foreach (var chunk in cursor.Chunks)
                chunk.ReadWrite<Position>()[0].X = 5;
        });

        var query = world.Query(
            world.QueryDefinition()
                .Read<Position>()
                .Read<Velocity>()
                .ChunkChanged<Position>()
                .ChunkChanged<Velocity>());

        Assert.Equal(0, CountChunks(world, query, lastVersion, world.CurrentTick));

        var velocityQuery = world.Query(world.QueryDefinition().ReadWrite<Velocity>());
        world.ExecuteQuery(velocityQuery, cursor =>
        {
            foreach (var chunk in cursor.Chunks)
                chunk.ReadWrite<Velocity>()[0].X = 6;
        });

        Assert.Equal(1, CountChunks(world, query, lastVersion, world.CurrentTick));
    }

    [Fact]
    public void Remove_WritesRemovedFact()
    {
        var world = new World();
        var entity = world.CreateEntity(new Position { X = 4, Y = 5 });

        world.Remove<Position>(entity);

        var query = world.Query(world.QueryDefinition().Removed<Position>());

        int count = 0;
        world.ExecuteQuery(query, cursor =>
        {
            foreach (var row in cursor.Rows)
            {
                var removed = row.Read<Removed<Position>>();
                Assert.Equal(entity, row.Entity);
                Assert.Equal(4, removed.Value.X);
                Assert.Equal(5, removed.Value.Y);
                count++;
            }
        });

        Assert.Equal(1, count);
    }

    [Fact]
    public void RemoveReaddRemoveBeforeClear_RefreshesOneRemovedFact()
    {
        var world = new World();
        Entity entity = world.CreateEntity(new Position { X = 1, Y = 2 });

        world.Remove<Position>(entity);
        world.AcquireSystemTick();
        world.Add(entity, new Position { X = 8, Y = 9 });
        world.AcquireSystemTick();
        uint secondRemovalVersion = world.CurrentTick;
        world.Remove<Position>(entity);

        Removed<Position> removed = world.Read<Removed<Position>>(entity);
        Assert.Equal(8, removed.Value.X);
        Assert.Equal(9, removed.Value.Y);
        Assert.Equal(secondRemovalVersion, removed.Version);
        Assert.False(world.Has<Position>(entity));
        var query = world.Query(world.QueryDefinition().Read<Removed<Position>>());
        int count = 0;
        world.ExecuteQuery(query, cursor =>
        {
            foreach (var row in cursor.Rows)
            {
                Assert.Equal(entity, row.Entity);
                count++;
            }
        });
        Assert.Equal(1, count);
    }

    [Fact]
    public void ClearRemoved_ReleasesFactWithoutDestroyingLiveEntity()
    {
        var world = new World();
        var entity = world.CreateEntity(new Position { X = 6, Y = 7 });

        world.Remove<Position>(entity);
        world.ClearRemoved<Position>(world.CurrentTick);

        Assert.True(world.IsAlive(entity));
        Assert.False(world.IsPendingCleanup(entity));
        Assert.False(world.Has<Removed<Position>>(entity));
        Assert.False(world.Has<Position>(entity));
    }

    [Fact]
    public void QueryReadWriteRef_BumpsChangeVersion()
    {
        var world = new World();
        var entity = world.CreateEntity(new Position { X = 1, Y = 2 });

        uint lastVersion = world.AcquireSystemTick();
        var writeQuery = world.Query(world.QueryDefinition().ReadWrite<Position>());
        world.ExecuteQuery(writeQuery, cursor =>
        {
            foreach (var row in cursor.Rows)
            {
                if (row.Entity == entity)
                    row.ReadWrite<Position>().X = 42;
            }
        });

        var query = world.Query(
            world.QueryDefinition()
                .Read<Position>()
                .Changed<Position>());

        int count = 0;
        world.ExecuteQuery(query, lastVersion, cursor =>
        {
            foreach (var row in cursor.Rows)
            {
                Assert.Equal(42, row.Read<Position>().X);
                count++;
            }
        });

        Assert.Equal(1, count);
    }

    [Fact]
    public async Task ConcurrentChangesRemainVisibleToChangedQuery()
    {
        var world = new World();
        Entity first = world.CreateEntity(new Position { X = 1 });
        Entity second = world.CreateEntity(new Position { X = 2 });
        uint before = world.AcquireSystemTick();
        await Task.WhenAll(
            Task.Run(() => world.Replace(first, new Position { X = 11 })),
            Task.Run(() => world.Replace(second, new Position { X = 22 }))).WaitAsync(TimeSpan.FromSeconds(10));
        QueryHandle query = world.Query(world.QueryDefinition().Changed<Position>());
        Assert.Equal(2, CountRows(world, query, before, world.CurrentTick));
        Assert.Equal(11, world.Read<Position>(first).X);
        Assert.Equal(22, world.Read<Position>(second).X);
    }

    private static int CountRows(
        World world,
        QueryHandle query,
        uint lastSystemVersion,
        uint currentSystemVersion)
    {
        int count = 0;
        world.ExecuteQuery(
            query,
            lastSystemVersion,
            ref count,
            CountRows);

        return count;
    }

    private static void CountRows(scoped QueryCursor cursor, ref int count)
    {
        foreach (var _ in cursor.Rows)
            count++;
    }

    private static int CountChunks(
        World world,
        QueryHandle query,
        uint lastSystemVersion,
        uint currentSystemVersion)
    {
        int count = 0;
        world.ExecuteQuery(
            query,
            lastSystemVersion,
            ref count,
            static (scoped QueryCursor cursor, ref int state) =>
            {
                foreach (var _ in cursor.Chunks)
                    state++;
            });

        return count;
    }
}
