using SomeEngine.ECS;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Registry;
using Xunit;

namespace SomeEngine.ECS.Tests;

public struct VisibilityState : SomeEngine.ECS.IEnableableComponent, IAddedComponent
{
    public int Value;
}

public struct MoveSpeed : SomeEngine.ECS.IAddedComponent
{
    public int Value;
}

public class EnableableComponentTests
{
    [Fact]
    public void Add_EnableableComponent_DefaultsToEnabled()
    {
        var world = new World();
        var entity = world.Create();

        world.Add(entity, new VisibilityState { Value = 7 });

        Assert.True(world.Has<VisibilityState>(entity));
        Assert.True(world.IsEnabled<VisibilityState>(entity));
        Assert.Equal(7, world.Read<VisibilityState>(entity).Value);
    }

    [Fact]
    public void Disable_AndEnable_DoNotAffectComponentExistence()
    {
        var world = new World();
        var entity = world.Create(new VisibilityState { Value = 3 });

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
        var entity1 = world.Create(new VisibilityState { Value = 1 });
        var entity2 = world.Create(new VisibilityState { Value = 2 });
        var entity3 = world.Create(new VisibilityState { Value = 3 });

        world.Disable<VisibilityState>(entity3);
        world.Destroy(entity1);

        Assert.True(world.IsAlive(entity2));
        Assert.True(world.IsAlive(entity3));
        Assert.False(world.IsEnabled<VisibilityState>(entity3));
        Assert.True(world.IsEnabled<VisibilityState>(entity2));
    }

    [Fact]
    public void EnabledQuerySelectsEnabledEntitiesAcrossLargePopulation()
    {
        using var world = new World();
        var expected = new List<Entity>();
        for (int i = 0; i < 513; i++)
        {
            var entity = world.Create(new VisibilityState { Value = i });
            if (i % 2 == 0)
                world.Disable<VisibilityState>(entity);
            else
                expected.Add(entity);
        }
        var query = world.Query(new QueryDescription().With<VisibilityState>());
        var actual = new List<Entity>();
        {
            using QueryScope cursor = (query).OpenRead();
            foreach (QueryChunk rowChunk in cursor)
                for (int row = 0; row < rowChunk.Count; row++)
                actual.Add(rowChunk.Entities[row]);

        };
        Assert.Equal(expected.OrderBy(entity => entity.Id), actual.OrderBy(entity => entity.Id));
    }

    [Fact]
    public void QueryCache_WithEnabledAndWithDisabled_UsesRowLevelEnableState()
    {
        var world = new World();
        var enabledEntity = world.Create(new VisibilityState { Value = 10 });
        var disabledEntity = world.Create(new VisibilityState { Value = 20 });
        world.Disable<VisibilityState>(disabledEntity);

        var enabledQuery = world.Query(
            new QueryDescription().WithStored<VisibilityState>().With<VisibilityState>());
        var disabledQuery = world.Query(
            new QueryDescription().WithStored<VisibilityState>().WithDisabled<VisibilityState>());

        var enabledMatches = new List<Entity>();
        {
            using QueryScope cursor = (enabledQuery).OpenRead();
            foreach (QueryChunk rowChunk in cursor)
                for (int row = 0; row < rowChunk.Count; row++)
                enabledMatches.Add(rowChunk.Entities[row]);

        };

        var disabledMatches = new List<Entity>();
        {
            using QueryScope cursor = (disabledQuery).OpenRead();
            foreach (QueryChunk rowChunk in cursor)
                for (int row = 0; row < rowChunk.Count; row++)
                disabledMatches.Add(rowChunk.Entities[row]);

        };

        Assert.Equal([enabledEntity], enabledMatches);
        Assert.Equal([disabledEntity], disabledMatches);
    }

    [Fact]
    public void EnabledQuery_SkipsChunksWithNoEnabledRows()
    {
        var world = new World();
        Entity[] entities = new Entity[128];
        for (int i = 0; i < entities.Length; i++)
            entities[i] = world.Create(new VisibilityState { Value = i });
        for (int i = 0; i < entities.Length; i++)
            world.Disable<VisibilityState>(entities[i]);

        var query = world.Query(
            new QueryDescription().WithStored<VisibilityState>().With<VisibilityState>());

        int chunks = 0;
        int rows = 0;
        {
            using QueryScope cursor = (query).OpenRead();
            foreach (QueryChunk _ in cursor)
                chunks++;
            foreach (QueryChunk _Chunk in cursor)
                for (int _ = 0; _ < _Chunk.Count; _++)
                rows++;

        };

        Assert.Equal(0, chunks);
        Assert.Equal(0, rows);
    }

    [Fact]
    public void DisabledQuery_SkipsChunksWithNoDisabledRows()
    {
        var world = new World();
        for (int i = 0; i < 128; i++)
            world.Create(new VisibilityState { Value = i });

        var query = world.Query(
            new QueryDescription().WithStored<VisibilityState>().WithDisabled<VisibilityState>());

        int chunks = 0;
        int rows = 0;
        {
            using QueryScope cursor = (query).OpenRead();
            foreach (QueryChunk _ in cursor)
                chunks++;
            foreach (QueryChunk _Chunk in cursor)
                for (int _ = 0; _ < _Chunk.Count; _++)
                rows++;

        };

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

}
