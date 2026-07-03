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
    public void Set_BumpsChangeVersion()
    {
        var world = new World();
        var entity = world.CreateEntity(new Position { X = 1, Y = 2 });

        var cache = world.CreateQuery().With<Position>().Build();
        var archetype = cache.Archetypes[0];
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

        var cache = world.CreateQuery().With<Position>().Build();
        var archetype = cache.Archetypes[0];
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
        foreach (var _ in world.RunQuery(query, lastVersion, world.CurrentTick).Rows)
            count++;

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
        foreach (var _ in world.RunQuery(query, lastVersion, world.CurrentTick).Rows)
            count++;

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

        Assert.Equal(0, CountRows(world.RunQuery(query, lastVersion, world.CurrentTick)));
        Assert.Equal(0, CountChunks(world.RunQuery(query, lastVersion, world.CurrentTick)));

        world.Replace(first, new Velocity { X = 7, Y = 7 });

        int count = 0;
        foreach (var row in world.RunQuery(query, lastVersion, world.CurrentTick).Rows)
        {
            Assert.Equal(first, row.Entity);
            count++;
        }

        Assert.Equal(1, count);
        Assert.Equal(1, CountChunks(world.RunQuery(query, lastVersion, world.CurrentTick)));
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
        foreach (var row in world.RunQuery(query, lastVersion, world.CurrentTick).Rows)
        {
            Assert.Equal(entity, row.Entity);
            count++;
        }

        Assert.Equal(1, count);
    }

    [Fact]
    public void ChunkWriteMarksRowsAndChunkChanged()
    {
        var world = new World();
        world.CreateEntity(new Position { X = 1, Y = 2 });

        uint lastVersion = world.AcquireSystemTick();
        var writeQuery = world.Query(world.QueryDefinition().ReadWrite<Position>());
        foreach (var chunk in world.RunQuery(writeQuery).Chunks)
            chunk.ReadWrite<Position>()[0].X = 3;

        var changedQuery = world.Query(
            world.QueryDefinition()
                .Read<Position>()
                .Changed<Position>());
        var chunkQuery = world.Query(
            world.QueryDefinition()
                .Read<Position>()
                .ChunkChanged<Position>());

        Assert.Equal(1, CountRows(world.RunQuery(changedQuery, lastVersion, world.CurrentTick)));
        Assert.Equal(1, CountRows(world.RunQuery(chunkQuery, lastVersion, world.CurrentTick)));
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
        foreach (var chunk in world.RunQuery(query, lastVersion, world.CurrentTick).Chunks)
        {
            Assert.Throws<InvalidOperationException>(() => chunk.Read<Position>());
            foreach (var row in chunk.Rows)
            {
                Assert.Equal(entity, row.Entity);
                count++;
            }
        }

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
        foreach (var chunk in world.RunQuery(positionQuery).Chunks)
            chunk.ReadWrite<Position>()[0].X = 5;

        var query = world.Query(
            world.QueryDefinition()
                .Read<Position>()
                .Read<Velocity>()
                .ChunkChanged<Position>()
                .ChunkChanged<Velocity>());

        Assert.Equal(0, CountChunks(world.RunQuery(query, lastVersion, world.CurrentTick)));

        var velocityQuery = world.Query(world.QueryDefinition().ReadWrite<Velocity>());
        foreach (var chunk in world.RunQuery(velocityQuery).Chunks)
            chunk.ReadWrite<Velocity>()[0].X = 6;

        Assert.Equal(1, CountChunks(world.RunQuery(query, lastVersion, world.CurrentTick)));
    }

    [Fact]
    public void Remove_WritesRemovedFact()
    {
        var world = new World();
        var entity = world.CreateEntity(new Position { X = 4, Y = 5 });

        world.Remove<Position>(entity);

        var query = world.Query(world.QueryDefinition().Removed<Position>());

        int count = 0;
        foreach (var row in world.RunQuery(query).Rows)
        {
            var removed = row.Read<Removed<Position>>();
            Assert.Equal(entity, row.Entity);
            Assert.Equal(4, removed.Value.X);
            Assert.Equal(5, removed.Value.Y);
            count++;
        }

        Assert.Equal(1, count);
    }

    [Fact]
    public void ClearRemoved_Releases()
    {
        var world = new World();
        var entity = world.CreateEntity(new Position { X = 6, Y = 7 });

        world.Remove<Position>(entity);
        world.ClearRemoved<Position>(world.CurrentTick);

        Assert.False(world.IsAlive(entity));
    }

    [Fact]
    public void MutableRefGet_BumpsChangeVersion()
    {
        var world = new World();
        var entity = world.CreateEntity(new Position { X = 1, Y = 2 });

        uint lastVersion = world.AcquireSystemTick();
        ref var position = ref world.Get<Position>(entity);
        position.X = 42;

        var query = world.Query(
            world.QueryDefinition()
                .Read<Position>()
                .Changed<Position>());

        int count = 0;
        foreach (var row in world.RunQuery(query, lastVersion, world.CurrentTick).Rows)
        {
            Assert.Equal(42, row.Read<Position>().X);
            count++;
        }

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

    private static int CountRows(QueryCursor run)
    {
        int count = 0;
        foreach (var _ in run.Rows)
            count++;

        return count;
    }

    private static int CountChunks(QueryCursor run)
    {
        int count = 0;
        foreach (var _ in run.Chunks)
            count++;

        return count;
    }
}
