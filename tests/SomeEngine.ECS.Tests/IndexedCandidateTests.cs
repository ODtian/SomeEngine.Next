using SomeEngine.ECS.Commands;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Serialization;

namespace SomeEngine.ECS.Tests;

public sealed class IndexedCandidateTests
{
    [Fact]
    public void FailedCandidatePreservesTheIndexAndTheNextBatchUpdatesIt()
    {
        using var world = new World();
        Entity first = world.Create(new Number(7));
        Entity second = world.Create(new Number(7));
        Assert.Equal([first, second], ReadIndexedEntities<Number, int>(world, 7));
        using var checkpoint = new WorldCheckpoint().Capture(world, CheckpointProgress.Preserve);
        using var candidate = new World();
        checkpoint.Restore(candidate);
        using (var commands = new CommandBuffer(candidate))
        {
            commands.Set(first, new Number(9));
            commands.Add(Entity.Null, new Number(11));
            Assert.Throws<ArgumentException>(commands.Playback);
        }
        Assert.Equal([first, second], ReadIndexedEntities<Number, int>(world, 7));
        Assert.Empty(ReadIndexedEntities<Number, int>(world, 9));
        Assert.Equal(7, world.Read<Number>(first).Value);
        using (var commands = new CommandBuffer(world))
        {
            commands.Set(first, new Number(9));
            commands.Playback();
        }
        Assert.Equal([second], ReadIndexedEntities<Number, int>(world, 7));
        Assert.Equal([first], ReadIndexedEntities<Number, int>(world, 9));
    }
    private readonly record struct Number(int Value) : IIndexedComponent<int>
    {
        public int GetKey() => Value;
    }
}
