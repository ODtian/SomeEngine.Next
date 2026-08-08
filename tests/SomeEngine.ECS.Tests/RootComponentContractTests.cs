using SomeEngine.ECS.Commands;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Hooks;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Registry;
using SomeEngine.ECS.Relations;

namespace SomeEngine.ECS.Tests;

public sealed class RootComponentContractTests
{
    [Fact]
    public void RootOnlyTableComponent_TraversesWorldQueryCommandHookAndRelationPaths()
    {
        using var world = new World();
        int addedValue = -1;
        world.Hooks<RootOnlyValue>().OnAdd(
            (DeferredWorld _, Entity _, in RootOnlyValue value) => addedValue = value.Value);

        Entity entity = world.CreateEntity(new RootOnlyValue { Value = 1 });

        Assert.Equal(StoragePath.Table, ComponentMetadata<RootOnlyValue>.Storage);
        Assert.Equal(1, addedValue);
        Assert.True(world.Has<RootOnlyValue>(entity));
        Assert.Equal(1, world.Read<RootOnlyValue>(entity).Value);

        world.Replace(entity, new RootOnlyValue { Value = 2 });
        QueryHandle query = world.Query(
            world.QueryDefinition().ReadWrite<RootOnlyValue>());
        world.ExecuteQuery(query, cursor =>
        {
            foreach (QueryRow row in cursor.Rows)
                row.ReadWrite<RootOnlyValue>().Value++;
        });
        Assert.Equal(3, world.Read<RootOnlyValue>(entity).Value);

        using (var commands = new CommandBuffer(world))
        {
            commands.Replace(entity, new RootOnlyValue { Value = 4 });
            commands.Playback();
        }
        Assert.Equal(4, world.Read<RootOnlyValue>(entity).Value);

        Entity source = world.CreateEntity();
        Entity target = world.CreateEntity();
        RelationEdge<RootOnlyRelation> edge = world.CreateRelation(
            source,
            target,
            new RootOnlyRelation { Value = 5 });
        Assert.Equal(5, world.Read<RootOnlyRelation>(edge.Entity).Value);
        Assert.Equal(edge, Assert.Single(
            world.GetRelationEdgesBetween<RootOnlyRelation>(source, target).ToArray()));

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
        Entity enabled = world.CreateEntity(new RootOnlyEnableable { Value = 7 });

        Assert.True(ComponentMetadata<RootOnlyEnableable>.IsEnableable);
        Assert.True(world.IsEnabled<RootOnlyEnableable>(enabled));
        world.Disable<RootOnlyEnableable>(enabled);
        Assert.False(world.IsEnabled<RootOnlyEnableable>(enabled));

        QueryHandle disabled = world.Query(
            world.QueryDefinition()
                .Read<RootOnlyEnableable>()
                .Disabled<RootOnlyEnableable>());
        int disabledRows = 0;
        world.ExecuteQuery(disabled, cursor =>
        {
            foreach (QueryRow _ in cursor.Rows)
                disabledRows++;
        });
        Assert.Equal(1, disabledRows);
        world.Enable<RootOnlyEnableable>(enabled);

        Entity cleanup = world.CreateEntity(new RootOnlyCleanup { Value = 9 });
        world.Add(cleanup, new RootOnlyValue { Value = 10 });
        Assert.True(ComponentMetadata<RootOnlyCleanup>.IsCleanup);

        world.DestroyEntity(cleanup);

        Assert.True(world.IsAlive(cleanup));
        Assert.True(world.IsPendingCleanup(cleanup));
        Assert.True(world.Has<RootOnlyCleanup>(cleanup));
        Assert.False(world.Has<RootOnlyValue>(cleanup));
        world.Remove<RootOnlyCleanup>(cleanup);
        Assert.False(world.IsAlive(cleanup));
    }

    private struct RootOnlyValue : global::SomeEngine.ECS.IComponent
    {
        public int Value;
    }

    private struct RootOnlyEnableable : global::SomeEngine.ECS.IEnableableComponent
    {
        public int Value;
    }

    private struct RootOnlyCleanup : global::SomeEngine.ECS.ICleanupComponent
    {
        public int Value;
    }

    [RelationSchema(RelationDirection.Directed, RelationCardinality.Parallel)]
    private struct RootOnlyRelation : global::SomeEngine.ECS.IComponent
    {
        public int Value;
    }

}
