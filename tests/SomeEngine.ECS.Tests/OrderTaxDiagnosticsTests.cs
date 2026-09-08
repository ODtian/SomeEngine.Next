using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Hierarchy;
using SomeEngine.ECS.Relations;

namespace SomeEngine.ECS.Tests;

public sealed class OrderTaxDiagnosticsTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HierarchyMaintenancePreservesChildrenAndSupportsReparenting(bool ordered)
    {
        var world = new World();
        Entity parent = world.CreateEntity();
        Entity other = world.CreateEntity();
        Entity first = world.CreateEntity();
        Entity second = world.CreateEntity();
        if (ordered)
            Hierarchy<DiagnosticDomain>.SetChildOrderPolicy(world, parent, ChildOrderPolicy.Ordered);
        Hierarchy<DiagnosticDomain>.SetParent(world, first, parent);
        Hierarchy<DiagnosticDomain>.SetParentDeferred(world, second, parent);
        Hierarchy<DiagnosticDomain>.Maintain(world);
        Entity[] children = Hierarchy<DiagnosticDomain>.GetChildren(world, parent).ToArray();
        Assert.Equal(2, children.Length);
        Assert.Contains(first, children);
        Assert.Contains(second, children);
        if (ordered) Assert.Equal(new[] { first, second }, children);
        Hierarchy<DiagnosticDomain>.SetParentDeferred(world, second, other);
        Hierarchy<DiagnosticDomain>.Maintain(world);
        Assert.Equal(new[] { first }, Hierarchy<DiagnosticDomain>.GetChildren(world, parent).ToArray());
        Assert.Equal(new[] { second }, Hierarchy<DiagnosticDomain>.GetChildren(world, other).ToArray());
    }
    [Fact]
    public void RelationMaintenanceUpdatesBothEndpointsAndDestroyRemovesEdge()
    {
        var world = new World();
        Entity source = world.CreateEntity();
        Entity target = world.CreateEntity();
        Entity next = world.CreateEntity();
        RelationEdge<DiagnosticRelation> edge = world.CreateRelation(source, target, new DiagnosticRelation());
        world.RetargetRelationDeferred(edge, source, next);
        world.MaintainRelations<DiagnosticRelation>();
        Assert.Empty(world.GetIncomingRelations<DiagnosticRelation>(target).Entries.ToArray());
        Assert.Equal(edge, Assert.Single(world.GetIncomingRelations<DiagnosticRelation>(next).Entries.ToArray()).Edge);
        world.DestroyRelation(edge);
        Assert.False(world.IsAlive(edge.Entity));
        Assert.Empty(world.GetOutgoingRelations<DiagnosticRelation>(source).Entries.ToArray());
        Assert.Empty(world.GetIncomingRelations<DiagnosticRelation>(next).Entries.ToArray());
    }

    [Fact]
    public void ImmutableInverseLookups_UsePublishedGenerationWithoutEndpointLiveness()
    {
        var world = new World();
        Entity parent = world.CreateEntity();
        Entity child = world.CreateEntity();
        Hierarchy<DiagnosticDomain>.SetParent(world, child, parent);
        HierarchyChildrenSnapshot<DiagnosticDomain> pinnedChildren =
            Hierarchy<DiagnosticDomain>.GetChildren(world, parent);

        world.DestroyEntity(parent);

        Assert.Equal(new[] { child }, pinnedChildren.ToArray());
        Assert.Empty(Hierarchy<DiagnosticDomain>.GetChildren(world, parent).ToArray());
        Assert.Empty(Hierarchy<DiagnosticDomain>.GetChildren(world, Entity.Null).ToArray());

        Entity source = world.CreateEntity();
        Entity target = world.CreateEntity();
        RelationEdge<DiagnosticRelation> edge = world.CreateRelation(
            source,
            target,
            new DiagnosticRelation());
        RelationAdjacencySnapshot<DiagnosticRelation> pinnedAdjacency =
            world.GetOutgoingRelations<DiagnosticRelation>(source);

        world.DestroyEntity(source);

        Assert.Equal(edge, Assert.Single(pinnedAdjacency.Entries.ToArray()).Edge);
        Assert.Empty(world.GetOutgoingRelations<DiagnosticRelation>(source).Entries.ToArray());
        Assert.Empty(world.GetOutgoingRelations<DiagnosticRelation>(Entity.Null).Entries.ToArray());
    }
    private readonly struct DiagnosticDomain : IHierarchyDomain;
    [RelationSchema(RelationDirection.Directed, RelationCardinality.Parallel)]
    private readonly struct DiagnosticRelation : IComponent;
}
