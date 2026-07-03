using SomeEngine.ECS;
using SomeEngine.ECS.Commands;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Hierarchy;
using SomeEngine.ECS.Registry;
using Xunit;

namespace SomeEngine.ECS.Tests;

public class HierarchyMutationTests
{
    [Fact]
    public void DirectParentAdd_IsProcessedByDeferredUpdate()
    {
        var world = new World();
        var parent = world.CreateEntity();
        var child = world.CreateEntity();

        world.Add(child, new Parent { Value = parent });

        Assert.Empty(UnorderedHierarchy.GetChildren(world, parent).ToArray());

        UnorderedHierarchy.Update(world);

        Assert.Equal(parent, UnorderedHierarchy.GetParent(world, child));
        Assert.Equal(new[] { child }, UnorderedHierarchy.GetChildren(world, parent).ToArray());
        Assert.Equal(1, world.Read<Depth>(child).Value);
    }

    [Fact]
    public void DirectParentAdd_UsesDirtyPath()
    {
        var world = new World();
        var parent = world.CreateEntity();
        var child = world.CreateEntity();

        world.Add(child, new Parent { Value = parent });

        Assert.True(world.Hierarchy.ShouldCollectDirty);
        Assert.False(world.Hierarchy.ScanNeeded);
    }

    [Fact]
    public void BundleParentWrite_UsesDirtyPath()
    {
        var world = new World();
        var parent = world.CreateEntity();
        Span<int> componentIds = [ComponentMetadata<Parent>.Id];

        var writer = world.CreateSpawnWriter(componentIds);
        writer.Write(new Parent { Value = parent });

        Assert.True(world.Hierarchy.ShouldCollectDirty);
        Assert.False(world.Hierarchy.ScanNeeded);
    }

    [Fact]
    public void BatchParentWrite_RequiresScan()
    {
        var world = new World();
        using var batch = world.SpawnBatch<Parent>(1);

        Assert.False(world.Hierarchy.ShouldCollectDirty);
        Assert.True(world.Hierarchy.ScanNeeded);
    }

    [Fact]
    public void GetChildren_DoesNotMarkChildBufferChanged()
    {
        var world = new World();
        var parent = world.CreateEntity();
        var child = world.CreateEntity();
        UnorderedHierarchy.Attach(world, child, parent);

        var cache = world.CreateQuery().With<ChildBuffer>().Build();
        var archetype = Assert.Single(cache.Archetypes);
        var chunk = Assert.Single(archetype.Chunks);
        int column = archetype.Column(ComponentMetadata<ChildBuffer>.Id);
        uint before = chunk.ChangeVersions[column];

        Assert.Equal(new[] { child }, UnorderedHierarchy.GetChildren(world, parent).ToArray());

        Assert.Equal(before, chunk.ChangeVersions[column]);
    }

    [Fact]
    public void HierarchyChanges_Record()
    {
        var world = new World();
        var oldParent = world.CreateEntity();
        var newParent = world.CreateEntity();
        var child = world.CreateEntity();

        uint lastVersion = world.AcquireSystemTick();
        UnorderedHierarchy.Attach(world, child, oldParent);
        UnorderedHierarchy.Move(world, child, newParent);
        UnorderedHierarchy.Detach(world, child);

        var changes = world.HierarchyChanges(lastVersion).ToArray();

        Assert.Equal(3, changes.Length);
        Assert.Equal(HierarchyChangeKind.Added, changes[0].Kind);
        Assert.Equal(child, changes[0].Child);
        Assert.Equal(Entity.Null, changes[0].OldParent);
        Assert.Equal(oldParent, changes[0].NewParent);
        Assert.Equal(-1, changes[0].OldIndex);
        Assert.Equal(0, changes[0].NewIndex);

        Assert.Equal(HierarchyChangeKind.Changed, changes[1].Kind);
        Assert.Equal(oldParent, changes[1].OldParent);
        Assert.Equal(newParent, changes[1].NewParent);
        Assert.Equal(0, changes[1].OldIndex);
        Assert.Equal(0, changes[1].NewIndex);

        Assert.Equal(HierarchyChangeKind.Removed, changes[2].Kind);
        Assert.Equal(newParent, changes[2].OldParent);
        Assert.Equal(Entity.Null, changes[2].NewParent);
        Assert.Equal(0, changes[2].OldIndex);
        Assert.Equal(-1, changes[2].NewIndex);
    }

    [Fact]
    public void OrderedReorder_Record()
    {
        var world = new World();
        var parent = world.CreateEntity();
        var first = world.CreateEntity();
        var second = world.CreateEntity();
        var third = world.CreateEntity();

        OrderedHierarchy.Attach(world, first, parent);
        OrderedHierarchy.Attach(world, second, parent);
        OrderedHierarchy.Attach(world, third, parent);

        uint lastVersion = world.AcquireSystemTick();
        OrderedHierarchy.Reorder(world, third, 0);

        var change = Assert.Single(world.HierarchyChanges(lastVersion).ToArray());

        Assert.Equal(HierarchyChangeKind.Reordered, change.Kind);
        Assert.Equal(third, change.Child);
        Assert.Equal(parent, change.OldParent);
        Assert.Equal(parent, change.NewParent);
        Assert.Equal(2, change.OldIndex);
        Assert.Equal(0, change.NewIndex);
        Assert.Equal(new[] { third, first, second }, OrderedHierarchy.GetChildren(world, parent).ToArray());
        Assert.Equal(0, world.Read<HierarchyLink>(third).ChildIndex);
        Assert.Equal(1, world.Read<HierarchyLink>(first).ChildIndex);
        Assert.Equal(2, world.Read<HierarchyLink>(second).ChildIndex);
    }

    [Fact]
    public void AttachExistingThrows()
    {
        var world = new World();
        var parent = world.CreateEntity();
        var child = world.CreateEntity();

        UnorderedHierarchy.Attach(world, child, parent);

        Assert.Throws<InvalidOperationException>(() => UnorderedHierarchy.Attach(world, child, parent));
    }

    [Fact]
    public void MoveMissingThrows()
    {
        var world = new World();
        var parent = world.CreateEntity();
        var child = world.CreateEntity();

        Assert.Throws<InvalidOperationException>(() => UnorderedHierarchy.Move(world, child, parent));
    }

    [Fact]
    public void ReorderMissingThrows()
    {
        var world = new World();
        var child = world.CreateEntity();

        Assert.Throws<InvalidOperationException>(() => OrderedHierarchy.Reorder(world, child, 0));
    }

    [Fact]
    public void CommandBufferRemoveParent_IsProcessedByDeferredUpdate()
    {
        var world = new World();
        var parent = world.CreateEntity();
        var child = world.CreateEntity();
        using var commandBuffer = new CommandBuffer(world);

        UnorderedHierarchy.Attach(world, child, parent);

        commandBuffer.Remove<Parent>(child);
        commandBuffer.Playback();

        Assert.Equal(new[] { child }, UnorderedHierarchy.GetChildren(world, parent).ToArray());

        UnorderedHierarchy.Update(world);

        Assert.Equal(Entity.Null, UnorderedHierarchy.GetParent(world, child));
        Assert.Empty(UnorderedHierarchy.GetChildren(world, parent).ToArray());
        Assert.Equal(0, world.Read<Depth>(child).Value);
        Assert.False(world.Has<HierarchyLink>(child));
    }

    [Fact]
    public void ExplicitRemoveParent_RemovesRootHierarchyLink()
    {
        var world = new World();
        var parent = world.CreateEntity();
        var child = world.CreateEntity();

        UnorderedHierarchy.Attach(world, child, parent);

        Assert.True(world.Has<HierarchyLink>(child));

        UnorderedHierarchy.Detach(world, child);

        Assert.Equal(Entity.Null, UnorderedHierarchy.GetParent(world, child));
        Assert.Empty(UnorderedHierarchy.GetChildren(world, parent).ToArray());
        Assert.False(world.Has<HierarchyLink>(child));
    }

    [Fact]
    public void DerivedCaches_AreOrdinaryWorldComponents()
    {
        var world = new World();
        var entity = world.CreateEntity();

        world.Add(entity, new Depth { Value = 4 });
        world.Add(entity, new ChildBuffer());

        Assert.Equal(4, world.Read<Depth>(entity).Value);
        Assert.True(world.Has<ChildBuffer>(entity));
    }

    [Fact]
    public void DerivedCaches_CanBeSetThroughWorld()
    {
        var world = new World();
        var parent = world.CreateEntity();
        var child = world.CreateEntity();

        UnorderedHierarchy.Attach(world, child, parent);

        world.Replace(child, new Depth { Value = 99 });
        world.Replace(child, new Depth { Value = 7 });

        ref var childBuffer = ref world.Get<ChildBuffer>(parent);
        Assert.Contains(child, childBuffer.Children.AsSpan().ToArray());
        Assert.Contains(child, world.Read<ChildBuffer>(parent).Children.AsSpan().ToArray());
        Assert.Equal(7, world.Read<Depth>(child).Value);
    }

    [Fact]
    public void RepeatedDirectParentWrites_CollapseToCurrentFact()
    {
        var world = new World();
        var parentA = world.CreateEntity();
        var parentB = world.CreateEntity();
        var child = world.CreateEntity();

        UnorderedHierarchy.Attach(world, child, parentA);

        world.Replace(child, new Parent { Value = parentB });
        world.Replace(child, new Parent { Value = parentA });

        UnorderedHierarchy.Update(world);

        Assert.Equal(parentA, UnorderedHierarchy.GetParent(world, child));
        Assert.Equal(new[] { child }, UnorderedHierarchy.GetChildren(world, parentA).ToArray());
        Assert.Empty(UnorderedHierarchy.GetChildren(world, parentB).ToArray());
    }

    [Fact]
    public void WideDeferredParentAdds_UpdateCreatesUniqueChildBuffer()
    {
        var world = new World();
        var parent = world.CreateEntity();
        var children = CreateEntities(world, 128);

        foreach (var child in children)
            world.Add(child, new Parent { Value = parent });

        UnorderedHierarchy.Update(world);
        UnorderedHierarchy.Update(world);

        var parentChildren = UnorderedHierarchy.GetChildren(world, parent).ToArray();
        Assert.Equal(children.Length, parentChildren.Length);
        foreach (var child in children)
        {
            Assert.Contains(child, parentChildren);
            Assert.Equal(parent, UnorderedHierarchy.GetParent(world, child));
            Assert.Equal(1, world.Read<Depth>(child).Value);
            Assert.True(world.Has<HierarchyLink>(child));
            Assert.Equal(parent, world.Read<HierarchyLink>(child).Parent);
        }
    }

    [Fact]
    public void WideDeferredReparent_UpdateMovesChildrenInBulk()
    {
        var world = new World();
        var oldParent = world.CreateEntity();
        var newParent = world.CreateEntity();
        var children = CreateEntities(world, 128);

        foreach (var child in children)
            UnorderedHierarchy.Attach(world, child, oldParent);

        foreach (var child in children)
            world.Replace(child, new Parent { Value = newParent });

        UnorderedHierarchy.Update(world);

        Assert.Empty(UnorderedHierarchy.GetChildren(world, oldParent).ToArray());
        var newParentChildren = UnorderedHierarchy.GetChildren(world, newParent).ToArray();
        Assert.Equal(children.Length, newParentChildren.Length);
        foreach (var child in children)
        {
            Assert.Contains(child, newParentChildren);
            Assert.Equal(newParent, UnorderedHierarchy.GetParent(world, child));
            Assert.Equal(1, world.Read<Depth>(child).Value);
            Assert.Equal(newParent, world.Read<HierarchyLink>(child).Parent);
        }
    }

    [Fact]
    public void WideDeferredReparent_MaintainsUnorderedChildIndices()
    {
        var world = new World();
        var oldParent = world.CreateEntity();
        var newParent = world.CreateEntity();
        var children = CreateEntities(world, 16);

        foreach (var child in children)
            world.Add(child, new Parent { Value = oldParent });

        UnorderedHierarchy.Update(world);

        for (int i = 0; i < 4; i++)
            world.Replace(children[i], new Parent { Value = newParent });

        UnorderedHierarchy.Update(world);

        AssertChildIndicesMatchBuffer(world, oldParent);
        AssertChildIndicesMatchBuffer(world, newParent);
    }

    [Fact]
    public void WideDeferredReparent_FallbackRemovalRepairsChildIndices()
    {
        var world = new World();
        var oldParent = world.CreateEntity();
        var newParent = world.CreateEntity();
        var children = CreateEntities(world, 16);

        foreach (var child in children)
            UnorderedHierarchy.Attach(world, child, oldParent);

        world.Replace(children[0], new Parent { Value = newParent });
        world.Replace(children[1], new Parent { Value = newParent });
        world.Replace(children[0], new HierarchyLink { Parent = oldParent, ChildIndex = -1 });

        UnorderedHierarchy.Update(world);

        AssertChildIndicesMatchBuffer(world, oldParent);
        AssertChildIndicesMatchBuffer(world, newParent);
    }

    [Fact]
    public void DirectUnorderedReparent_MaintainsChildIndices()
    {
        var world = new World();
        var oldParent = world.CreateEntity();
        var newParent = world.CreateEntity();
        var children = CreateEntities(world, 16);

        foreach (var child in children)
            UnorderedHierarchy.Attach(world, child, oldParent);

        for (int i = 0; i < 4; i++)
            UnorderedHierarchy.Move(world, children[i], newParent);

        AssertChildIndicesMatchBuffer(world, oldParent);
        AssertChildIndicesMatchBuffer(world, newParent);

        UnorderedHierarchy.Detach(world, children[4]);
        AssertChildIndicesMatchBuffer(world, oldParent);
    }

    [Fact]
    public void WideDeferredRemove_UpdateClearsParentFactsAndCache()
    {
        var world = new World();
        var parent = world.CreateEntity();
        var children = CreateEntities(world, 128);

        foreach (var child in children)
            UnorderedHierarchy.Attach(world, child, parent);

        foreach (var child in children)
            world.Remove<Parent>(child);

        UnorderedHierarchy.Update(world);

        Assert.Empty(UnorderedHierarchy.GetChildren(world, parent).ToArray());
        foreach (var child in children)
        {
            Assert.False(world.Has<Parent>(child));
            Assert.Equal(Entity.Null, UnorderedHierarchy.GetParent(world, child));
            Assert.Equal(0, world.Read<Depth>(child).Value);
            Assert.False(world.Has<HierarchyLink>(child));
        }
    }

    [Fact]
    public void QueryRowParentWrite_IsProcessedByDeferredUpdate()
    {
        var world = new World();
        var oldParent = world.CreateEntity();
        var newParent = world.CreateEntity();
        var child = world.CreateEntity();

        UnorderedHierarchy.Attach(world, child, oldParent);

        var query = world.Query(world.QueryDefinition().ReadWrite<Parent>());
        foreach (var row in world.RunQuery(query).Rows)
            row.ReadWrite<Parent>().Value = newParent;

        Assert.Equal(new[] { child }, UnorderedHierarchy.GetChildren(world, oldParent).ToArray());

        UnorderedHierarchy.Update(world);

        Assert.Empty(UnorderedHierarchy.GetChildren(world, oldParent).ToArray());
        Assert.Equal(new[] { child }, UnorderedHierarchy.GetChildren(world, newParent).ToArray());
        Assert.Equal(newParent, UnorderedHierarchy.GetParent(world, child));
        AssertChildIndicesMatchBuffer(world, newParent);
    }

    [Fact]
    public void QueryChunkParentWrite_FallsBackToDeferredChunkScan()
    {
        var world = new World();
        var oldParent = world.CreateEntity();
        var newParent = world.CreateEntity();
        var children = CreateEntities(world, 16);

        foreach (var child in children)
            UnorderedHierarchy.Attach(world, child, oldParent);

        var query = world.Query(world.QueryDefinition().ReadWrite<Parent>());
        foreach (var chunk in world.RunQuery(query).Chunks)
        {
            var parents = chunk.ReadWrite<Parent>();
            for (int i = 0; i < parents.Length; i++)
                parents[i] = new Parent { Value = newParent };
        }

        UnorderedHierarchy.Update(world);

        Assert.Empty(UnorderedHierarchy.GetChildren(world, oldParent).ToArray());
        Assert.Equal(children.Length, UnorderedHierarchy.GetChildren(world, newParent).Length);
        foreach (var child in children)
            Assert.Equal(newParent, UnorderedHierarchy.GetParent(world, child));
        AssertChildIndicesMatchBuffer(world, newParent);
    }

    [Fact]
    public void BundleAddParent_IsProcessedByDeferredUpdate()
    {
        var world = new World();
        var parent = world.CreateEntity();
        var child = world.CreateEntity();
        Span<int> componentIds = [ComponentMetadata<Parent>.Id];

        var writer = world.CreateAddWriter(child, componentIds);
        writer.Write(new Parent { Value = parent });

        Assert.Empty(UnorderedHierarchy.GetChildren(world, parent).ToArray());

        UnorderedHierarchy.Update(world);

        Assert.Equal(parent, UnorderedHierarchy.GetParent(world, child));
        Assert.Equal(new[] { child }, UnorderedHierarchy.GetChildren(world, parent).ToArray());
        Assert.Equal(1, world.Read<Depth>(child).Value);
        AssertChildIndicesMatchBuffer(world, parent);
    }

    [Fact]
    public void BundleSpawnParent_IsProcessedByDeferredUpdate()
    {
        var world = new World();
        var parent = world.CreateEntity();
        Span<int> componentIds = [ComponentMetadata<Parent>.Id];

        var writer = world.CreateSpawnWriter(componentIds);
        var child = writer.Entity;
        writer.Write(new Parent { Value = parent });

        Assert.Empty(UnorderedHierarchy.GetChildren(world, parent).ToArray());

        UnorderedHierarchy.Update(world);

        Assert.Equal(parent, UnorderedHierarchy.GetParent(world, child));
        Assert.Equal(new[] { child }, UnorderedHierarchy.GetChildren(world, parent).ToArray());
        Assert.Equal(1, world.Read<Depth>(child).Value);
        AssertChildIndicesMatchBuffer(world, parent);
    }

    private static Entity[] CreateEntities(World world, int count)
    {
        var entities = new Entity[count];
        for (int i = 0; i < entities.Length; i++)
            entities[i] = world.CreateEntity();

        return entities;
    }

    private static void AssertChildIndicesMatchBuffer(World world, Entity parent)
    {
        var children = UnorderedHierarchy.GetChildren(world, parent).ToArray();
        for (int i = 0; i < children.Length; i++)
        {
            var link = world.Read<HierarchyLink>(children[i]);
            Assert.Equal(parent, link.Parent);
            Assert.Equal(i, link.ChildIndex);
        }
    }
}
