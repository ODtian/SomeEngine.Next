using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Hierarchy;
using SomeEngine.ECS.Relations;

namespace SomeEngine.ECS.Tests;

public sealed class TopologySideOwnerCopyOnWriteTests
{
    [Fact]
    public void StructuralCandidateRollback_DetachesOnlyWrittenTopologyTypes_AndKeepsPublishedImageExact()
    {
        Fixture fixture = CreateFixture();
        World world = fixture.World;
        WorldStructureRoot published = world.PublishedStructureRoot;
        var publishedHierarchy = published.Hierarchy.Domain<CowHierarchyDomain>();
        var publishedAlternateHierarchy = published.Hierarchy.Domain<AlternateHierarchyDomain>();
        object hierarchyBacking = publishedHierarchy.BackingIdentity;
        object alternateHierarchyBacking = publishedAlternateHierarchy.BackingIdentity;
        object relationBacking = published.RelationGraph.StateBackingIdentity<CowRelation>()!;
        object alternateRelationBacking =
            published.RelationGraph.StateBackingIdentity<AlternateRelation>()!;
        HierarchyChildrenSnapshot<CowHierarchyDomain> pinnedChildren =
            publishedHierarchy.GetChildren(fixture.FirstParent);
        RelationAdjacencySnapshot<CowRelation> pinnedRelations =
            published.RelationGraph.Snapshot<CowRelation>(
                fixture.Source,
                RelationAdjacencyRole.Outgoing);
        WorldStructureRoot candidate;

        using (StructuralMutationScope mutation = world.BeginStructuralMutation())
        {
            candidate = world.ActiveStructureRoot;
            AssertSharedBeforeWrite(
                candidate,
                hierarchyBacking,
                alternateHierarchyBacking,
                relationBacking,
                alternateRelationBacking,
                fixture);

            MutateCandidate(candidate, fixture);
            AssertWrittenTypesDetached(
                published,
                candidate,
                hierarchyBacking,
                alternateHierarchyBacking,
                relationBacking,
                alternateRelationBacking);
            AssertCandidateImage(candidate, fixture);
            AssertPublishedImage(published, fixture);
            Assert.Equal([fixture.Child], pinnedChildren.ToArray());
            AssertRelationTarget(pinnedRelations, fixture.FirstTarget);
        }

        Assert.Same(published, world.PublishedStructureRoot);
        AssertPublishedImage(published, fixture);
        Assert.Equal([fixture.Child], pinnedChildren.ToArray());
        AssertRelationTarget(pinnedRelations, fixture.FirstTarget);
    }

    [Fact]
    public void StructuralCandidateCommit_PublishesDetachedTopologyTypes_AndRetainsPriorRootExactness()
    {
        Fixture fixture = CreateFixture();
        World world = fixture.World;
        WorldStructureRoot published = world.PublishedStructureRoot;
        var publishedHierarchy = published.Hierarchy.Domain<CowHierarchyDomain>();
        object hierarchyBacking = publishedHierarchy.BackingIdentity;
        object alternateHierarchyBacking =
            published.Hierarchy.Domain<AlternateHierarchyDomain>().BackingIdentity;
        object relationBacking = published.RelationGraph.StateBackingIdentity<CowRelation>()!;
        object alternateRelationBacking =
            published.RelationGraph.StateBackingIdentity<AlternateRelation>()!;
        HierarchyChildrenSnapshot<CowHierarchyDomain> pinnedChildren =
            publishedHierarchy.GetChildren(fixture.FirstParent);
        RelationAdjacencySnapshot<CowRelation> pinnedRelations =
            published.RelationGraph.Snapshot<CowRelation>(
                fixture.Source,
                RelationAdjacencyRole.Outgoing);
        WorldStructureRoot candidate;

        using (StructuralMutationScope mutation = world.BeginStructuralMutation())
        {
            candidate = world.ActiveStructureRoot;
            AssertSharedBeforeWrite(
                candidate,
                hierarchyBacking,
                alternateHierarchyBacking,
                relationBacking,
                alternateRelationBacking,
                fixture);

            MutateCandidate(candidate, fixture);
            AssertWrittenTypesDetached(
                published,
                candidate,
                hierarchyBacking,
                alternateHierarchyBacking,
                relationBacking,
                alternateRelationBacking);
            mutation.Commit();
        }

        Assert.Same(candidate, world.PublishedStructureRoot);
        AssertCandidateImage(candidate, fixture);

        // Readers retaining the previous publication and its snapshots see the exact old image.
        AssertPublishedImage(published, fixture);
        Assert.Equal([fixture.Child], pinnedChildren.ToArray());
        AssertRelationTarget(pinnedRelations, fixture.FirstTarget);
    }

    [Fact]
    public void DeferredMaintenance_IsolatesTransientTracking_ThenDetachesPublishedGenerations()
    {
        Fixture fixture = CreateFixture();
        World world = fixture.World;
        WorldStructureRoot published = world.PublishedStructureRoot;
        object hierarchyBacking =
            published.Hierarchy.Domain<CowHierarchyDomain>().BackingIdentity;
        object relationBacking = published.RelationGraph.StateBackingIdentity<CowRelation>()!;

        using (StructuralMutationScope mutation = world.BeginStructuralMutation())
        {
            WorldStructureRoot candidate = world.ActiveStructureRoot;
            var hierarchy = candidate.Hierarchy.Domain<CowHierarchyDomain>();
            hierarchy.SetParent(
                fixture.Child,
                fixture.SecondParent,
                insertIndex: null,
                immediate: false);
            candidate.RelationGraph.Retarget(
                world,
                fixture.Edge,
                fixture.Source,
                fixture.SecondTarget,
                RelationMaintenanceTiming.Deferred);

            // Hierarchy's maintenance workspace is part of its domain backing, while relation
            // dirty placement/tracker state is an isolated transient workspace. Neither path can
            // mutate the retained publication.
            Assert.NotSame(hierarchyBacking, hierarchy.BackingIdentity);
            Assert.Equal(1, hierarchy.DetachCount);
            Assert.Same(
                relationBacking,
                candidate.RelationGraph.StateBackingIdentity<CowRelation>());
            Assert.Equal(0, candidate.RelationGraph.StateDetachCount<CowRelation>());
            AssertPublishedImage(published, fixture);

            hierarchy.Maintain();
            candidate.RelationGraph.Maintain<CowRelation>(world);
            Assert.NotSame(
                relationBacking,
                candidate.RelationGraph.StateBackingIdentity<CowRelation>());
            Assert.Equal(1, candidate.RelationGraph.StateDetachCount<CowRelation>());
            AssertCandidateImage(candidate, fixture);
        }

        Assert.Same(published, world.PublishedStructureRoot);
        AssertPublishedImage(published, fixture);
    }

    [Fact]
    public void OrderChanges_DetachOnlyTheirTypedTopologyBackings()
    {
        Fixture fixture = CreateFixture();
        World world = fixture.World;
        WorldStructureRoot published = world.PublishedStructureRoot;
        object hierarchyBacking =
            published.Hierarchy.Domain<CowHierarchyDomain>().BackingIdentity;
        object relationBacking = published.RelationGraph.StateBackingIdentity<CowRelation>()!;

        using (StructuralMutationScope mutation = world.BeginStructuralMutation())
        {
            WorldStructureRoot candidate = world.ActiveStructureRoot;
            var hierarchy = candidate.Hierarchy.Domain<CowHierarchyDomain>();
            hierarchy.SetOrderPolicy(
                fixture.FirstParent,
                ChildOrderPolicy.Ordered);
            candidate.RelationGraph.SetOrderPolicy<CowRelation>(
                world,
                fixture.Source,
                RelationAdjacencyRole.Outgoing,
                RelationAdjacencyOrderPolicy.Ordered);

            Assert.NotSame(hierarchyBacking, hierarchy.BackingIdentity);
            Assert.Equal(1, hierarchy.DetachCount);
            Assert.NotSame(
                relationBacking,
                candidate.RelationGraph.StateBackingIdentity<CowRelation>());
            Assert.Equal(1, candidate.RelationGraph.StateDetachCount<CowRelation>());
            Assert.Equal(ChildOrderPolicy.Ordered, hierarchy.GetOrderPolicy(fixture.FirstParent));
            Assert.Equal(
                RelationAdjacencyOrderPolicy.Ordered,
                candidate.RelationGraph.Snapshot<CowRelation>(
                    fixture.Source,
                    RelationAdjacencyRole.Outgoing).OrderPolicy);
        }

        Assert.Equal(
            ChildOrderPolicy.Unordered,
            published.Hierarchy.Domain<CowHierarchyDomain>().GetOrderPolicy(fixture.FirstParent));
        Assert.Equal(
            RelationAdjacencyOrderPolicy.Unordered,
            published.RelationGraph.Snapshot<CowRelation>(
                fixture.Source,
                RelationAdjacencyRole.Outgoing).OrderPolicy);
    }

    [Fact]
    public void DestroyCleanup_DetachesAffectedDomainsAndRelationTypes_WithoutTouchingAlternates()
    {
        Fixture fixture = CreateFixture();
        World world = fixture.World;
        WorldStructureRoot published = world.PublishedStructureRoot;
        object hierarchyBacking =
            published.Hierarchy.Domain<CowHierarchyDomain>().BackingIdentity;
        object alternateHierarchyBacking =
            published.Hierarchy.Domain<AlternateHierarchyDomain>().BackingIdentity;
        object relationBacking = published.RelationGraph.StateBackingIdentity<CowRelation>()!;
        object alternateRelationBacking =
            published.RelationGraph.StateBackingIdentity<AlternateRelation>()!;

        using (StructuralMutationScope mutation = world.BeginStructuralMutation())
        {
            WorldStructureRoot candidate = world.ActiveStructureRoot;
            world.DestroyEntity(fixture.FirstParent);
            world.DestroyEntity(fixture.FirstTarget);

            var hierarchy = candidate.Hierarchy.Domain<CowHierarchyDomain>();
            var alternateHierarchy = candidate.Hierarchy.Domain<AlternateHierarchyDomain>();
            Assert.NotSame(hierarchyBacking, hierarchy.BackingIdentity);
            Assert.Equal(1, hierarchy.DetachCount);
            Assert.Same(alternateHierarchyBacking, alternateHierarchy.BackingIdentity);
            Assert.Equal(0, alternateHierarchy.DetachCount);
            Assert.NotSame(
                relationBacking,
                candidate.RelationGraph.StateBackingIdentity<CowRelation>());
            Assert.Equal(1, candidate.RelationGraph.StateDetachCount<CowRelation>());
            Assert.Same(
                alternateRelationBacking,
                candidate.RelationGraph.StateBackingIdentity<AlternateRelation>());
            Assert.Equal(0, candidate.RelationGraph.StateDetachCount<AlternateRelation>());
            Assert.Empty(hierarchy.GetChildren(fixture.FirstParent));
            Assert.Empty(candidate.RelationGraph.Snapshot<CowRelation>(
                fixture.Source,
                RelationAdjacencyRole.Outgoing).Entries.ToArray());
        }

        AssertPublishedImage(published, fixture);
    }

    private static Fixture CreateFixture()
    {
        var world = new World();
        Entity firstParent = world.CreateEntity();
        Entity secondParent = world.CreateEntity();
        Entity child = world.CreateEntity();
        Entity alternateParent = world.CreateEntity();
        Entity alternateChild = world.CreateEntity();
        Entity source = world.CreateEntity();
        Entity firstTarget = world.CreateEntity();
        Entity secondTarget = world.CreateEntity();
        Entity alternateSource = world.CreateEntity();
        Entity alternateTarget = world.CreateEntity();

        Hierarchy<CowHierarchyDomain>.SetParent(world, child, firstParent);
        Hierarchy<AlternateHierarchyDomain>.SetParent(
            world,
            alternateChild,
            alternateParent);
        RelationEdge<CowRelation> edge = world.CreateRelation(
            source,
            firstTarget,
            new CowRelation(1));
        _ = world.CreateRelation(
            alternateSource,
            alternateTarget,
            new AlternateRelation());

        return new Fixture(
            world,
            firstParent,
            secondParent,
            child,
            alternateParent,
            alternateChild,
            source,
            firstTarget,
            secondTarget,
            edge);
    }

    private static void AssertSharedBeforeWrite(
        WorldStructureRoot candidate,
        object hierarchyBacking,
        object alternateHierarchyBacking,
        object relationBacking,
        object alternateRelationBacking,
        Fixture fixture)
    {
        var hierarchy = candidate.Hierarchy.Domain<CowHierarchyDomain>();
        var alternateHierarchy = candidate.Hierarchy.Domain<AlternateHierarchyDomain>();
        Assert.Same(hierarchyBacking, hierarchy.BackingIdentity);
        Assert.Same(alternateHierarchyBacking, alternateHierarchy.BackingIdentity);
        Assert.Equal(0, hierarchy.DetachCount);
        Assert.Equal(0, alternateHierarchy.DetachCount);
        Assert.Same(
            relationBacking,
            candidate.RelationGraph.StateBackingIdentity<CowRelation>());
        Assert.Same(
            alternateRelationBacking,
            candidate.RelationGraph.StateBackingIdentity<AlternateRelation>());
        Assert.Equal(0, candidate.RelationGraph.StateDetachCount<CowRelation>());
        Assert.Equal(0, candidate.RelationGraph.StateDetachCount<AlternateRelation>());

        // Relaxed/read-only snapshots do not detach either topology owner.
        Assert.Equal([fixture.Child], hierarchy.GetChildren(fixture.FirstParent).ToArray());
        AssertRelationTarget(
            candidate.RelationGraph.Snapshot<CowRelation>(
                fixture.Source,
                RelationAdjacencyRole.Outgoing),
            fixture.FirstTarget);
        Assert.Same(hierarchyBacking, hierarchy.BackingIdentity);
        Assert.Same(
            relationBacking,
            candidate.RelationGraph.StateBackingIdentity<CowRelation>());
    }

    private static void MutateCandidate(WorldStructureRoot candidate, Fixture fixture)
    {
        candidate.Hierarchy.Domain<CowHierarchyDomain>().SetParent(
            fixture.Child,
            fixture.SecondParent,
            insertIndex: null,
            immediate: true);
        candidate.RelationGraph.Retarget(
            fixture.World,
            fixture.Edge,
            fixture.Source,
            fixture.SecondTarget,
            RelationMaintenanceTiming.Immediate);
    }

    private static void AssertWrittenTypesDetached(
        WorldStructureRoot published,
        WorldStructureRoot candidate,
        object hierarchyBacking,
        object alternateHierarchyBacking,
        object relationBacking,
        object alternateRelationBacking)
    {
        var hierarchy = candidate.Hierarchy.Domain<CowHierarchyDomain>();
        var alternateHierarchy = candidate.Hierarchy.Domain<AlternateHierarchyDomain>();
        Assert.NotSame(hierarchyBacking, hierarchy.BackingIdentity);
        Assert.NotSame(
            published.Hierarchy.Domain<CowHierarchyDomain>().BackingIdentity,
            hierarchy.BackingIdentity);
        Assert.Equal(1, hierarchy.DetachCount);
        Assert.Same(alternateHierarchyBacking, alternateHierarchy.BackingIdentity);
        Assert.Equal(0, alternateHierarchy.DetachCount);

        Assert.NotSame(
            relationBacking,
            candidate.RelationGraph.StateBackingIdentity<CowRelation>());
        Assert.NotSame(
            published.RelationGraph.StateBackingIdentity<CowRelation>(),
            candidate.RelationGraph.StateBackingIdentity<CowRelation>());
        Assert.Equal(1, candidate.RelationGraph.StateDetachCount<CowRelation>());
        Assert.Same(
            alternateRelationBacking,
            candidate.RelationGraph.StateBackingIdentity<AlternateRelation>());
        Assert.Equal(0, candidate.RelationGraph.StateDetachCount<AlternateRelation>());
    }

    private static void AssertPublishedImage(WorldStructureRoot root, Fixture fixture)
    {
        var hierarchy = root.Hierarchy.Domain<CowHierarchyDomain>();
        Assert.Equal([fixture.Child], hierarchy.GetChildren(fixture.FirstParent).ToArray());
        Assert.Empty(hierarchy.GetChildren(fixture.SecondParent));
        AssertRelationTarget(
            root.RelationGraph.Snapshot<CowRelation>(
                fixture.Source,
                RelationAdjacencyRole.Outgoing),
            fixture.FirstTarget);
    }

    private static void AssertCandidateImage(WorldStructureRoot root, Fixture fixture)
    {
        var hierarchy = root.Hierarchy.Domain<CowHierarchyDomain>();
        Assert.Empty(hierarchy.GetChildren(fixture.FirstParent));
        Assert.Equal([fixture.Child], hierarchy.GetChildren(fixture.SecondParent).ToArray());
        AssertRelationTarget(
            root.RelationGraph.Snapshot<CowRelation>(
                fixture.Source,
                RelationAdjacencyRole.Outgoing),
            fixture.SecondTarget);
    }

    private static void AssertRelationTarget(
        RelationAdjacencySnapshot<CowRelation> snapshot,
        Entity target) =>
        Assert.Equal(target, Assert.Single(snapshot.Entries.ToArray()).OtherEndpoint);

    private readonly record struct Fixture(
        World World,
        Entity FirstParent,
        Entity SecondParent,
        Entity Child,
        Entity AlternateParent,
        Entity AlternateChild,
        Entity Source,
        Entity FirstTarget,
        Entity SecondTarget,
        RelationEdge<CowRelation> Edge);

    private readonly struct CowHierarchyDomain : IHierarchyDomain;

    private readonly struct AlternateHierarchyDomain : IHierarchyDomain;

    [RelationSchema(RelationDirection.Directed, RelationCardinality.Parallel)]
    private readonly record struct CowRelation(int Value) : IComponent;

    [RelationSchema(RelationDirection.Directed, RelationCardinality.Parallel)]
    private readonly struct AlternateRelation : IComponent;
}
