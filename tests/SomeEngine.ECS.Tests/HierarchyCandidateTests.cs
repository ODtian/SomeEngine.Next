using SomeEngine.ECS.Commands;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Hierarchy;
using SomeEngine.ECS.Serialization;

namespace SomeEngine.ECS.Tests;

public sealed class HierarchyCandidateTests
{
    [Fact]
    public void InvalidCandidateHierarchyBatchPreservesTheParentAndAllowsRetry()
    {
        using var world = new World();
        Entity parent = world.Create();
        Entity next = world.Create();
        Entity child = world.Create();
        Hierarchy<Domain>.SetParent(world, child, parent);
        using var checkpoint = new WorldCheckpoint().Capture(world, CheckpointProgress.Preserve);
        using var candidate = new World();
        checkpoint.Restore(candidate);
        using (var commands = new CommandBuffer(candidate))
        {
            commands.EditParents<Domain>([new(child, next)]);
            commands.EditParents<Domain>([new(next, next)]);
            Assert.Throws<ArgumentException>(commands.Playback);
        }
        Assert.Equal(parent, Hierarchy<Domain>.GetParent(world, child));
        using (var commands = new CommandBuffer(world))
        {
            commands.EditParents<Domain>([new(child, next)]);
            commands.Playback();
        }
        Assert.Equal(next, Hierarchy<Domain>.GetParent(world, child));
    }
    private readonly struct Domain : IHierarchyDomain;
}
