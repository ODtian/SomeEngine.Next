using SomeEngine.ECS.Commands;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Hierarchy;
using SomeEngine.ECS.Relations;

namespace SomeEngine.ECS.Tests;

public sealed class TopologySideOwnerCopyOnWriteTests
{
    [Fact]
    public void InvalidHierarchyBatchPreservesParentsAndNextBatchSucceeds()
    {
        var world = new World();
        Entity parent = world.CreateEntity();
        Entity next = world.CreateEntity();
        Entity child = world.CreateEntity();
        Hierarchy<Domain>.SetParent(world, child, parent);
        using (var commands = new CommandBuffer(world))
        {
            var hierarchy = commands.Hierarchy<Domain>();
            hierarchy.SetParent(child, next, HierarchyMaintenanceTiming.Immediate);
            hierarchy.SetParent(next, next, HierarchyMaintenanceTiming.Immediate);
            Assert.Throws<InvalidOperationException>(commands.Playback);
        }
        Assert.Equal(parent, Hierarchy<Domain>.GetParent(world, child));
        using (var commands = new CommandBuffer(world))
        {
            commands.Hierarchy<Domain>().SetParent(child, next, HierarchyMaintenanceTiming.Immediate);
            commands.Playback();
        }
        Assert.Equal(next, Hierarchy<Domain>.GetParent(world, child));
    }
    private readonly struct Domain : IHierarchyDomain;
}
