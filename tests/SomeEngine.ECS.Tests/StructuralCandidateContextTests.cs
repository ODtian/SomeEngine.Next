using SomeEngine.ECS.Serialization;
using SomeEngine.ECS.Commands;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Hierarchy;
using SomeEngine.ECS.Relations;

namespace SomeEngine.ECS.Tests;

public sealed class StructuralCandidateContextTests
{
    [Fact]
    public void RejectedBatchInOneWorldDoesNotChangeAnotherWorld()
    {
        var first = new World();
        var second = new World();
        Entity left = first.Create(new Value(1));
        Entity right = second.Create(new Value(2));
        using var checkpoint = new WorldCheckpoint().Capture(first, CheckpointProgress.Preserve);
        using var candidate = new World();
        checkpoint.Restore(candidate);
        using var rejected = new CommandBuffer(candidate);
        rejected.Set(left, new Value(3));
        rejected.Add(Entity.Null, new Value(4));
        using var accepted = new CommandBuffer(second);
        accepted.Set(right, new Value(5));
        accepted.Playback();
        Assert.Throws<ArgumentException>(rejected.Playback);
        Assert.Equal(1, first.Read<Value>(left).Number);
        Assert.Equal(5, second.Read<Value>(right).Number);
        first.Set(left, new Value(6));
        Assert.Equal(6, first.Read<Value>(left).Number);
    }
    private readonly record struct Value(int Number) : IAddedComponent;
}
