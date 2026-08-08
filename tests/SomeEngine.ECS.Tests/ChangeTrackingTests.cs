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

        Assert.True(VersionClock.IsNewer(version, baseline));
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
        uint predecessorVersion = RowWriteVersion<Position>(world, entity);

        world.ExecuteQuery(query, lastSystemVersion: 0, cursor =>
        {
            foreach (QueryRow row in cursor.Rows)
                row.ReadWrite<Position>().X++;
        });

        uint queryVersion = RowWriteVersion<Position>(world, entity);
        Assert.True(VersionClock.IsNewer(queryVersion, predecessorVersion));
        Assert.Equal(queryVersion, world.CurrentTick);
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
    public void Set_BumpsChangeVersion()
    {
        var world = new World();
        var entity = world.CreateEntity(new Position { X = 1, Y = 2 });

        var archetype = Assert.Single(
            world.AllArchetypes.ToArray(),
            static candidate => candidate.HasComponent(ComponentMetadata<Position>.Id));
        var chunk = archetype.Chunks[0];
        int column = archetype.Column(ComponentMetadata<Position>.Id);

        uint before = chunk.ChangeVersions[column];
        world.AcquireSystemTick();
        world.Replace(entity, new Position { X = 99, Y = 100 });

        Assert.True(chunk.ChangeVersions[column] > before);
    }

    [Fact]
    public void Add_BumpsChangeVersion()
    {
        var world = new World();
        var entity = world.CreateEntity();
        world.Add(entity, new Position { X = 1, Y = 2 });

        var archetype = Assert.Single(
            world.AllArchetypes.ToArray(),
            static candidate => candidate.HasComponent(ComponentMetadata<Position>.Id));
        var chunk = archetype.Chunks[0];
        int column = archetype.Column(ComponentMetadata<Position>.Id);

        Assert.True(chunk.ChangeVersions[column] > 0);
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
        world.ExecuteQuery(query, lastVersion, world.CurrentTick, ref count, CountRows);

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
        world.ExecuteQuery(query, lastVersion, world.CurrentTick, ref count, CountRows);

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
        world.ExecuteQuery(query, lastVersion, world.CurrentTick, cursor =>
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
        world.ExecuteQuery(query, lastVersion, world.CurrentTick, cursor =>
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
        world.ExecuteQuery(query, lastVersion, world.CurrentTick, cursor =>
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
        Assert.Single(
            world.AllArchetypes.ToArray(),
            archetype => archetype.HasComponent(ComponentMetadata<Removed<Position>>.Id) &&
                         archetype.Chunks.ToArray().Any(chunk =>
                            chunk.Entities[..chunk.Count].Contains(entity)));
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
        world.ExecuteQuery(query, lastVersion, world.CurrentTick, cursor =>
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
    public void VersionIsNewer_WrapAroundHandling()
    {
        Assert.True(VersionClock.IsNewer(1, 0));
        Assert.True(VersionClock.IsNewer(100, 50));
        Assert.False(VersionClock.IsNewer(50, 100));
        Assert.False(VersionClock.IsNewer(0, 0));
        Assert.True(VersionClock.IsNewer(0, uint.MaxValue));
    }

    [Fact]
    public async Task CoarseVersionDoesNotRegressWhenOlderPacketFinishesLast()
    {
        var world = new World();
        _ = world.CreateEntity(new Position { X = 1, Y = 2 });
        _ = world.CreateEntity(new Position { X = 3, Y = 4 });
        var archetype = Assert.Single(
            world.AllArchetypes.ToArray(),
            static candidate => candidate.HasComponent(ComponentMetadata<Position>.Id));
        Assert.Equal(1, archetype.Chunks.Length);
        var chunk = archetype.Chunks[0];
        int column = archetype.Column(ComponentMetadata<Position>.Id);
        using var olderStarted = new ManualResetEventSlim();
        using var newerPublished = new ManualResetEventSlim();

        Task older = Task.Run(() =>
        {
            olderStarted.Set();
            Assert.True(newerPublished.Wait(TimeSpan.FromSeconds(5)));
            chunk.MarkWrite(column, row: 0, version: 101);
        });
        Task newer = Task.Run(() =>
        {
            Assert.True(olderStarted.Wait(TimeSpan.FromSeconds(5)));
            chunk.MarkWrite(column, row: 1, version: 102);
            newerPublished.Set();
        });

        await Task.WhenAll(older, newer).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(101u, chunk.WriteVersionRows(column)[0]);
        Assert.Equal(102u, chunk.WriteVersionRows(column)[1]);
        Assert.Equal(102u, chunk.ChangeVersions[column]);
    }

    [Fact]
    public void CoarseVersionPublicationUsesClockWrapOrdering()
    {
        uint version = uint.MaxValue;

        VersionClock.PublishNewest(ref version, 0);
        VersionClock.PublishNewest(ref version, uint.MaxValue);

        Assert.Equal(0u, version);
    }

    private static uint RowWriteVersion<T>(World world, Entity entity)
        where T : struct, IComponent
    {
        var record = world.ActiveStructureRoot.Entities.ReadRow(entity);
        int column = record.Archetype!.Column(ComponentMetadata<T>.Id);
        return record.Chunk!.WriteVersionRows(column)[record.RowInChunk];
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
            currentSystemVersion,
            ref count,
            CountRows);

        return count;
    }

    private static void CountRows(QueryCursor cursor, ref int count)
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
            currentSystemVersion,
            ref count,
            static (QueryCursor cursor, ref int state) =>
            {
                foreach (var _ in cursor.Chunks)
                    state++;
            });

        return count;
    }
}
