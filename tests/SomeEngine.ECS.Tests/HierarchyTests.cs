using SomeEngine.ECS;
using SomeEngine.ECS.Commands;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Hierarchy;
using Xunit;

namespace SomeEngine.ECS.Tests;

public class HierarchyTests
{
    [Fact]
    public void SetParent_MaintainsOldAndNewParentChildBuffers()
    {
        var world = new World();
        var oldParent = world.CreateEntity();
        var newParent = world.CreateEntity();
        var child = world.CreateEntity();

        UnorderedHierarchy.Attach(world, child, oldParent);
        Assert.Equal(new[] { child }, UnorderedHierarchy.GetChildren(world, oldParent).ToArray());

        UnorderedHierarchy.Move(world, child, newParent);

        Assert.Empty(UnorderedHierarchy.GetChildren(world, oldParent).ToArray());
        Assert.Equal(new[] { child }, UnorderedHierarchy.GetChildren(world, newParent).ToArray());
        Assert.Equal(newParent, UnorderedHierarchy.GetParent(world, child));
    }

    [Fact]
    public void SetParent_RejectsSelfParentAndCycles()
    {
        var world = new World();
        var root = world.CreateEntity();
        var child = world.CreateEntity();
        var grandChild = world.CreateEntity();

        Assert.Throws<InvalidOperationException>(() => UnorderedHierarchy.Attach(world, root, root));

        UnorderedHierarchy.Attach(world, child, root);
        UnorderedHierarchy.Attach(world, grandChild, child);

        Assert.Throws<InvalidOperationException>(() => UnorderedHierarchy.Attach(world, root, grandChild));
    }

    [Fact]
    public void RemoveParent_MakesChildRootAndRecomputesDepths()
    {
        var world = new World();
        var root = world.CreateEntity();
        var child = world.CreateEntity();
        var grandChild = world.CreateEntity();

        UnorderedHierarchy.Attach(world, child, root);
        UnorderedHierarchy.Attach(world, grandChild, child);

        UnorderedHierarchy.Detach(world, child);

        Assert.Equal(Entity.Null, UnorderedHierarchy.GetParent(world, child));
        Assert.Equal(0, world.Read<Depth>(child).Value);
        Assert.Equal(1, world.Read<Depth>(grandChild).Value);
        Assert.Empty(UnorderedHierarchy.GetChildren(world, root).ToArray());
        Assert.Equal(new[] { grandChild }, UnorderedHierarchy.GetChildren(world, child).ToArray());
    }

    [Fact]
    public void GetChildren_WithoutChildBuffer_ReturnsEmpty()
    {
        var world = new World();
        var entity = world.CreateEntity();

        Assert.Empty(UnorderedHierarchy.GetChildren(world, entity).ToArray());
        Assert.Equal(Entity.Null, UnorderedHierarchy.GetParent(world, entity));
    }

    [Fact]
    public void SetParent_ComputesDepthsAcrossSubtree()
    {
        var world = new World();
        var root = world.CreateEntity();
        var child = world.CreateEntity();
        var grandChild = world.CreateEntity();

        UnorderedHierarchy.Attach(world, child, root);
        UnorderedHierarchy.Attach(world, grandChild, child);

        Assert.Equal(0, world.Read<Depth>(root).Value);
        Assert.Equal(1, world.Read<Depth>(child).Value);
        Assert.Equal(2, world.Read<Depth>(grandChild).Value);
    }

    [Fact]
    public void SetParent_WritesParentFactAndDerivedDepth()
    {
        var world = new World();
        var parent = world.CreateEntity();
        var child = world.CreateEntity();

        UnorderedHierarchy.Attach(world, child, parent);

        var storedParent = world.Read<Parent>(child);
        Assert.Equal(parent, storedParent.Value);
        Assert.Equal(1, world.Read<Depth>(child).Value);
    }

    [Fact]
    public void DestroyEntity_DoesNotCascade()
    {
        var world = new World();
        var root = world.CreateEntity();
        var child = world.CreateEntity();
        var grandChild = world.CreateEntity();

        UnorderedHierarchy.Attach(world, child, root);
        UnorderedHierarchy.Attach(world, grandChild, child);

        world.DestroyEntity(root);

        Assert.True(world.IsAlive(root));
        Assert.True(world.IsPendingCleanup(root));
        Assert.True(world.Has<ChildBuffer>(root));
        Assert.True(world.IsAlive(child));
        Assert.True(world.IsAlive(grandChild));
        Assert.Equal(Entity.Null, UnorderedHierarchy.GetParent(world, child));
        Assert.Empty(UnorderedHierarchy.GetChildren(world, root).ToArray());
        Assert.Equal(child, UnorderedHierarchy.GetParent(world, grandChild));
    }

    [Fact]
    public void DestroySubtree_CascadesAllDescendants()
    {
        var world = new World();
        var root = world.CreateEntity();
        var child = world.CreateEntity();
        var grandChild = world.CreateEntity();

        UnorderedHierarchy.Attach(world, child, root);
        UnorderedHierarchy.Attach(world, grandChild, child);

        UnorderedHierarchy.DestroySubtree(world, root);

        Assert.False(world.IsAlive(root));
        Assert.False(world.IsAlive(child));
        Assert.False(world.IsAlive(grandChild));
    }

    [Fact]
    public void DestroySubtree_DetachesFromLivingParent()
    {
        var world = new World();
        var root = world.CreateEntity();
        var child = world.CreateEntity();
        var grandChild = world.CreateEntity();

        UnorderedHierarchy.Attach(world, child, root);
        UnorderedHierarchy.Attach(world, grandChild, child);

        UnorderedHierarchy.DestroySubtree(world, child);

        Assert.True(world.IsAlive(root));
        Assert.False(world.IsAlive(child));
        Assert.False(world.IsAlive(grandChild));
        Assert.Empty(UnorderedHierarchy.GetChildren(world, root).ToArray());
    }

    [Fact]
    public void DestroyOneChild_LeavesSiblingBranchIntact()
    {
        var world = new World();
        var root = world.CreateEntity();
        var left = world.CreateEntity();
        var right = world.CreateEntity();
        var rightChild = world.CreateEntity();

        UnorderedHierarchy.Attach(world, left, root);
        UnorderedHierarchy.Attach(world, right, root);
        UnorderedHierarchy.Attach(world, rightChild, right);

        UnorderedHierarchy.DestroySubtree(world, left);

        Assert.False(world.IsAlive(left));
        Assert.True(world.IsAlive(root));
        Assert.True(world.IsAlive(right));
        Assert.True(world.IsAlive(rightChild));
        Assert.Equal(new[] { right }, UnorderedHierarchy.GetChildren(world, root).ToArray());
        Assert.Equal(new[] { rightChild }, UnorderedHierarchy.GetChildren(world, right).ToArray());
    }

    [Fact]
    public void DestroySubtree_CascadesDeepHierarchy()
    {
        var world = new World();
        var root = world.CreateEntity();
        var level1 = world.CreateEntity();
        var level2 = world.CreateEntity();
        var level3 = world.CreateEntity();
        var level4 = world.CreateEntity();

        UnorderedHierarchy.Attach(world, level1, root);
        UnorderedHierarchy.Attach(world, level2, level1);
        UnorderedHierarchy.Attach(world, level3, level2);
        UnorderedHierarchy.Attach(world, level4, level3);

        UnorderedHierarchy.DestroySubtree(world, root);

        Assert.False(world.IsAlive(root));
        Assert.False(world.IsAlive(level1));
        Assert.False(world.IsAlive(level2));
        Assert.False(world.IsAlive(level3));
        Assert.False(world.IsAlive(level4));
    }

    [Fact]
    public void ReparentingSubtree_RecomputesMovedDepthsWithoutTouchingUnrelatedBranch()
    {
        var world = new World();
        var rootA = world.CreateEntity();
        var rootB = world.CreateEntity();
        var child = world.CreateEntity();
        var grandChild = world.CreateEntity();
        var sibling = world.CreateEntity();

        UnorderedHierarchy.Attach(world, child, rootA);
        UnorderedHierarchy.Attach(world, grandChild, child);
        UnorderedHierarchy.Attach(world, sibling, rootA);

        UnorderedHierarchy.Move(world, child, rootB);

        Assert.Equal(rootB, UnorderedHierarchy.GetParent(world, child));
        Assert.Equal(1, world.Read<Depth>(child).Value);
        Assert.Equal(2, world.Read<Depth>(grandChild).Value);
        Assert.Equal(rootA, UnorderedHierarchy.GetParent(world, sibling));
        Assert.Equal(1, world.Read<Depth>(sibling).Value);
        Assert.DoesNotContain(child, UnorderedHierarchy.GetChildren(world, rootA).ToArray());
        Assert.Equal(new[] { child }, UnorderedHierarchy.GetChildren(world, rootB).ToArray());
    }

    [Fact]
    public void FirstSetParent_CreatesOnlyParentAndChildTargetArchetypes()
    {
        var world = new World();
        var parent = world.CreateEntity();
        var child = world.CreateEntity();

        UnorderedHierarchy.Attach(world, child, parent);

        Assert.Equal(3, world.ArchetypeCount);
        Assert.True(world.Has<Depth>(parent));
        Assert.True(world.Has<ChildBuffer>(parent));
        Assert.True(world.Has<Parent>(child));
        Assert.True(world.Has<Depth>(child));
    }

    [Fact]
    public void Reparent_WhenBothSidesAlreadyPrepared_DoesNotCreateNewArchetype()
    {
        var world = new World();
        var parentA = world.CreateEntity();
        var parentB = world.CreateEntity();
        var child = world.CreateEntity();

        UnorderedHierarchy.Attach(world, child, parentA);
        var prepared = world.CreateEntity();
        UnorderedHierarchy.Attach(world, prepared, parentB);
        UnorderedHierarchy.Detach(world, prepared);

        int archetypeCountBefore = world.ArchetypeCount;
        UnorderedHierarchy.Move(world, child, parentB);

        Assert.Equal(archetypeCountBefore, world.ArchetypeCount);
    }

    [Fact]
    public void RemoveParent_RetainsEmptyChildBufferOnFormerParent()
    {
        var world = new World();
        var parent = world.CreateEntity();
        var child = world.CreateEntity();

        UnorderedHierarchy.Attach(world, child, parent);
        UnorderedHierarchy.Detach(world, child);

        Assert.True(world.Has<ChildBuffer>(parent));
        Assert.Empty(UnorderedHierarchy.GetChildren(world, parent).ToArray());
    }

    [Fact]
    public void ReattachToParentWithEmptyChildBuffer_DoesNotCreateNewArchetype()
    {
        var world = new World();
        var parent = world.CreateEntity();
        var firstChild = world.CreateEntity();

        UnorderedHierarchy.Attach(world, firstChild, parent);
        UnorderedHierarchy.Detach(world, firstChild);

        int archetypeCountBefore = world.ArchetypeCount;
        var secondChild = world.CreateEntity();
        UnorderedHierarchy.Attach(world, secondChild, parent);

        Assert.Equal(archetypeCountBefore, world.ArchetypeCount);
        Assert.Equal(new[] { secondChild }, UnorderedHierarchy.GetChildren(world, parent).ToArray());
    }

    [Fact]
    public void DestroySubtree_OnHundredLevelChain_DoesNotOverflowAndKillsWholeTree()
    {
        var world = new World();
        var entities = new List<Entity>();
        entities.Add(world.CreateEntity());

        for (int i = 1; i < 100; i++)
        {
            var entity = world.CreateEntity();
            UnorderedHierarchy.Attach(world, entity, entities[i - 1]);
            entities.Add(entity);
        }

        UnorderedHierarchy.DestroySubtree(world, entities[0]);

        foreach (var entity in entities)
            Assert.False(world.IsAlive(entity));
    }

    [Fact]
    public void EntityCount_O1_Accuracy()
    {
        var world = new World();
        var entities = new List<Entity>();

        for (int i = 0; i < 100; i++)
            entities.Add(world.CreateEntity());

        Assert.Equal(100, world.EntityCount);

        for (int i = 0; i < 30; i++)
            world.DestroyEntity(entities[i]);

        Assert.Equal(70, world.EntityCount);
    }

    [Fact]
    public void Reparent_DeepSubtree_UpdatesDepthsIteratively()
    {
        var world = new World();
        var rootA = world.CreateEntity();
        var rootB = world.CreateEntity();
        var anchor = world.CreateEntity();
        var movingRoot = world.CreateEntity();
        var descendants = new List<Entity>();

        UnorderedHierarchy.Attach(world, anchor, rootB);
        UnorderedHierarchy.Attach(world, movingRoot, rootA);

        var parent = movingRoot;
        for (int i = 0; i < 200; i++)
        {
            var child = world.CreateEntity();
            UnorderedHierarchy.Attach(world, child, parent);
            descendants.Add(child);
            parent = child;
        }

        UnorderedHierarchy.Move(world, movingRoot, anchor);

        Assert.Equal(2, world.Read<Depth>(movingRoot).Value);
        for (int i = 0; i < descendants.Count; i++)
            Assert.Equal(i + 3, world.Read<Depth>(descendants[i]).Value);
    }

    [Fact]
    public void RemoveParent_DeepSubtree_ResetsDepthsIteratively()
    {
        var world = new World();
        var root = world.CreateEntity();
        var movingRoot = world.CreateEntity();
        var descendants = new List<Entity>();

        UnorderedHierarchy.Attach(world, movingRoot, root);

        var parent = movingRoot;
        for (int i = 0; i < 200; i++)
        {
            var child = world.CreateEntity();
            UnorderedHierarchy.Attach(world, child, parent);
            descendants.Add(child);
            parent = child;
        }

        UnorderedHierarchy.Detach(world, movingRoot);

        Assert.Equal(0, world.Read<Depth>(movingRoot).Value);
        for (int i = 0; i < descendants.Count; i++)
            Assert.Equal(i + 1, world.Read<Depth>(descendants[i]).Value);
    }
    [Fact]
    public void DestroyEntity_ChildLeavesParentCacheStaleUntilUpdate()
    {
        var world = new World();
        var parent = world.CreateEntity();
        var child = world.CreateEntity();

        UnorderedHierarchy.Attach(world, child, parent);
        world.DestroyEntity(child);

        Assert.True(world.IsAlive(child));
        Assert.True(world.IsPendingCleanup(child));
        Assert.Equal(new[] { child }, UnorderedHierarchy.GetChildren(world, parent).ToArray());

        UnorderedHierarchy.Update(world);

        Assert.False(world.IsAlive(child));
        Assert.Empty(UnorderedHierarchy.GetChildren(world, parent).ToArray());
    }

    [Fact]
    public void WorldSetParent_DoesNotImmediatelyRepairChildBufferOrDepth()
    {
        var world = new World();
        var oldParent = world.CreateEntity();
        var newParentRoot = world.CreateEntity();
        var newParent = world.CreateEntity();
        var child = world.CreateEntity();

        UnorderedHierarchy.Attach(world, newParent, newParentRoot);
        UnorderedHierarchy.Attach(world, child, oldParent);

        world.Replace(child, new Parent { Value = newParent });

        var storedParent = world.Read<Parent>(child);
        Assert.Equal(newParent, storedParent.Value);
        Assert.Equal(new[] { child }, UnorderedHierarchy.GetChildren(world, oldParent).ToArray());
        Assert.Empty(UnorderedHierarchy.GetChildren(world, newParent).ToArray());
        Assert.Equal(1, world.Read<Depth>(child).Value);
    }

    [Fact]
    public void UnorderedUpdate_RepairsChildBufferAndDepth()
    {
        var world = new World();
        var oldParent = world.CreateEntity();
        var newParentRoot = world.CreateEntity();
        var newParent = world.CreateEntity();
        var child = world.CreateEntity();

        UnorderedHierarchy.Attach(world, newParent, newParentRoot);
        UnorderedHierarchy.Attach(world, child, oldParent);
        world.Replace(child, new Parent { Value = newParent });

        UnorderedHierarchy.Update(world);

        var storedParent = world.Read<Parent>(child);
        Assert.Equal(newParent, storedParent.Value);
        Assert.Empty(UnorderedHierarchy.GetChildren(world, oldParent).ToArray());
        Assert.Equal(new[] { child }, UnorderedHierarchy.GetChildren(world, newParent).ToArray());
        Assert.Equal(2, world.Read<Depth>(child).Value);
    }

    [Fact]
    public void OrderedUpdate_RepairsChildBuffer_InOrder()
    {
        var world = new World();
        var oldParent = world.CreateEntity();
        var newParent = world.CreateEntity();
        var first = world.CreateEntity();
        var moving = world.CreateEntity();

        OrderedHierarchy.Attach(world, first, newParent);
        OrderedHierarchy.Attach(world, moving, oldParent);
        world.Replace(moving, new Parent { Value = newParent });

        OrderedHierarchy.Update(world);

        Assert.Empty(OrderedHierarchy.GetChildren(world, oldParent).ToArray());
        Assert.Equal(new[] { first, moving }, OrderedHierarchy.GetChildren(world, newParent).ToArray());
    }

    [Fact]
    public void CommandBufferSetParent_ThenUpdate_Works()
    {
        var world = new World();
        var oldParent = world.CreateEntity();
        var newParent = world.CreateEntity();
        var child = world.CreateEntity();
        var cb = new CommandBuffer(world);

        UnorderedHierarchy.Attach(world, child, oldParent);
        cb.Replace(child, new Parent { Value = newParent });
        cb.Playback();

        Assert.Equal(new[] { child }, UnorderedHierarchy.GetChildren(world, oldParent).ToArray());
        Assert.Empty(UnorderedHierarchy.GetChildren(world, newParent).ToArray());

        UnorderedHierarchy.Update(world);

        var storedParent = world.Read<Parent>(child);
        Assert.Equal(newParent, storedParent.Value);
        Assert.Empty(UnorderedHierarchy.GetChildren(world, oldParent).ToArray());
        Assert.Equal(new[] { child }, UnorderedHierarchy.GetChildren(world, newParent).ToArray());
    }

    [Fact]
    public void WorldRemoveParent_DoesNotImmediatelyRepairChildBuffer_AndUpdateProcessesTransition()
    {
        var world = new World();
        var parent = world.CreateEntity();
        var child = world.CreateEntity();

        UnorderedHierarchy.Attach(world, child, parent);

        world.Remove<Parent>(child);

        Assert.False(world.Has<Parent>(child));
        Assert.Equal(new[] { child }, UnorderedHierarchy.GetChildren(world, parent).ToArray());

        UnorderedHierarchy.Update(world);

        Assert.False(world.Has<Parent>(child));
        Assert.Empty(UnorderedHierarchy.GetChildren(world, parent).ToArray());
        Assert.Equal(0, world.Read<Depth>(child).Value);
    }

    [Fact]
    public void Update_HandlesDestroyedChild()
    {
        var world = new World();
        var parent = world.CreateEntity();
        var child = world.CreateEntity();

        UnorderedHierarchy.Attach(world, child, parent);
        world.DestroyEntity(child);

        Assert.Equal(new[] { child }, UnorderedHierarchy.GetChildren(world, parent).ToArray());

        UnorderedHierarchy.Update(world);

        Assert.False(world.IsAlive(child));
        Assert.Empty(UnorderedHierarchy.GetChildren(world, parent).ToArray());
    }

    [Fact]
    public void Update_HandlesSoftDestroyedParent_UsingCleanupOwnedChildCache()
    {
        var world = new World();
        var parent = world.CreateEntity();
        var child = world.CreateEntity();
        var grandChild = world.CreateEntity();

        UnorderedHierarchy.Attach(world, child, parent);
        UnorderedHierarchy.Attach(world, grandChild, child);

        world.DestroyEntity(parent);

        Assert.True(world.IsAlive(parent));
        Assert.True(world.IsPendingCleanup(parent));
        Assert.True(world.Has<ChildBuffer>(parent));
        Assert.Equal(Entity.Null, UnorderedHierarchy.GetParent(world, child));

        UnorderedHierarchy.Update(world);

        Assert.False(world.IsAlive(parent));
        Assert.False(world.Has<Parent>(child));
        Assert.Equal(Entity.Null, UnorderedHierarchy.GetParent(world, child));
        Assert.Equal(child, UnorderedHierarchy.GetParent(world, grandChild));
    }

    [Fact]
    [Trait("Category", "Performance")]
    public void Update_WarmedNoop_DoesNotAllocate()
    {
        var world = new World();
        var parent = world.CreateEntity();
        var child = world.CreateEntity();

        UnorderedHierarchy.Attach(world, child, parent);
        UnorderedHierarchy.Update(world); // warm

        long before = GC.GetAllocatedBytesForCurrentThread();
        UnorderedHierarchy.Update(world);
        long after = GC.GetAllocatedBytesForCurrentThread();

        Assert.Equal(0, after - before);
    }

    [Fact]
    [Trait("Category", "Performance")]
    public void Update_WarmedDeferredReparent_DoesNotAllocate()
    {
        var world = new World();
        var oldParent = world.CreateEntity();
        var newParent = world.CreateEntity();
        var child = world.CreateEntity();

        UnorderedHierarchy.Attach(world, child, oldParent);
        world.Replace(child, new Parent { Value = newParent });
        UnorderedHierarchy.Update(world); // warm deferred reparent path

        world.Replace(child, new Parent { Value = oldParent });

        long before = GC.GetAllocatedBytesForCurrentThread();
        UnorderedHierarchy.Update(world);
        long after = GC.GetAllocatedBytesForCurrentThread();

        Assert.Equal(0, after - before);
    }

    [Fact]
    public void Update_RecomputesUserWrittenDepthWhenParentTransitionIsProcessed()
    {
        var world = new World();
        var oldParent = world.CreateEntity();
        var newParent = world.CreateEntity();
        var child = world.CreateEntity();

        UnorderedHierarchy.Attach(world, child, oldParent);
        world.Replace(child, new Depth { Value = 99 });
        world.Replace(child, new Parent { Value = newParent });

        UnorderedHierarchy.Update(world);

        Assert.Equal(1, world.Read<Depth>(child).Value);
        Assert.Empty(UnorderedHierarchy.GetChildren(world, oldParent).ToArray());
        Assert.Equal(new[] { child }, UnorderedHierarchy.GetChildren(world, newParent).ToArray());
    }

    [Fact]
    public void Update_ProcessesParentWrittenThroughSet()
    {
        var world = new World();
        var oldParent = world.CreateEntity();
        var newParent = world.CreateEntity();
        var child = world.CreateEntity();

        UnorderedHierarchy.Attach(world, child, oldParent);
        UnorderedHierarchy.Update(world);

        world.Replace(child, new Parent { Value = newParent });

        Assert.Equal(new[] { child }, UnorderedHierarchy.GetChildren(world, oldParent).ToArray());

        UnorderedHierarchy.Update(world);

        Assert.Empty(UnorderedHierarchy.GetChildren(world, oldParent).ToArray());
        Assert.Equal(new[] { child }, UnorderedHierarchy.GetChildren(world, newParent).ToArray());
        Assert.Equal(1, world.Read<Depth>(child).Value);
    }

    [Fact]
    public void Update_DoesNotCommitDirtyVersionAfterFailedValidation()
    {
        var world = new World();
        var parent = world.CreateEntity();
        var child = world.CreateEntity();

        UnorderedHierarchy.Attach(world, child, parent);
        UnorderedHierarchy.Update(world);

        world.Replace(child, new Parent { Value = child });

        Assert.Throws<InvalidOperationException>(() => UnorderedHierarchy.Update(world));
        Assert.Throws<InvalidOperationException>(() => UnorderedHierarchy.Update(world));
    }
}
