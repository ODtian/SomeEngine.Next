using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Hierarchy;
using SomeEngine.ECS.Relations;
using Xunit;

namespace SomeEngine.ECS.Tests;

public class RelationTests
{
    [Fact]
    public void RelationChanges_Record()
    {
        var world = new World();
        var source = world.CreateEntity();
        var target = world.CreateEntity();

        uint lastVersion = world.AcquireSystemTick();
        world.AddRelation(source, target, new Likes { Strength = 1f });
        world.ReplaceRelation(source, target, new Likes { Strength = 2f });
        world.RemoveRelation<Likes>(source, target);

        var changes = world.RelationChanges<Likes>(lastVersion).ToArray();

        Assert.Equal(3, changes.Length);
        Assert.Equal(RelationChangeKind.Added, changes[0].Kind);
        Assert.Equal(source, changes[0].Source);
        Assert.Equal(target, changes[0].Target);
        Assert.Equal(Entity.Null, changes[0].OldTarget);
        Assert.Equal(1f, changes[0].Value.Strength);

        Assert.Equal(RelationChangeKind.Changed, changes[1].Kind);
        Assert.Equal(target, changes[1].Target);
        Assert.Equal(target, changes[1].OldTarget);
        Assert.Equal(1f, changes[1].OldValue.Strength);
        Assert.Equal(2f, changes[1].Value.Strength);

        Assert.Equal(RelationChangeKind.Removed, changes[2].Kind);
        Assert.Equal(target, changes[2].Target);
        Assert.Equal(target, changes[2].OldTarget);
        Assert.Equal(2f, changes[2].OldValue.Strength);
    }

    [Fact]
    public void ExclusiveChanges_Record()
    {
        var world = new World();
        var source = world.CreateEntity();
        var oldTarget = world.CreateEntity();
        var newTarget = world.CreateEntity();

        world.AddRelation(source, oldTarget, new Owns { Slot = 1 });
        uint lastVersion = world.AcquireSystemTick();
        world.ReplaceRelation(source, newTarget, new Owns { Slot = 2 });

        var change = Assert.Single(world.RelationChanges<Owns>(lastVersion).ToArray());

        Assert.Equal(RelationChangeKind.Changed, change.Kind);
        Assert.Equal(source, change.Source);
        Assert.Equal(newTarget, change.Target);
        Assert.Equal(oldTarget, change.OldTarget);
        Assert.Equal(1, change.OldValue.Slot);
        Assert.Equal(2, change.Value.Slot);
    }

    [Fact]
    public void AddRelation_ProvidesForwardAndReverseLookup()
    {
        var world = new World();
        var source = world.CreateEntity();
        var target = world.CreateEntity();

        world.AddRelation(source, target, new Likes { Strength = 2.5f });

        var relation = Assert.Single(world.GetRelations<Likes>(source).ToArray());
        Assert.Equal(target, relation.Target);
        Assert.Equal(2.5f, relation.Value.Strength);
        Assert.Equal(new[] { source }, world.GetRelationSources<Likes>(target).ToArray());
        Assert.True(world.HasRelation<Likes>(source, target));
    }

    [Fact]
    public void AddRelation_SameSourceTarget_OverwritesValueWithoutDuplicating()
    {
        var world = new World();
        var source = world.CreateEntity();
        var target = world.CreateEntity();

        world.AddRelation(source, target, new Likes { Strength = 1f });
        Assert.Throws<InvalidOperationException>(
            () => world.AddRelation(source, target, new Likes { Strength = 4f }));

        var relation = Assert.Single(world.GetRelations<Likes>(source).ToArray());
        Assert.Equal(1f, relation.Value.Strength);
        Assert.Equal(new[] { source }, world.GetRelationSources<Likes>(target).ToArray());

        world.ReplaceRelation(source, target, new Likes { Strength = 4f });

        relation = Assert.Single(world.GetRelations<Likes>(source).ToArray());
        Assert.Equal(4f, relation.Value.Strength);
    }

    [Fact]
    public void ExclusiveRelation_ReAdd_OverwritesOldTarget()
    {
        var world = new World();
        var owner = world.CreateEntity();
        var slotA = world.CreateEntity();
        var slotB = world.CreateEntity();

        world.AddRelation(owner, slotA, new Owns { Slot = 1 });
        Assert.Throws<InvalidOperationException>(
            () => world.AddRelation(owner, slotB, new Owns { Slot = 2 }));

        var relation = Assert.Single(world.GetRelations<Owns>(owner).ToArray());
        Assert.Equal(slotA, relation.Target);
        Assert.Equal(1, relation.Value.Slot);
        Assert.Equal(new[] { owner }, world.GetRelationSources<Owns>(slotA).ToArray());
        Assert.Empty(world.GetRelationSources<Owns>(slotB).ToArray());

        world.ReplaceRelation(owner, slotB, new Owns { Slot = 2 });

        relation = Assert.Single(world.GetRelations<Owns>(owner).ToArray());
        Assert.Equal(slotB, relation.Target);
        Assert.Equal(2, relation.Value.Slot);
        Assert.Empty(world.GetRelationSources<Owns>(slotA).ToArray());
        Assert.Equal(new[] { owner }, world.GetRelationSources<Owns>(slotB).ToArray());
    }

    [Fact]
    public void RemoveRelation_RemovesForwardAndReverseEntries()
    {
        var world = new World();
        var source = world.CreateEntity();
        var target = world.CreateEntity();

        world.AddRelation(source, target, new Likes { Strength = 3f });
        world.RemoveRelation<Likes>(source, target);

        Assert.Empty(world.GetRelations<Likes>(source).ToArray());
        Assert.Empty(world.GetRelationSources<Likes>(target).ToArray());
        Assert.False(world.HasRelation<Likes>(source, target));
        Assert.Throws<InvalidOperationException>(
            () => world.RemoveRelation<Likes>(source, target));
    }

    [Fact]
    public void DestroyingSource_CleansOutgoingRelations()
    {
        var world = new World();
        var source = world.CreateEntity();
        var target = world.CreateEntity();

        world.AddRelation(source, target, new Likes { Strength = 5f });
        world.DestroyEntity(source);

        Assert.Empty(world.GetRelationSources<Likes>(target).ToArray());
    }

    [Fact]
    public void DestroyingTarget_CleansIncomingRelations()
    {
        var world = new World();
        var source = world.CreateEntity();
        var target = world.CreateEntity();

        world.AddRelation(source, target, new Likes { Strength = 5f });
        world.DestroyEntity(target);

        Assert.Empty(world.GetRelations<Likes>(source).ToArray());
    }

    [Fact]
    public void MultipleSourcesToSameTarget_DestroyingTarget_CleansAllIncomingRelations()
    {
        var world = new World();
        var sourceA = world.CreateEntity();
        var sourceB = world.CreateEntity();
        var target = world.CreateEntity();

        world.AddRelation(sourceA, target, new Likes { Strength = 1f });
        world.AddRelation(sourceB, target, new Likes { Strength = 2f });

        world.DestroyEntity(target);

        Assert.Empty(world.GetRelations<Likes>(sourceA).ToArray());
        Assert.Empty(world.GetRelations<Likes>(sourceB).ToArray());
    }

    [Fact]
    public void NonExclusiveRelation_AllowsMultipleTargetsPerSource()
    {
        var world = new World();
        var source = world.CreateEntity();
        var targetA = world.CreateEntity();
        var targetB = world.CreateEntity();

        world.AddRelation(source, targetA, new Likes { Strength = 1f });
        world.AddRelation(source, targetB, new Likes { Strength = 3f });

        var relations = world.GetRelations<Likes>(source).ToArray();

        Assert.Equal(2, relations.Length);
        Assert.Contains(relations, relation => relation.Target == targetA && relation.Value.Strength == 1f);
        Assert.Contains(relations, relation => relation.Target == targetB && relation.Value.Strength == 3f);
        Assert.Equal(new[] { source }, world.GetRelationSources<Likes>(targetA).ToArray());
        Assert.Equal(new[] { source }, world.GetRelationSources<Likes>(targetB).ToArray());
    }

    [Fact]
    public void DestroyingRoot_CleansRelationsOwnedByDestroyedDescendants()
    {
        var world = new World();
        var root = world.CreateEntity();
        var child = world.CreateEntity();
        var external = world.CreateEntity();

        UnorderedHierarchy.Attach(world, child, root);
        world.AddRelation(child, external, new Likes { Strength = 7f });

        UnorderedHierarchy.DestroySubtree(world, root);

        Assert.False(world.IsAlive(root));
        Assert.False(world.IsAlive(child));
        Assert.Empty(world.GetRelationSources<Likes>(external).ToArray());
    }

    [Fact]
    public void DestroyingNonHierarchyEntity_WithRelation_CleansRelation()
    {
        var world = new World();
        var source = world.CreateEntity();
        var target = world.CreateEntity();

        world.AddRelation(source, target, new Likes { Strength = 9f });
        world.DestroyEntity(source);

        Assert.False(world.IsAlive(source));
        Assert.Empty(world.GetRelationSources<Likes>(target).ToArray());
    }

    [Fact]
    [Trait("Category", "Performance")]
    public void DestroyEntity_WithRelations_WarmedCleanup_DoesNotAllocate()
    {
        var world = new World();

        var warmEntity = world.CreateEntity();
        var warmTargetA = world.CreateEntity();
        var warmTargetB = world.CreateEntity();
        var warmSourceA = world.CreateEntity();
        var warmSourceB = world.CreateEntity();
        world.AddRelation(warmEntity, warmTargetA, new Likes { Strength = 1f });
        world.AddRelation(warmEntity, warmTargetB, new Likes { Strength = 2f });
        world.AddRelation(warmSourceA, warmEntity, new Likes { Strength = 3f });
        world.AddRelation(warmSourceB, warmEntity, new Likes { Strength = 4f });
        world.DestroyEntity(warmEntity);

        var entity = world.CreateEntity();
        var targetA = world.CreateEntity();
        var targetB = world.CreateEntity();
        var sourceA = world.CreateEntity();
        var sourceB = world.CreateEntity();
        world.AddRelation(entity, targetA, new Likes { Strength = 5f });
        world.AddRelation(entity, targetB, new Likes { Strength = 6f });
        world.AddRelation(sourceA, entity, new Likes { Strength = 7f });
        world.AddRelation(sourceB, entity, new Likes { Strength = 8f });

        long before = GC.GetAllocatedBytesForCurrentThread();
        world.DestroyEntity(entity);
        long after = GC.GetAllocatedBytesForCurrentThread();

        Assert.Equal(0, after - before);
        Assert.Empty(world.GetRelationSources<Likes>(targetA).ToArray());
        Assert.Empty(world.GetRelationSources<Likes>(targetB).ToArray());
        Assert.Empty(world.GetRelations<Likes>(sourceA).ToArray());
        Assert.Empty(world.GetRelations<Likes>(sourceB).ToArray());
    }
}
