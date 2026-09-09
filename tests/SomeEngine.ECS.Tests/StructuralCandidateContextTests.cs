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
        Entity left = first.CreateEntity(new Value(1));
        Entity right = second.CreateEntity(new Value(2));
        using var rejected = new CommandBuffer(first);
        rejected.Replace(left, new Value(3));
        rejected.Add(left, new Value(4));
        using var accepted = new CommandBuffer(second);
        accepted.Replace(right, new Value(5));
        accepted.Playback();
        Assert.Throws<InvalidOperationException>(rejected.Playback);
        Assert.Equal(1, first.Read<Value>(left).Number);
        Assert.Equal(5, second.Read<Value>(right).Number);
        first.Replace(left, new Value(6));
        Assert.Equal(6, first.Read<Value>(left).Number);
    }
    private readonly record struct Value(int Number) : IComponent;
}
