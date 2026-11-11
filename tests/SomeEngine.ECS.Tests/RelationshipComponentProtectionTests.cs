using SomeEngine.ECS.Commands;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Hierarchy;
using SomeEngine.ECS.Relations;

namespace SomeEngine.ECS.Tests;

public sealed class RelationshipComponentProtectionTests
{
    [Fact]
    public void OrdinaryAndNativeRelationshipComponentsKeepTheirPublicMutationRules()
    {
        using var world = new World();
        Entity parent = world.Create();
        Entity child = world.Create(new RegularValue { Value = 1 });
        Hierarchy<ProtectionDomain>.SetParent(world, child, parent);
        world.Set(child, new RegularValue { Value = 2 });
        Assert.Equal(2, world.Read<RegularValue>(child).Value);
        Assert.Equal(parent, world.Read<Parent<ProtectionDomain>>(child).Value);
        Assert.Equal([child], Children(world, parent));
        Assert.Throws<InvalidOperationException>(() => world.Get<Parent<ProtectionDomain>>(child));
    }

    [Fact]
    public void DirectedEdgesRetainTheirTwoEndpointRoles()
    {
        using var world = new World();
        Entity source = world.Create();
        Entity target = world.Create();
        RelationEdge<RegularValue> edge = Relations<RegularValue>.CreateEdge(world, source, target, new RegularValue { Value = 3 });
        RelationEndpoints<RegularValue> endpoints = world.Read<RelationEndpoints<RegularValue>>(edge.Entity);
        Assert.Equal(source, endpoints.Source);
        Assert.Equal(target, endpoints.Target);
        using RelationRead outgoing = Relations<RegularValue>.Outgoing(world, source);
        using RelationRead incoming = Relations<RegularValue>.Incoming(world, target);
        Assert.Equal([new RelationEntry(target, edge.Entity)], outgoing.Entries.ToArray());
        Assert.Equal([new RelationEntry(source, edge.Entity)], incoming.Entries.ToArray());
    }

    [Fact]
    public void GenericRelationshipMutationRequiresTypedHierarchyApi()
    {
        using var world = new World();
        Entity parent = world.Create();
        Entity child = world.Create();
        Entity spare = world.Create();
        Hierarchy<ProtectionDomain>.SetParent(world, child, parent);
        Assert.Throws<InvalidOperationException>(() => world.Add(spare, new Parent<ProtectionDomain>(parent)));
        Assert.Throws<InvalidOperationException>(() => world.Add(spare, default(Children<ProtectionDomain>)));
        Assert.Throws<InvalidOperationException>(() => world.Set(child, new Parent<ProtectionDomain>(Entity.Null)));
        Assert.Throws<InvalidOperationException>(() => world.Set(parent, default(Children<ProtectionDomain>)));
        Assert.Throws<InvalidOperationException>(() => world.Remove<Parent<ProtectionDomain>>(child));
        Assert.Throws<InvalidOperationException>(() => world.Remove<Children<ProtectionDomain>>(parent));
        Assert.Equal(parent, Hierarchy<ProtectionDomain>.GetParent(world, child));
        Assert.Equal([child], Children(world, parent));
    }

    [Fact]
    public void CommandRelationshipMutationsFailBeforeRecording()
    {
        using var world = new World();
        Entity parent = world.Create();
        Entity entity = world.Create();
        Hierarchy<ProtectionDomain>.SetParent(world, entity, parent);
        using var commands = new CommandBuffer(world);
        Assert.Throws<InvalidOperationException>(() => commands.Add(entity, new Parent<ProtectionDomain>(parent)));
        Assert.Throws<InvalidOperationException>(() => commands.Add(entity, default(Children<ProtectionDomain>)));
        Assert.Throws<InvalidOperationException>(() => commands.Set(entity, new Parent<ProtectionDomain>(parent)));
        Assert.Throws<InvalidOperationException>(() => commands.Remove<Parent<ProtectionDomain>>(entity));
        Assert.Equal(0, commands.Count);
        Assert.Equal(2, world.EntityCount);
        Assert.Equal(parent, Hierarchy<ProtectionDomain>.GetParent(world, entity));
    }

    [Fact]
    public void EntityCloningInitializesTheCopiedParentThroughNativeHierarchyMaintenance()
    {
        using var world = new World();
        Entity parent = world.Create();
        Entity source = world.Create(new RegularValue { Value = 4 });
        Hierarchy<ProtectionDomain>.SetParent(world, source, parent);
        Entity clone = world.Clone(source);
        Assert.Equal(4, world.Read<RegularValue>(clone).Value);
        Assert.Equal(parent, Hierarchy<ProtectionDomain>.GetParent(world, clone));
        Assert.Equal([source, clone], Children(world, parent));
    }

    [Fact]
    public void OrdinaryComponentMutation_RemainsUnchanged()
    {
        using var world = new World();
        Entity entity = world.Create(new RegularValue { Value = 1 });
        world.Set(entity, new RegularValue { Value = 2 });
        Assert.Equal(2, world.Read<RegularValue>(entity).Value);
        world.Set(entity, new RegularValue { Value = 3 });
        Assert.Equal(3, world.Read<RegularValue>(entity).Value);
        world.Remove<RegularValue>(entity);
        Assert.False(world.Has<RegularValue>(entity));
    }

    [Fact]
    public void CreatingAParentValueInitializesItsNativeInverse()
    {
        using var world = new World();
        Entity parent = world.Create();
        Entity child = world.Create(new Parent<ProtectionDomain>(parent));
        Assert.Equal(parent, Hierarchy<ProtectionDomain>.GetParent(world, child));
        Assert.Equal([child], Children(world, parent));
    }

    private static Entity[] Children(World world, Entity parent)
    {
        using ChildrenRead children = Hierarchy<ProtectionDomain>.GetChildren(world, parent);
        return children.Entities.ToArray();
    }
    private readonly struct ProtectionDomain : IHierarchyDomain;
    private struct RegularValue : IAddedComponent { public int Value; }
}
