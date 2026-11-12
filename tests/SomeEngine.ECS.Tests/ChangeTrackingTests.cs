using SomeEngine.ECS;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Registry;
using Xunit;

namespace SomeEngine.ECS.Tests;

public class ChangeTrackingTests
{
    [Fact]
    public void AcquireSystemTick_ReturnsThenIncrements()
    {
        using var world = new World();
        ulong firstVersion = world.AcquireSystemVersion();
        ulong secondVersion = world.AcquireSystemVersion();
        Assert.Equal(firstVersion + 1, secondVersion);
    }

    [Fact]
    public void AcquireSystemVersion_ReturnsTheNewlyAdvancedWriteVersion()
    {
        using var world = new World();
        ulong baseline = world.Version;

        ulong version = world.AcquireSystemVersion();

        Assert.True(version > baseline);
        Assert.Equal(version, world.Version);
    }

    [Fact]
    public void AutomaticWritableQueryVersionIsNewerThanTheAdmittedPredecessor()
    {
        using var world = new World();
        Entity entity = world.Create(new Position { X = 1 });
        Query query = world.Query(
            new QueryDescription().WithStored<Position>());

        _ = world.AcquireSystemVersion();
        world.Set(entity, new Position { X = 2 });
        ulong predecessorVersion = world.Version;

        {
            using QueryScope cursor = query.Since(version: 0).Open();
            foreach (QueryChunk rowChunk in cursor)
                for (int row = 0; row < rowChunk.Count; row++)
                    rowChunk.Write<Position>(row).X++;

        }

        Assert.Equal(3, world.Read<Position>(entity).X);
        Query changed = world.Query(new QueryDescription().WithChanged<Position>(0));
        Assert.Equal(1, CountRows(world, changed, predecessorVersion, world.Version));
    }

    [Fact]
    public void AutomaticReadOnlyQueryDoesNotAcquireAWriteVersion()
    {
        using var world = new World();
        _ = world.Create(new Position { X = 1 });
        Query query = world.Query(new QueryDescription().WithStored<Position>());
        ulong tickBefore = world.Version;

        {
            using QueryScope cursor = query.Since(version: 0).OpenRead();
            foreach (QueryChunk rowChunk in cursor)
                for (int row = 0; row < rowChunk.Count; row++)
                    _ = rowChunk.Read<Position>()[row];

        }

        Assert.Equal(tickBefore, world.Version);
    }

    [Fact]
    public void ReplacingComponentMakesItVisibleToChangedQuery()
    {
        using var world = new World();
        Entity entity = world.Create(new Position { X = 1 });
        ulong before = world.AcquireSystemVersion();
        world.Set(entity, new Position { X = 99 });
        Query query = world.Query(new QueryDescription().WithChanged<Position>(0));
        Assert.Equal(1, CountRows(world, query, before, world.Version));
        Assert.Equal(99, world.Read<Position>(entity).X);
    }

    [Fact]
    public void AddingComponentMakesItVisibleToChangedQuery()
    {
        using var world = new World();
        Entity entity = world.Create();
        ulong before = world.AcquireSystemVersion();
        world.Add(entity, new Position { X = 7 });
        Query query = world.Query(new QueryDescription().WithChanged<Position>(0));
        Assert.Equal(1, CountRows(world, query, before, world.Version));
        Assert.Equal(7, world.Read<Position>(entity).X);
    }

    [Fact]
    public void Changed_Filter_SkipsUnchangedChunks()
    {
        using var world = new World();
        world.Create(new Position { X = 1, Y = 2 });

        ulong lastVersion = world.AcquireSystemVersion();

        var query = world.Query(
            new QueryDescription().WithStored<Position>().WithChanged<Position>(0));

        int count = 0;
        count = query.Since(lastVersion).Count;

        Assert.Equal(0, count);
    }

    [Fact]
    public void Changed_Filter_IncludesRecentlyModified()
    {
        using var world = new World();
        var entity = world.Create(new Position { X = 1, Y = 2 });

        ulong lastVersion = world.AcquireSystemVersion();
        world.Set(entity, new Position { X = 99, Y = 100 });

        var query = world.Query(
            new QueryDescription().WithStored<Position>().WithChanged<Position>(0));

        int count = 0;
        count = query.Since(lastVersion).Count;

        Assert.Equal(1, count);
    }

    [Fact]
    public void ChangedSameRow()
    {
        using var world = new World();
        var first = world.Create(new Position { X = 1, Y = 2 });
        world.Add(first, new Velocity { X = 1, Y = 1 });
        var second = world.Create(new Position { X = 3, Y = 4 });
        world.Add(second, new Velocity { X = 2, Y = 2 });

        ulong lastVersion = world.AcquireSystemVersion();
        world.Set(first, new Position { X = 9, Y = 9 });
        world.Set(second, new Velocity { X = 8, Y = 8 });

        var query = world.Query(
            new QueryDescription().WithStored<Position>().WithStored<Velocity>().WithChanged<Position>(0).WithChanged<Velocity>(0));

        Assert.Equal(0, CountRows(world, query, lastVersion, world.Version));
        Assert.Equal(0, CountChunks(world, query, lastVersion, world.Version));

        world.Set(first, new Velocity { X = 7, Y = 7 });

        int count = 0;
        {
            using QueryScope cursor = query.Since(lastVersion).OpenRead();
            foreach (QueryChunk rowChunk in cursor)
                for (int row = 0; row < rowChunk.Count; row++)
                {
                    Assert.Equal(first, rowChunk.Entities[row]);
                    count++;
                }

        }

        Assert.Equal(1, count);
        Assert.Equal(1, CountChunks(world, query, lastVersion, world.Version));
    }

    [Fact]
    public void Added_Filter_IncludesRecentlyAdded()
    {
        using var world = new World();
        var entity = world.Create();

        ulong lastVersion = world.AcquireSystemVersion();
        world.Add(entity, new Position { X = 1, Y = 2 });

        var query = world.Query(
            new QueryDescription().WithStored<Position>().WithAdded<Position>(0));

        int count = 0;
        {
            using QueryScope cursor = query.Since(lastVersion).OpenRead();
            foreach (QueryChunk rowChunk in cursor)
                for (int row = 0; row < rowChunk.Count; row++)
                {
                    Assert.Equal(entity, rowChunk.Entities[row]);
                    count++;
                }

        }

        Assert.Equal(1, count);
    }

    [Fact]
    public void ChunkWriteMarksRowsAndChunkChanged()
    {
        using var world = new World();
        world.Create(new Position { X = 1, Y = 2 });

        ulong lastVersion = world.AcquireSystemVersion();
        var writeQuery = world.Query(new QueryDescription().WithStored<Position>());
        {
            using QueryScope cursor = writeQuery.Open();
            foreach (var chunk in cursor)
                chunk.Write<Position>()[0].X = 3;

        }

        var changedQuery = world.Query(
            new QueryDescription().WithStored<Position>().WithChanged<Position>(0));
        var chunkQuery = world.Query(
            new QueryDescription().WithStored<Position>()
                .WithChanged<Position>(0));

        Assert.Equal(1, CountRows(world, changedQuery, lastVersion, world.Version));
        Assert.Equal(1, CountRows(world, chunkQuery, lastVersion, world.Version));
    }

    [Fact]
    public void FilteredChunkSpanContainsMatchingRows()
    {
        using var world = new World();
        var entity = world.Create(new Position { X = 1, Y = 2 });

        ulong lastVersion = world.AcquireSystemVersion();
        world.Set(entity, new Position { X = 3, Y = 4 });

        var query = world.Query(
            new QueryDescription().WithStored<Position>().WithChanged<Position>(0));

        int count = 0;
        {
            using QueryScope cursor = query.Since(lastVersion).OpenRead();
            foreach (var chunk in cursor)
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
                Assert.Null(error);
                foreach (Entity row in chunk.Entities)
                {
                    Assert.Equal(entity, row);
                    count++;
                }
            }

        }

        Assert.Equal(1, count);
    }

    [Fact]
    public void ChunkFiltersAnd()
    {
        using var world = new World();
        var entity = world.Create(new Position { X = 1, Y = 2 });
        world.Add(entity, new Velocity { X = 3, Y = 4 });

        ulong lastVersion = world.AcquireSystemVersion();
        var positionQuery = world.Query(new QueryDescription().WithStored<Position>());
        {
            using QueryScope cursor = positionQuery.Open();
            foreach (var chunk in cursor)
                chunk.Write<Position>()[0].X = 5;

        }

        var query = world.Query(
            new QueryDescription().WithStored<Position>().WithStored<Velocity>()
                .WithChanged<Position>(0)
                .WithChanged<Velocity>(0));

        Assert.Equal(0, CountChunks(world, query, lastVersion, world.Version));

        var velocityQuery = world.Query(new QueryDescription().WithStored<Velocity>());
        {
            using QueryScope cursor = velocityQuery.Open();
            foreach (var chunk in cursor)
                chunk.Write<Velocity>()[0].X = 6;

        }

        Assert.Equal(1, CountChunks(world, query, lastVersion, world.Version));
    }

    [Fact]
    public void Remove_WritesRemovedFact()
    {
        using var world = new World();
        var entity = world.Create(new RecordedPosition { X = 4, Y = 5 });

        world.Remove<RecordedPosition>(entity);

        var query = world.Query(new QueryDescription().WithStored<Removed<RecordedPosition>>());

        int count = 0;
        {
            using QueryScope cursor = query.OpenRead();
            foreach (QueryChunk rowChunk in cursor)
                for (int row = 0; row < rowChunk.Count; row++)
                {
                    var removed = rowChunk.Read<Removed<RecordedPosition>>()[row];
                    Assert.Equal(entity, rowChunk.Entities[row]);
                    Assert.Equal(4, removed.Value.X);
                    Assert.Equal(5, removed.Value.Y);
                    count++;
                }

        }

        Assert.Equal(1, count);
    }

    [Fact]
    public void RemoveReaddRemoveBeforeClear_RefreshesOneRemovedFact()
    {
        using var world = new World();
        Entity entity = world.Create(new RecordedPosition { X = 1, Y = 2 });

        world.Remove<RecordedPosition>(entity);
        world.AcquireSystemVersion();
        world.Add(entity, new RecordedPosition { X = 8, Y = 9 });
        world.AcquireSystemVersion();
        ulong secondRemovalVersion = world.Version;
        world.Remove<RecordedPosition>(entity);

        Removed<RecordedPosition> removed = world.Read<Removed<RecordedPosition>>(entity);
        Assert.Equal(8, removed.Value.X);
        Assert.Equal(9, removed.Value.Y);
        Assert.True(removed.Version > secondRemovalVersion);
        Assert.False(world.Has<RecordedPosition>(entity));
        var query = world.Query(new QueryDescription().WithStored<Removed<RecordedPosition>>());
        int count = 0;
        {
            using QueryScope cursor = query.OpenRead();
            foreach (QueryChunk rowChunk in cursor)
                for (int row = 0; row < rowChunk.Count; row++)
                {
                    Assert.Equal(entity, rowChunk.Entities[row]);
                    count++;
                }

        }
        Assert.Equal(1, count);
    }

    [Fact]
    public void ClearRemoved_ReleasesFactWithoutDestroyingLiveEntity()
    {
        using var world = new World();
        var entity = world.Create(new RecordedPosition { X = 6, Y = 7 });

        world.Remove<RecordedPosition>(entity);
        Assert.True(world.Has<Removed<RecordedPosition>>(entity));
        world.ClearRemoved<RecordedPosition>(world.Version);

        Assert.True(world.IsAlive(entity));
        Assert.False(world.IsPendingCleanup(entity));
        Assert.False(world.Has<Removed<RecordedPosition>>(entity));
        Assert.False(world.Has<RecordedPosition>(entity));
    }

    [Fact]
    public void QueryReadWriteRef_BumpsChangeVersion()
    {
        using var world = new World();
        var entity = world.Create(new Position { X = 1, Y = 2 });

        ulong lastVersion = world.AcquireSystemVersion();
        var writeQuery = world.Query(new QueryDescription().WithStored<Position>());
        {
            using QueryScope cursor = writeQuery.Open();
            foreach (QueryChunk rowChunk in cursor)
                for (int row = 0; row < rowChunk.Count; row++)
                {
                    if (rowChunk.Entities[row] == entity)
                        rowChunk.Write<Position>(row).X = 42;
                }

        }

        var query = world.Query(
            new QueryDescription().WithStored<Position>().WithChanged<Position>(0));

        int count = 0;
        {
            using QueryScope cursor = query.Since(lastVersion).OpenRead();
            foreach (QueryChunk rowChunk in cursor)
                for (int row = 0; row < rowChunk.Count; row++)
                {
                    Assert.Equal(42, rowChunk.Read<Position>()[row].X);
                    count++;
                }

        }

        Assert.Equal(1, count);
    }

    [Fact]
    public async Task ConcurrentChangesRemainVisibleToChangedQuery()
    {
        using var world = new World();
        Entity first = world.Create(new Position { X = 1 });
        Entity second = world.Create(new Position { X = 2 });
        ulong before = world.AcquireSystemVersion();
        await Task.WhenAll(
            Task.Run(() => world.Set(first, new Position { X = 11 })),
            Task.Run(() => world.Set(second, new Position { X = 22 }))).WaitAsync(TimeSpan.FromSeconds(10));
        Query query = world.Query(new QueryDescription().WithChanged<Position>(0));
        Assert.Equal(2, CountRows(world, query, before, world.Version));
        Assert.Equal(11, world.Read<Position>(first).X);
        Assert.Equal(22, world.Read<Position>(second).X);
    }

    private struct RecordedPosition : IAddedComponent, IRemovedComponent
    {
        public float X, Y;
    }

    private static int CountRows(World world, Query query, ulong lastSystemVersion, ulong currentSystemVersion)
        => query.Since(lastSystemVersion).Count;

    private static int CountChunks(World world, Query query, ulong lastSystemVersion, ulong currentSystemVersion)
    {
        int count = 0;
        using QueryScope scope = query.Since(lastSystemVersion).OpenRead();
        foreach (QueryChunk chunk in scope) count++;
        return count;
    }
}
