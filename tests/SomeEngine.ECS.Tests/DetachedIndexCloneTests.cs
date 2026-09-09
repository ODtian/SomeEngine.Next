using SomeEngine.ECS.Commands;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Hierarchy;
using SomeEngine.ECS.Relations;

namespace SomeEngine.ECS.Tests;

public sealed class DetachedIndexCloneTests
{
    [Fact]
    public void FailedBatchPreservesIndexAndNextBatchUpdatesIt()
    {
        var world = new World();
        Entity first = world.CreateEntity(new Number(7));
        Entity second = world.CreateEntity(new Number(7));
        using (var commands = new CommandBuffer(world))
        {
            commands.Replace(first, new Number(9));
            commands.Add(first, new Number(11));
            Assert.Throws<InvalidOperationException>(commands.Playback);
        }
        Assert.Equal(new[] { first, second }, world.GetByIndex<Number, int>(7).ToArray());
        Assert.Empty(world.GetByIndex<Number, int>(9).ToArray());
        Assert.Equal(7, world.Read<Number>(first).Value);
        using (var commands = new CommandBuffer(world))
        {
            commands.Replace(first, new Number(9));
            commands.Playback();
        }
        Assert.Equal(new[] { second }, world.GetByIndex<Number, int>(7).ToArray());
        Assert.Equal(new[] { first }, world.GetByIndex<Number, int>(9).ToArray());
    }
    private readonly record struct Number(int Value) : IIndexedComponent<int>
    {
        public int GetKey() => Value;
    }
}
