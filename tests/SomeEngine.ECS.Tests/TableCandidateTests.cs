using SomeEngine.ECS.Commands;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Serialization;

namespace SomeEngine.ECS.Tests;

public sealed class TableCandidateTests
{
    [Fact]
    public void FailedCandidatePreservesEntitiesAndSuccessfulCandidateChangesAllValues()
    {
        using var world = new World();
        Entity[] entities = Enumerable.Range(0, 256).Select(index => world.Create(new Value(index))).ToArray();
        using var checkpoint = new WorldCheckpoint().Capture(world, CheckpointProgress.Preserve);
        using var candidate = new World();
        checkpoint.Restore(candidate);
        using (var commands = new CommandBuffer(candidate))
        {
            foreach (Entity entity in entities) commands.Set(entity, new Value(-1));
            commands.Add(Entity.Null, new Value(-2));
            Assert.Throws<ArgumentException>(commands.Playback);
        }
        Assert.Equal(256, world.EntityCount);
        Assert.Equal(Enumerable.Range(0, 256), entities.Select(entity => world.Read<Value>(entity).Number));
        checkpoint.Restore(candidate);
        using (var commands = new CommandBuffer(candidate))
        {
            foreach (Entity entity in entities) commands.Set(entity, new Value(42));
            commands.Playback();
        }
        checkpoint.Capture(candidate, CheckpointProgress.Preserve).Restore(world);
        Assert.All(entities, entity => Assert.Equal(42, world.Read<Value>(entity).Number));
    }
    private readonly record struct Value(int Number) : IAddedComponent;
}
