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
        Entity parent = world.Create();
        Entity other = world.Create();
        Entity first = world.Create();
        Entity second = world.Create();
        if (ordered)
            Hierarchy<DiagnosticDomain>.SetOrdered(world, parent, true);
        Hierarchy<DiagnosticDomain>.SetParent(world, first, parent);
        Hierarchy<DiagnosticDomain>.SetParentDeferred(world, second, parent);
        Hierarchy<DiagnosticDomain>.Maintain(world);
        Entity[] children = ReadChildren<DiagnosticDomain>(world, parent).ToArray();
        Assert.Equal(2, children.Length);
        Assert.Contains(first, children);
        Assert.Contains(second, children);
        if (ordered) Assert.Equal(new[] { first, second }, children);
        Hierarchy<DiagnosticDomain>.SetParentDeferred(world, second, other);
        Hierarchy<DiagnosticDomain>.Maintain(world);
        Assert.Equal(new[] { first }, ReadChildren<DiagnosticDomain>(world, parent).ToArray());
        Assert.Equal(new[] { second }, ReadChildren<DiagnosticDomain>(world, other).ToArray());
    }
    [Fact]
    public void RelationMaintenanceUpdatesBothEndpointsAndDestroyRemovesEdge()
    {
        var world = new World();
        Entity source = world.Create();
        Entity target = world.Create();
        Entity next = world.Create();
        RelationEdge<DiagnosticRelation> edge = world.CreateRelation(source, target, new DiagnosticRelation());
        world.RetargetRelationDeferred(edge, source, next);
        world.MaintainRelations<DiagnosticRelation>();
        Assert.Empty(ReadIncoming<DiagnosticRelation>(world, target).ToArray());
        Assert.Equal(edge, Assert.Single(ReadIncoming<DiagnosticRelation>(world, next).ToArray()).Edge);
        world.DestroyRelation(edge);
        world.Remove<Removed<RelationEndpoints<DiagnosticRelation>>>(edge.Entity);
        Assert.False(world.IsAlive(edge.Entity));
        Assert.Empty(ReadOutgoing<DiagnosticRelation>(world, source).ToArray());
        Assert.Empty(ReadIncoming<DiagnosticRelation>(world, next).ToArray());
    }

    [Fact]
    public void ImmutableInverseLookups_UsePublishedGenerationWithoutEndpointLiveness()
    {
        var world = new World();
        Entity parent = world.Create();
        Entity child = world.Create();
        Hierarchy<DiagnosticDomain>.SetParent(world, child, parent);
        Entity[] pinnedChildren =
            ReadChildren<DiagnosticDomain>(world, parent);

        world.Destroy(parent);

        Assert.Equal(new[] { child }, pinnedChildren.ToArray());
        Assert.Empty(ReadChildren<DiagnosticDomain>(world, parent).ToArray());
        Assert.Empty(ReadChildren<DiagnosticDomain>(world, Entity.Null).ToArray());

        Entity source = world.Create();
        Entity target = world.Create();
        RelationEdge<DiagnosticRelation> edge = world.CreateRelation(
            source,
            target,
            new DiagnosticRelation());
        var pinnedAdjacency =
            ReadOutgoing<DiagnosticRelation>(world, source);

        world.Destroy(source);

        Assert.Equal(edge, Assert.Single(pinnedAdjacency).Edge);
        Assert.Empty(ReadOutgoing<DiagnosticRelation>(world, source).ToArray());
        Assert.Empty(ReadOutgoing<DiagnosticRelation>(world, Entity.Null).ToArray());
    }
    private readonly struct DiagnosticDomain : IHierarchyDomain;
    [RelationSchema(RelationDirection.Directed, RelationCardinality.Parallel)]
    private readonly struct DiagnosticRelation : IAddedComponent;
}
