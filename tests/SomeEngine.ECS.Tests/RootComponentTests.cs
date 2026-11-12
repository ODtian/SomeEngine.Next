using SomeEngine.ECS.Commands;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Registry;
using SomeEngine.ECS.Relations;

namespace SomeEngine.ECS.Tests;

public sealed class RootComponentTests
{
    [Fact]
    public void RootOnlyTableComponentTraversesWorldQueryCommandsAndRelations()
    {
        using var world = new World();
        Entity entity = world.Create(new RootOnlyValue { Value = 1 });

        Assert.True(world.Has<RootOnlyValue>(entity));
        Assert.Equal(1, world.Read<RootOnlyValue>(entity).Value);

        world.Set(entity, new RootOnlyValue { Value = 2 });
        Query query = world.Query(
            new QueryDescription().WithStored<RootOnlyValue>());
        {
            using QueryScope cursor = (query).Open();
            foreach (QueryChunk rowChunk in cursor)
                for (int row = 0; row < rowChunk.Count; row++)
                rowChunk.Write<RootOnlyValue>(row).Value++;

        };
        Assert.Equal(3, world.Read<RootOnlyValue>(entity).Value);

        using (var commands = new CommandBuffer(world))
        {
            commands.Set(entity, new RootOnlyValue { Value = 4 });
            commands.Playback();
        }
        Assert.Equal(4, world.Read<RootOnlyValue>(entity).Value);

        Entity source = world.Create();
        Entity target = world.Create();
        RelationEdge<RootOnlyRelation> edge = world.CreateRelation(
            source,
            target,
            new RootOnlyRelation { Value = 5 });
        Assert.Equal(5, world.Read<RootOnlyRelation>(edge.Entity).Value);
        Assert.Equal(edge, Assert.Single(
            ReadPairEdges<RootOnlyRelation>(world, source, target).ToArray()));

        using (var commands = new CommandBuffer(world))
        {
            commands.Remove<RootOnlyValue>(entity);
            commands.Playback();
        }
        Assert.False(world.Has<RootOnlyValue>(entity));
    }

    [Fact]
    public void RootOnlyEnableableAndCleanupComponents_RetainTheirSpecialSemantics()
    {
        using var world = new World();
        Entity enabled = world.Create(new RootOnlyEnableable { Value = 7 });

        Assert.True(world.IsEnabled<RootOnlyEnableable>(enabled));
        world.Disable<RootOnlyEnableable>(enabled);
        Assert.False(world.IsEnabled<RootOnlyEnableable>(enabled));

        Query disabled = world.Query(
            new QueryDescription().WithStored<RootOnlyEnableable>().WithDisabled<RootOnlyEnableable>());
        int disabledRows = 0;
        {
            using QueryScope cursor = (disabled).OpenRead();
            foreach (QueryChunk _Chunk in cursor)
                for (int _ = 0; _ < _Chunk.Count; _++)
                disabledRows++;

        };
        Assert.Equal(1, disabledRows);
        world.Enable<RootOnlyEnableable>(enabled);

        Entity cleanup = world.Create(new RootOnlyCleanup { Value = 9 });
        world.Add(cleanup, new RootOnlyValue { Value = 10 });

        world.Destroy(cleanup);

        Assert.True(world.IsAlive(cleanup));
        Assert.True(world.IsPendingCleanup(cleanup));
        Assert.True(world.Has<RootOnlyCleanup>(cleanup));
        Assert.False(world.Has<RootOnlyValue>(cleanup));
        world.Remove<RootOnlyCleanup>(cleanup);
        Assert.False(world.IsAlive(cleanup));
    }

    private struct RootOnlyValue : global::SomeEngine.ECS.IAddedComponent
    {
        public int Value;
    }

    private struct RootOnlyEnableable : global::SomeEngine.ECS.IEnableableComponent, IAddedComponent
{
        public int Value;
    }

    private struct RootOnlyCleanup : global::SomeEngine.ECS.ICleanupComponent, IAddedComponent
{
        public int Value;
    }

    [RelationSchema(RelationDirection.Directed, RelationCardinality.Parallel)]
    private struct RootOnlyRelation : global::SomeEngine.ECS.IAddedComponent
    {
        public int Value;
    }

}
