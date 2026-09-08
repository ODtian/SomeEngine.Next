using SomeEngine.ECS.Commands;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Hierarchy;
using SomeEngine.ECS.Relations;

namespace SomeEngine.ECS.Tests;

public sealed class DetachedTableCloneTests
{
    [Fact]
    public void FailedBatchPreservesEntitiesAndSuccessfulBatchChangesAllValues()
    {
        var world = new World();
        Entity[] entities = Enumerable.Range(0, 256).Select(i => world.CreateEntity(new Value(i))).ToArray();
        using (var commands = new CommandBuffer(world))
        {
            foreach (Entity entity in entities) commands.Replace(entity, new Value(-1));
            commands.Add(entities[^1], new Value(-2));
            Assert.Throws<InvalidOperationException>(commands.Playback);
        }
        Assert.Equal(256, world.EntityCount);
        Assert.Equal(Enumerable.Range(0, 256), entities.Select(entity => world.Read<Value>(entity).Number));
        using (var commands = new CommandBuffer(world))
        {
            foreach (Entity entity in entities) commands.Replace(entity, new Value(42));
            commands.Playback();
        }
        Assert.All(entities, entity => Assert.Equal(42, world.Read<Value>(entity).Number));
    }
    private readonly record struct Value(int Number) : IComponent;
}
