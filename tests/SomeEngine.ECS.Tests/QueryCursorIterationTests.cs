using SomeEngine.ECS;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Registry;
using Xunit;

namespace SomeEngine.ECS.Tests;

public class QueryCursorIterationTests
{
    [Fact]
    public void QueryCursor_Rows_ReadMatchingData()
    {
        var world = new World();
        world.CreateEntity(new Position { X = 1, Y = 2 });
        world.CreateEntity(new Position { X = 3, Y = 4 });
        world.CreateEntity(new Velocity { X = 100, Y = 200 });

        var query = world.Query(world.QueryDefinition().Read<Position>());

        int count = 0;
        float sumX = 0;
        world.ExecuteQuery(query, cursor =>
        {
            foreach (var row in cursor.Rows)
            {
                count++;
                sumX += row.Read<Position>().X;
            }
        });

        Assert.Equal(2, count);
        Assert.Equal(4f, sumX);
    }

    [Fact]
    public void QueryCursor_Chunks_ReadWriteCanMutate()
    {
        var world = new World();
        var entity = world.CreateEntity(new Position { X = 1, Y = 2 });

        var query = world.Query(world.QueryDefinition().ReadWrite<Position>());

        world.ExecuteQuery(query, cursor =>
        {
            foreach (var chunk in cursor.Chunks)
                chunk.ReadWrite<Position>()[0].X = 99;
        });

        Assert.Equal(99f, world.Read<Position>(entity).X);
    }

    [Fact]
    public void QueryCursor_ChunkRows_PreserveRunVersions()
    {
        var world = new World();
        world.CreateEntity(new Position { X = 1, Y = 2 });

        var query = world.Query(world.QueryDefinition().Read<Position>());
        const uint last = 7;
        const uint current = 11;

        world.ExecuteQuery(query, last, current, cursor =>
        {
            foreach (var chunk in cursor.Chunks)
            {
                foreach (var row in chunk.Rows)
                {
                    Assert.Equal(last, row.LastSystemVersion);
                    Assert.Equal(current, row.CurrentSystemVersion);
                }
            }
        });
    }

    [Fact]
    public void QueryCursor_ChunkEarlyBreak_ReleasesGuard()
    {
        var world = new World();
        world.CreateEntity(new Position { X = 1, Y = 2 });

        var query = world.Query(world.QueryDefinition().Read<Position>());
        world.ExecuteQuery(query, cursor =>
        {
            foreach (var _ in cursor.Chunks)
                break;
        });

        world.Add(world.CreateEntity(), new Velocity { X = 1, Y = 2 });
    }

    [Fact]
    public void QueryCursor_Rows_ReadsMultipleComponents()
    {
        var world = new World();
        world.Spawn(new PhysicsBundle { Position = new() { X = 1 }, Velocity = new() { X = 5 } });
        world.Spawn(new PhysicsBundle { Position = new() { X = 3 }, Velocity = new() { X = 7 } });

        var query = world.Query(
            world.QueryDefinition()
                .Read<Position>()
                .Read<Velocity>());

        float sumVx = 0;
        world.ExecuteQuery(query, cursor =>
        {
            foreach (var row in cursor.Rows)
                sumVx += row.Read<Velocity>().X;
        });

        Assert.Equal(12f, sumVx);
    }

    [Fact]
    public void ReadWrite_Bumps_ChangeVersion()
    {
        var world = new World();
        world.CreateEntity(new Position { X = 1, Y = 2 });

        var query = world.Query(world.QueryDefinition().ReadWrite<Position>());
        var archetype = Assert.Single(
            world.AllArchetypes.ToArray(),
            static candidate => candidate.HasComponent(ComponentMetadata<Position>.Id));
        var chunk = archetype.Chunks[0];
        int column = archetype.Column(ComponentMetadata<Position>.Id);

        uint before = chunk.ChangeVersions[column];
        world.ExecuteQuery(query, cursor =>
        {
            foreach (var row in cursor.Rows)
                row.ReadWrite<Position>().X += 1;
        });

        Assert.True(chunk.ChangeVersions[column] > before);
    }

    [Fact]
    public void Read_DoesNotBump_ChangeVersion()
    {
        var world = new World();
        world.CreateEntity(new Position { X = 1, Y = 2 });

        var query = world.Query(world.QueryDefinition().Read<Position>());
        var archetype = Assert.Single(
            world.AllArchetypes.ToArray(),
            static candidate => candidate.HasComponent(ComponentMetadata<Position>.Id));
        var chunk = archetype.Chunks[0];
        int column = archetype.Column(ComponentMetadata<Position>.Id);

        uint before = chunk.ChangeVersions[column];
        world.ExecuteQuery(query, cursor =>
        {
            foreach (var row in cursor.Rows)
                _ = row.Read<Position>();
        });

        Assert.Equal(before, chunk.ChangeVersions[column]);
    }

    [Fact]
    public void ReadWriteRead_SelectiveBump()
    {
        var world = new World();
        world.Spawn(new PhysicsBundle { Position = new() { X = 1 }, Velocity = new() { X = 10 } });

        var query = world.Query(
            world.QueryDefinition()
                .ReadWrite<Position>()
                .Read<Velocity>());

        var archetype = Assert.Single(
            world.AllArchetypes.ToArray(),
            static candidate =>
                candidate.HasComponent(ComponentMetadata<Position>.Id) &&
                candidate.HasComponent(ComponentMetadata<Velocity>.Id));
        var chunk = archetype.Chunks[0];
        int posColumn = archetype.Column(ComponentMetadata<Position>.Id);
        int velColumn = archetype.Column(ComponentMetadata<Velocity>.Id);

        uint posBefore = chunk.ChangeVersions[posColumn];
        uint velBefore = chunk.ChangeVersions[velColumn];

        world.ExecuteQuery(query, cursor =>
        {
            foreach (var row in cursor.Rows)
                row.ReadWrite<Position>().X += row.Read<Velocity>().X;
        });

        Assert.True(chunk.ChangeVersions[posColumn] > posBefore);
        Assert.Equal(velBefore, chunk.ChangeVersions[velColumn]);
    }

    [Fact]
    public void WithAny_MatchesArchetypeWithAtLeastOne()
    {
        var world = new World();
        world.CreateEntity(new Position { X = 1, Y = 2 });
        world.CreateEntity(new Velocity { X = 3, Y = 4 });
        world.CreateEntity(new Health { Value = 100 });

        var query = world.Query(
            world.QueryDefinition()
                .Any<Position>()
                .Any<Velocity>());

        Assert.Equal(2, CountRows(world, query));
    }

    [Fact]
    public void EnabledAndDisabled_FiltersRows()
    {
        var world = new World();
        var enabledEntity = world.CreateEntity(new VisibilityState { Value = 1 });
        var disabledEntity = world.CreateEntity(new VisibilityState { Value = 2 });
        world.Disable<VisibilityState>(disabledEntity);

        var enabledQuery = world.Query(
            world.QueryDefinition()
                .Read<VisibilityState>()
                .Enabled<VisibilityState>());
        var disabledQuery = world.Query(
            world.QueryDefinition()
                .Read<VisibilityState>()
                .Disabled<VisibilityState>());

        Assert.Equal([enabledEntity], CollectEntities(world, enabledQuery));
        Assert.Equal([disabledEntity], CollectEntities(world, disabledQuery));
    }

    [Fact]
    public void QueryCursor_ChunkRowIndices_FilterRowsWithoutQueryRow()
    {
        var world = new World();
        var enabledEntity = world.CreateEntity(new VisibilityState { Value = 1 });
        var disabledEntity = world.CreateEntity(new VisibilityState { Value = 2 });
        world.Disable<VisibilityState>(disabledEntity);

        var query = world.Query(
            world.QueryDefinition()
                .ReadWrite<VisibilityState>()
                .Enabled<VisibilityState>());

        int count = 0;
        world.ExecuteQuery(query, cursor =>
        {
            foreach (QueryChunkView chunk in cursor.Chunks)
            {
                foreach (int row in chunk.RowIndices)
                {
                    Assert.Equal(enabledEntity, chunk.GetEntity(row));
                    ref VisibilityState state = ref chunk.ReadWrite<VisibilityState>(row);
                    state.Value = 9;
                    chunk.SetComponentEnabled<VisibilityState>(row, enabled: false);
                    count++;
                }
            }
        });

        Assert.Equal(1, count);
        Assert.Equal(9, world.Read<VisibilityState>(enabledEntity).Value);
        Assert.False(world.IsEnabled<VisibilityState>(enabledEntity));
        Assert.False(world.IsEnabled<VisibilityState>(disabledEntity));
    }

    [Fact]
    public void QueryHandle_IsReusable()
    {
        var world = new World();
        world.CreateEntity(new Position { X = 1, Y = 2 });

        var query = world.Query(world.QueryDefinition().Read<Position>());

        Assert.Equal(1, CountRows(world, query));
        Assert.Equal(1, CountRows(world, query));
    }

    [Fact]
    public void ExecuteReadWrite_UsesUpdatedTypedPlanAfterNewMatchingArchetype()
    {
        var world = new World();
        var query = world.Query(
            world.QueryDefinition()
                .ReadWrite<Position>()
                .Read<Velocity>());

        var first = CreatePositionVelocity(world, 1, 10);

        Assert.Equal(1, AddVelocityToPosition(world, query));

        var second = CreatePositionVelocity(world, 2, 20);
        world.AddTag<PlayerTag>(second);

        Assert.Equal(2, AddVelocityToPosition(world, query));
        Assert.Equal(21f, world.Read<Position>(first).X);
        Assert.Equal(22f, world.Read<Position>(second).X);
    }

    [Fact]
    public void ExecuteReadWrite_MultiChunkPlanVisitsEveryRow()
    {
        var world = new World();
        CreatePositionVelocity(world, 1, 10);

        var query = world.Query(
            world.QueryDefinition()
                .ReadWrite<Position>()
                .Read<Velocity>());

        for (int i = 0; i < 1000; i++)
            CreatePositionVelocity(world, i + 2, i + 20);

        int count = 0;
        world.ExecuteReadWrite<Position, Velocity, int>(
            query,
            ref count,
            static (QueryPairEnumerator<Position, Velocity> chunks, ref int state) =>
            {
                foreach (var chunk in chunks)
                    state += chunk.Count;
            });

        Assert.Equal(1001, count);
    }

    [Fact]
    public void QueryDefinitionBuilder_ProducesReusableRuntimeHandle()
    {
        var world = new World();
        world.CreateEntity(new Position { X = 1, Y = 2 });

        var query = world.Query(
            world.QueryDefinition()
                .All<Position>()
                .None<Velocity>());

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

    private static Entity CreatePositionVelocity(World world, float x, float vx)
    {
        var entity = world.CreateEntity(new Position { X = x, Y = x + 1 });
        world.Add(entity, new Velocity { X = vx, Y = vx + 1 });
        return entity;
    }

    private static int AddVelocityToPosition(World world, QueryHandle query)
    {
        int count = 0;
        world.ExecuteReadWrite<Position, Velocity, int>(
            query,
            ref count,
            static (QueryPairEnumerator<Position, Velocity> chunks, ref int state) =>
            {
                foreach (var chunk in chunks)
                {
                    var positions = chunk.Write;
                    var velocities = chunk.Read;
                    for (int i = 0; i < positions.Length; i++)
                    {
                        positions[i].X += velocities[i].X;
                        state++;
                    }
                }
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
