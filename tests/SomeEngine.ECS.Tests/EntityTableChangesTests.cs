using SomeEngine.ECS.Commands;
using SomeEngine.ECS.Entities;

namespace SomeEngine.ECS.Tests;

public sealed class EntityTableChangesTests
{
    [Fact]
    public void AcceptingAnEarlierBatchPreservesLaterChangesAndDestroyedIdentities()
    {
        using var world = new World();
        Entity entity = world.CreateEntity(new Position());
        using EntityTableChanges changes = world.ObserveEntityTables();
        EntityTableChangeBatch first = changes.Read();
        Assert.Equal(entity, Assert.Single(first.Entities));
        world.Add(entity, new Velocity());
        changes.Accept(first);
        EntityTableChangeBatch second = changes.Read();
        Assert.Equal(entity, Assert.Single(second.Entities));
        changes.Accept(second);
        Assert.Empty(changes.Read().Entities);
        world.Replace(entity, new Position { X = 12 });
        Assert.Empty(changes.Read().Entities);
        world.DestroyEntity(entity);
        Entity replacement = world.CreateEntity();
        EntityTableChangeBatch final = changes.Read();
        Assert.Contains(entity, final.Entities);
        Assert.Contains(replacement, final.Entities);
        Assert.Equal(2, final.Count);
        Assert.Equal(entity, Assert.Single(first.Entities));
        changes.Accept(final);
        Assert.Empty(changes.Read().Entities);
    }

    [Fact]
    public void FailedCommandBatchDoesNotExposeMembershipChanges()
    {
        using var world = new World();
        Entity entity = world.CreateEntity(new Position());
        using EntityTableChanges changes = world.ObserveEntityTables();
        changes.Accept(changes.Read());
        using (var commands = new CommandBuffer(world))
        {
            commands.Add(entity, new Velocity());
            commands.Replace(commands.CreateEntity(), new Position());
            Assert.Throws<InvalidOperationException>(commands.Playback);
        }
        Assert.Empty(changes.Read().Entities);
        Assert.False(world.Has<Velocity>(entity));
        using (var commands = new CommandBuffer(world))
        {
            commands.Add(entity, new Velocity());
            commands.Playback();
        }
        Assert.Equal(entity, Assert.Single(changes.Read().Entities));
    }

    [Fact]
    public void ObserversAcceptIndependently()
    {
        using var world = new World();
        using EntityTableChanges first = world.ObserveEntityTables();
        using EntityTableChanges second = world.ObserveEntityTables();
        Entity entity = world.CreateEntity();
        first.Accept(first.Read());
        Assert.Empty(first.Read().Entities);
        Assert.Equal(entity, Assert.Single(second.Read().Entities));
        Assert.Throws<ArgumentException>(() => first.Accept(second.Read()));
        second.Accept(second.Read());
        Assert.Empty(second.Read().Entities);
    }
}
