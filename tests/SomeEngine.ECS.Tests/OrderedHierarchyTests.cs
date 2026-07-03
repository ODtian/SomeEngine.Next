using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Hierarchy;
using Xunit;

namespace SomeEngine.ECS.Tests;

public class OrderedHierarchyTests
{
    [Fact]
    public void SetParent_AppendsChildrenInStableOrder()
    {
        var world = new World();
        var parent = world.CreateEntity();
        var childA = world.CreateEntity();
        var childB = world.CreateEntity();
        var childC = world.CreateEntity();

        OrderedHierarchy.Attach(world, childA, parent);
        OrderedHierarchy.Attach(world, childB, parent);
        OrderedHierarchy.Attach(world, childC, parent);

        Assert.Equal(new[] { childA, childB, childC }, OrderedHierarchy.GetChildren(world, parent).ToArray());
    }

    [Fact]
    public void SetParent_WithInsertIndex_InsertsAtRequestedPosition()
    {
        var world = new World();
        var parent = world.CreateEntity();
        var childA = world.CreateEntity();
        var childB = world.CreateEntity();
        var childC = world.CreateEntity();

        OrderedHierarchy.Attach(world, childA, parent);
        OrderedHierarchy.Attach(world, childC, parent);
        OrderedHierarchy.Attach(world, childB, parent, 1);

        Assert.Equal(new[] { childA, childB, childC }, OrderedHierarchy.GetChildren(world, parent).ToArray());
    }

    [Fact]
    public void RemoveParent_PreservesRemainingSiblingOrder()
    {
        var world = new World();
        var parent = world.CreateEntity();
        var childA = world.CreateEntity();
        var childB = world.CreateEntity();
        var childC = world.CreateEntity();

        OrderedHierarchy.Attach(world, childA, parent);
        OrderedHierarchy.Attach(world, childB, parent);
        OrderedHierarchy.Attach(world, childC, parent);

        OrderedHierarchy.Detach(world, childB);

        Assert.Equal(new[] { childA, childC }, OrderedHierarchy.GetChildren(world, parent).ToArray());
        Assert.Equal(Entity.Null, OrderedHierarchy.GetParent(world, childB));
    }

    [Fact]
    public void DestroySubtree_PreservesRemainingSiblingOrder()
    {
        var world = new World();
        var root = world.CreateEntity();
        var childA = world.CreateEntity();
        var childB = world.CreateEntity();
        var childC = world.CreateEntity();

        OrderedHierarchy.Attach(world, childA, root);
        OrderedHierarchy.Attach(world, childB, root);
        OrderedHierarchy.Attach(world, childC, root);

        OrderedHierarchy.DestroySubtree(world, childB);

        Assert.False(world.IsAlive(childB));
        Assert.Equal(new[] { childA, childC }, OrderedHierarchy.GetChildren(world, root).ToArray());
    }

    [Fact]
    public void SetParent_SameParent_ReordersChildren()
    {
        var world = new World();
        var parent = world.CreateEntity();
        var childA = world.CreateEntity();
        var childB = world.CreateEntity();
        var childC = world.CreateEntity();

        OrderedHierarchy.Attach(world, childA, parent);
        OrderedHierarchy.Attach(world, childB, parent);
        OrderedHierarchy.Attach(world, childC, parent);

        OrderedHierarchy.Reorder(world, childC, 0);

        Assert.Equal(new[] { childC, childA, childB }, OrderedHierarchy.GetChildren(world, parent).ToArray());
    }

    [Fact]
    public void SetParent_SameParent_WithoutInsertIndex_IsNoop()
    {
        var world = new World();
        var parent = world.CreateEntity();
        var childA = world.CreateEntity();
        var childB = world.CreateEntity();
        var childC = world.CreateEntity();

        OrderedHierarchy.Attach(world, childA, parent);
        OrderedHierarchy.Attach(world, childB, parent);
        OrderedHierarchy.Attach(world, childC, parent);

        OrderedHierarchy.Move(world, childB, parent);

        Assert.Equal(new[] { childA, childB, childC }, OrderedHierarchy.GetChildren(world, parent).ToArray());
    }
}
