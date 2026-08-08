using SomeEngine.ECS;
using SomeEngine.ECS.Archetypes;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Registry;
using Xunit;

namespace SomeEngine.ECS.Tests;

public struct VisibilityState : SomeEngine.ECS.IEnableableComponent
{
    public int Value;
}

public struct MoveSpeed : SomeEngine.ECS.IComponent
{
    public int Value;
}

public class EnableableComponentTests
{
    [Fact]
    public void Add_EnableableComponent_DefaultsToEnabled()
    {
        var world = new World();
        var entity = world.CreateEntity();

        world.Add(entity, new VisibilityState { Value = 7 });

        Assert.True(world.Has<VisibilityState>(entity));
        Assert.True(world.IsEnabled<VisibilityState>(entity));
        Assert.Equal(7, world.Read<VisibilityState>(entity).Value);
    }

    [Fact]
    public void Disable_AndEnable_DoNotAffectComponentExistence()
    {
        var world = new World();
        var entity = world.CreateEntity(new VisibilityState { Value = 3 });

        world.Disable<VisibilityState>(entity);
        Assert.True(world.Has<VisibilityState>(entity));
        Assert.False(world.IsEnabled<VisibilityState>(entity));
        Assert.Equal(3, world.Read<VisibilityState>(entity).Value);

        world.Enable<VisibilityState>(entity);
        Assert.True(world.IsEnabled<VisibilityState>(entity));
    }

    [Fact]
    public void SwapRemove_PreservesMovedEntityEnableBit()
    {
        var world = new World();
        var entity1 = world.CreateEntity(new VisibilityState { Value = 1 });
        var entity2 = world.CreateEntity(new VisibilityState { Value = 2 });
        var entity3 = world.CreateEntity(new VisibilityState { Value = 3 });

        world.Disable<VisibilityState>(entity3);
        world.DestroyEntity(entity1);

        Assert.True(world.IsAlive(entity2));
        Assert.True(world.IsAlive(entity3));
        Assert.False(world.IsEnabled<VisibilityState>(entity3));
        Assert.True(world.IsEnabled<VisibilityState>(entity2));
    }

    [Fact]
    public void Archetype_WithEnableableColumn_IsCappedTo128EntitiesPerChunk()
    {
        var world = new World();
        world.CreateEntity(new VisibilityState { Value = 1 });

        var archetype = Assert.Single(
            world.AllArchetypes.ToArray(),
            static candidate => candidate.HasComponent(ComponentMetadata<VisibilityState>.Id));

        Assert.Equal(128, archetype.MaxChunkRows);
        Assert.Equal(1, archetype.EnableableComponentIds.Length);
        Assert.Equal(ComponentMetadata<VisibilityState>.Id, archetype.EnableableComponentIds[0]);
    }

    [Fact]
    public void QueryCache_WithEnabledAndWithDisabled_UsesRowLevelEnableState()
    {
        var world = new World();
        var enabledEntity = world.CreateEntity(new VisibilityState { Value = 10 });
        var disabledEntity = world.CreateEntity(new VisibilityState { Value = 20 });
        world.Disable<VisibilityState>(disabledEntity);

        var enabledQuery = world.Query(
            world.QueryDefinition()
                .All<VisibilityState>()
                .Enabled<VisibilityState>());
        var disabledQuery = world.Query(
            world.QueryDefinition()
                .All<VisibilityState>()
                .Disabled<VisibilityState>());

        var enabledMatches = new List<Entity>();
        world.ExecuteQuery(enabledQuery, cursor =>
        {
            foreach (var row in cursor.Rows)
                enabledMatches.Add(row.Entity);
        });

        var disabledMatches = new List<Entity>();
        world.ExecuteQuery(disabledQuery, cursor =>
        {
            foreach (var row in cursor.Rows)
                disabledMatches.Add(row.Entity);
        });

        Assert.Equal([enabledEntity], enabledMatches);
        Assert.Equal([disabledEntity], disabledMatches);
    }

    [Fact]
    public void EnabledQuery_SkipsChunksWithNoEnabledRows()
    {
        var world = new World();
        Entity[] entities = new Entity[128];
        for (int i = 0; i < entities.Length; i++)
            entities[i] = world.CreateEntity(new VisibilityState { Value = i });
        for (int i = 0; i < entities.Length; i++)
            world.Disable<VisibilityState>(entities[i]);

        var query = world.Query(
            world.QueryDefinition()
                .Read<VisibilityState>()
                .Enabled<VisibilityState>());

        int chunks = 0;
        int rows = 0;
        world.ExecuteQuery(query, cursor =>
        {
            foreach (QueryChunkView _ in cursor.Chunks)
                chunks++;
            foreach (QueryRow _ in cursor.Rows)
                rows++;
        });

        Assert.Equal(0, chunks);
        Assert.Equal(0, rows);
    }

    [Fact]
    public void DisabledQuery_SkipsChunksWithNoDisabledRows()
    {
        var world = new World();
        for (int i = 0; i < 128; i++)
            world.CreateEntity(new VisibilityState { Value = i });

        var query = world.Query(
            world.QueryDefinition()
                .Read<VisibilityState>()
                .Disabled<VisibilityState>());

        int chunks = 0;
        int rows = 0;
        world.ExecuteQuery(query, cursor =>
        {
            foreach (QueryChunkView _ in cursor.Chunks)
                chunks++;
            foreach (QueryRow _ in cursor.Rows)
                rows++;
        });

        Assert.Equal(0, chunks);
        Assert.Equal(0, rows);
    }

    [Fact]
    public void EnableableState_SurvivesMigrationForSharedComponents()
    {
        var world = new World();
        var entity = world.Spawn(new VisibilityMoveSpeedBundle
        {
            Visibility = new VisibilityState { Value = 1 },
            MoveSpeed = new MoveSpeed { Value = 5 },
        });

        world.Disable<VisibilityState>(entity);
        world.Add(entity, new Health { Value = 100 });
        world.Remove<MoveSpeed>(entity);

        Assert.False(world.IsEnabled<VisibilityState>(entity));
        Assert.Equal(1, world.Read<VisibilityState>(entity).Value);
        Assert.Equal(100, world.Read<Health>(entity).Value);
    }

    private static int FindRow(Chunk chunk, Entity entity)
    {
        for (int row = 0; row < chunk.Count; row++)
        {
            if (chunk.Entities[row] == entity)
                return row;
        }

        throw new Xunit.Sdk.XunitException($"Entity {entity} was not found in the chunk.");
    }
}
