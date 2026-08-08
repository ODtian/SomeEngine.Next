using System.Reflection;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Hierarchy;
using SomeEngine.ECS.Owners;
using SomeEngine.ECS.Relations;

namespace SomeEngine.ECS.Tests;

public sealed class OrderTaxDiagnosticsTests
{
    [Fact]
    public void Hierarchy_UnorderedAddRemoveReparentAndMaintenance_UseNoOrderedWorkOrMetadata()
    {
        var world = new World();
        Entity firstParent = world.CreateEntity();
        Entity secondParent = world.CreateEntity();
        Entity firstChild = world.CreateEntity();
        Entity secondChild = world.CreateEntity();

        Hierarchy<DiagnosticDomain>.SetParent(world, firstChild, firstParent);
        Hierarchy<DiagnosticDomain>.SetParentDeferred(world, secondChild, firstParent);
        AssertNoOrderedWork(HierarchyDiagnostics<DiagnosticDomain>(world));
        Hierarchy<DiagnosticDomain>.Maintain(world);

        Hierarchy<DiagnosticDomain>.SetParent(world, firstChild, secondParent);
        Hierarchy<DiagnosticDomain>.Detach(world, firstChild);
        ClearHierarchyRemovedFacts<DiagnosticDomain>(world);
        Hierarchy<DiagnosticDomain>.SetParentDeferred(world, secondChild, secondParent);
        AssertNoOrderedWork(HierarchyDiagnostics<DiagnosticDomain>(world));
        Hierarchy<DiagnosticDomain>.Maintain(world);
        ClearHierarchyRemovedFacts<DiagnosticDomain>(world);
        Hierarchy<DiagnosticDomain>.DetachDeferred(world, secondChild);
        Hierarchy<DiagnosticDomain>.Maintain(world);

        AssertNoOrderedWork(HierarchyDiagnostics<DiagnosticDomain>(world));
    }

    [Fact]
    public void Hierarchy_MixedDomain_UnorderedMutationDoesNotTouchOrderedShard_ButOrderedMutationDoes()
    {
        var world = new World();
        Entity orderedParent = world.CreateEntity();
        Entity unorderedParentA = world.CreateEntity();
        Entity unorderedParentB = world.CreateEntity();
        Entity orderedExisting = world.CreateEntity();
        Entity unorderedChild = world.CreateEntity();

        Hierarchy<DiagnosticDomain>.SetChildOrderPolicy(
            world,
            orderedParent,
            ChildOrderPolicy.Ordered);
        Hierarchy<DiagnosticDomain>.SetParent(world, orderedExisting, orderedParent);
        TopologyOrderDiagnostics baseline = HierarchyDiagnostics<DiagnosticDomain>(world);
        Assert.True(baseline.OrderedPathDispatches > 0);
        Assert.True(baseline.OrderedIndexWorkUnits > 0);

        Hierarchy<DiagnosticDomain>.SetParent(world, unorderedChild, unorderedParentA);
        Hierarchy<DiagnosticDomain>.SetParentDeferred(world, unorderedChild, unorderedParentB);
        Hierarchy<DiagnosticDomain>.Maintain(world);
        Hierarchy<DiagnosticDomain>.Detach(world, unorderedChild);

        Assert.Equal(baseline, HierarchyDiagnostics<DiagnosticDomain>(world));

        Entity orderedDeferred = world.CreateEntity();
        Hierarchy<DiagnosticDomain>.SetParentDeferred(
            world,
            orderedDeferred,
            orderedParent,
            insertIndex: 1);
        TopologyOrderDiagnostics pending = HierarchyDiagnostics<DiagnosticDomain>(world);
        Assert.Equal(1, pending.LivePlacementMetadataRecords);
        Assert.True(pending.PlacementMetadataWrites > baseline.PlacementMetadataWrites);
        Assert.True(
            pending.PlacementMetadataPayloadBytesWritten >
            baseline.PlacementMetadataPayloadBytesWritten);
        Assert.True(pending.LivePlacementMetadataPayloadBytes > 0);
        Assert.Equal(0, pending.ExplicitOrderKeyBytes);

        Hierarchy<DiagnosticDomain>.Maintain(world);
        TopologyOrderDiagnostics maintained = HierarchyDiagnostics<DiagnosticDomain>(world);
        Assert.Equal(0, maintained.LivePlacementMetadataRecords);
        Assert.Equal(0, maintained.LivePlacementMetadataPayloadBytes);
        Assert.True(maintained.OrderedPathDispatches > baseline.OrderedPathDispatches);
        Assert.True(maintained.OrderedIndexWorkUnits > baseline.OrderedIndexWorkUnits);

        AssertNoOrderedWork(HierarchyDiagnostics<IndependentDomain>(world));
    }

    [Fact]
    public void Hierarchy_MixedDomain_RawChunkScanDoesNotRepublishUnchangedOrderedParent()
    {
        var world = new World();
        Entity orderedParent = world.CreateEntity();
        Entity unorderedParentA = world.CreateEntity();
        Entity unorderedParentB = world.CreateEntity();
        Entity orderedChild = world.CreateEntity();
        Entity unorderedChild = world.CreateEntity();

        Hierarchy<DiagnosticDomain>.SetChildOrderPolicy(
            world,
            orderedParent,
            ChildOrderPolicy.Ordered);
        Hierarchy<DiagnosticDomain>.SetParent(world, orderedChild, orderedParent);
        Hierarchy<DiagnosticDomain>.SetParent(world, unorderedChild, unorderedParentA);

        HierarchyChildrenSnapshot<DiagnosticDomain> orderedBefore =
            Hierarchy<DiagnosticDomain>.GetChildren(world, orderedParent);
        TopologyOrderDiagnostics diagnosticsBefore = HierarchyDiagnostics<DiagnosticDomain>(world);
        var query = world.Query(
            world.QueryDefinition().Write<Parent<DiagnosticDomain>>());

        world.ExecuteQuery(query, cursor =>
        {
            foreach (var chunk in cursor.Chunks)
            {
                ReadOnlySpan<Entity> entities = chunk.Entities;
                Span<Parent<DiagnosticDomain>> parents = chunk.Write<Parent<DiagnosticDomain>>();
                for (int i = 0; i < entities.Length; i++)
                {
                    if (entities[i] == unorderedChild)
                        parents[i].Value = unorderedParentB;
                }
            }
        });
        Hierarchy<DiagnosticDomain>.Maintain(world);

        HierarchyChildrenSnapshot<DiagnosticDomain> orderedAfter =
            Hierarchy<DiagnosticDomain>.GetChildren(world, orderedParent);
        Assert.Equal(diagnosticsBefore, HierarchyDiagnostics<DiagnosticDomain>(world));
        Assert.Equal(orderedBefore.Generation, orderedAfter.Generation);
        Assert.Equal(new[] { orderedChild }, orderedAfter.ToArray());
        Assert.Empty(Hierarchy<DiagnosticDomain>.GetChildren(world, unorderedParentA));
        Assert.Equal(
            new[] { unorderedChild },
            Hierarchy<DiagnosticDomain>.GetChildren(world, unorderedParentB).ToArray());
    }

    [Fact]
    public void Relation_UnorderedAddRemoveRetargetAndMaintenance_UseNoOrderedWorkOrMetadata()
    {
        var world = new World();
        Entity sourceA = world.CreateEntity();
        Entity sourceB = world.CreateEntity();
        Entity targetA = world.CreateEntity();
        Entity targetB = world.CreateEntity();

        RelationEdge<DiagnosticRelation> edge = world.CreateRelation(
            sourceA,
            targetA,
            new DiagnosticRelation());
        world.RetargetRelationDeferred(edge, sourceB, targetB);
        AssertNoOrderedWork(RelationDiagnostics<DiagnosticRelation>(world));
        world.MaintainRelations<DiagnosticRelation>();
        ClearRelationMarkerRemovedFacts<DiagnosticRelation>(world);
        world.RetargetRelationImmediate(edge, sourceA, targetB);
        ClearRelationMarkerRemovedFacts<DiagnosticRelation>(world);
        world.DestroyRelation(edge);

        AssertNoOrderedWork(RelationDiagnostics<DiagnosticRelation>(world));
    }

    [Fact]
    public void Relation_MixedPayload_UnorderedMutationDoesNotTouchOrderedShard_ButOrderedMutationDoes()
    {
        var world = new World();
        Entity orderedSource = world.CreateEntity();
        Entity orderedTarget = world.CreateEntity();
        world.SetRelationAdjacencyOrder<DiagnosticRelation>(
            orderedSource,
            RelationAdjacencyRole.Outgoing,
            RelationAdjacencyOrderPolicy.Ordered);
        RelationEdge<DiagnosticRelation> orderedEdge = world.CreateRelation(
            orderedSource,
            orderedTarget,
            new DiagnosticRelation());
        TopologyOrderDiagnostics baseline = RelationDiagnostics<DiagnosticRelation>(world);
        Assert.True(baseline.OrderedPathDispatches > 0);
        Assert.True(baseline.OrderedIndexWorkUnits > 0);

        Entity sourceA = world.CreateEntity();
        Entity sourceB = world.CreateEntity();
        Entity targetA = world.CreateEntity();
        Entity targetB = world.CreateEntity();
        RelationEdge<DiagnosticRelation> unorderedEdge = world.CreateRelation(
            sourceA,
            targetA,
            new DiagnosticRelation());
        world.RetargetRelationDeferred(unorderedEdge, sourceB, targetB);
        world.MaintainRelations<DiagnosticRelation>();
        world.DestroyRelation(unorderedEdge);

        Assert.Equal(baseline, RelationDiagnostics<DiagnosticRelation>(world));

        Entity replacementTarget = world.CreateEntity();
        world.RetargetRelationDeferred(orderedEdge, orderedSource, replacementTarget);
        TopologyOrderDiagnostics pending = RelationDiagnostics<DiagnosticRelation>(world);
        Assert.Equal(1, pending.LivePlacementMetadataRecords);
        Assert.True(pending.PlacementMetadataWrites > baseline.PlacementMetadataWrites);
        Assert.True(pending.LivePlacementMetadataPayloadBytes > 0);
        Assert.True(pending.OrderedPathDispatches > baseline.OrderedPathDispatches);
        Assert.True(pending.OrderedIndexWorkUnits > baseline.OrderedIndexWorkUnits);
        Assert.Equal(0, pending.ExplicitOrderKeyBytes);

        world.MaintainRelations<DiagnosticRelation>();
        TopologyOrderDiagnostics maintained = RelationDiagnostics<DiagnosticRelation>(world);
        Assert.Equal(0, maintained.LivePlacementMetadataRecords);
        Assert.Equal(0, maintained.LivePlacementMetadataPayloadBytes);
        Assert.True(maintained.OrderedPathDispatches > pending.OrderedPathDispatches);
        Assert.True(maintained.OrderedIndexWorkUnits > pending.OrderedIndexWorkUnits);

        AssertNoOrderedWork(RelationDiagnostics<IndependentRelation>(world));
    }

    [Theory]
    [InlineData(64, 7)]
    [InlineData(257, 31)]
    public void Hierarchy_UnorderedFlatWideAndSparseDirtyWorkloads_KeepOrderedMetricsZero(
        int childCount,
        int dirtyStride)
    {
        var world = new World();
        Entity wideParent = world.CreateEntity();
        var children = new Entity[childCount];
        for (int i = 0; i < children.Length; i++)
        {
            children[i] = world.CreateEntity();
            Hierarchy<WideDiagnosticDomain>.SetParent(world, children[i], wideParent);
        }
        AssertNoOrderedWork(HierarchyDiagnostics<WideDiagnosticDomain>(world));

        var sparseDestinations = new List<Entity>();
        for (int i = 0; i < children.Length; i += dirtyStride)
        {
            Entity destination = world.CreateEntity();
            sparseDestinations.Add(destination);
            Hierarchy<WideDiagnosticDomain>.SetParentDeferred(
                world,
                children[i],
                destination);
        }

        // Dirty membership is necessary for maintenance, but pure unordered children must not
        // gain a placement/sequence record even when only a sparse subset of a wide shard moves.
        AssertNoOrderedWork(HierarchyDiagnostics<WideDiagnosticDomain>(world));
        Hierarchy<WideDiagnosticDomain>.Maintain(world);
        AssertNoOrderedWork(HierarchyDiagnostics<WideDiagnosticDomain>(world));

        int destinationIndex = 0;
        for (int i = 0; i < children.Length; i += dirtyStride)
        {
            Assert.Equal(
                new[] { children[i] },
                Hierarchy<WideDiagnosticDomain>
                    .GetChildren(world, sparseDestinations[destinationIndex++])
                    .ToArray());
        }
    }

    [Theory]
    [InlineData(48)]
    [InlineData(129)]
    public void Hierarchy_UnorderedDeepDetachAndReparentWorkload_KeepsOrderedMetricsZero(
        int depth)
    {
        var world = new World();
        var chain = new Entity[depth];
        for (int i = 0; i < chain.Length; i++)
        {
            chain[i] = world.CreateEntity();
            if (i != 0)
                Hierarchy<DeepDiagnosticDomain>.SetParent(world, chain[i], chain[i - 1]);
        }
        AssertNoOrderedWork(HierarchyDiagnostics<DeepDiagnosticDomain>(world));

        int detachIndex = depth / 3;
        Hierarchy<DeepDiagnosticDomain>.DetachDeferred(world, chain[detachIndex]);
        Hierarchy<DeepDiagnosticDomain>.Maintain(world);
        Assert.Equal(Entity.Null, Hierarchy<DeepDiagnosticDomain>.GetParent(world, chain[detachIndex]));

        int reparentIndex = (depth * 2) / 3;
        Entity newRoot = world.CreateEntity();
        Hierarchy<DeepDiagnosticDomain>.SetParentDeferred(
            world,
            chain[reparentIndex],
            newRoot);
        AssertNoOrderedWork(HierarchyDiagnostics<DeepDiagnosticDomain>(world));
        Hierarchy<DeepDiagnosticDomain>.Maintain(world);

        Assert.Equal(
            newRoot,
            Hierarchy<DeepDiagnosticDomain>.GetParent(world, chain[reparentIndex]));
        AssertNoOrderedWork(HierarchyDiagnostics<DeepDiagnosticDomain>(world));
    }

    [Theory]
    [InlineData(64, 7)]
    [InlineData(257, 31)]
    public void Relation_UnorderedWideEndpointAndSparseDirtyWorkload_KeepsOrderedMetricsZero(
        int edgeCount,
        int dirtyStride)
    {
        var world = new World();
        Entity source = world.CreateEntity();
        var edges = new RelationEdge<WideDiagnosticRelation>[edgeCount];
        for (int i = 0; i < edges.Length; i++)
        {
            Entity target = world.CreateEntity();
            edges[i] = world.CreateRelation(source, target, new WideDiagnosticRelation());
        }
        Assert.Equal(edgeCount, world.GetOutgoingRelations<WideDiagnosticRelation>(source).Count);
        AssertNoOrderedWork(RelationDiagnostics<WideDiagnosticRelation>(world));

        int dirtyCount = 0;
        for (int i = 0; i < edges.Length; i += dirtyStride)
        {
            Entity replacementTarget = world.CreateEntity();
            world.RetargetRelationDeferred(edges[i], source, replacementTarget);
            dirtyCount++;
        }

        AssertNoOrderedWork(RelationDiagnostics<WideDiagnosticRelation>(world));
        world.MaintainRelations<WideDiagnosticRelation>();
        Assert.Equal(edgeCount, world.GetOutgoingRelations<WideDiagnosticRelation>(source).Count);
        AssertNoOrderedWork(RelationDiagnostics<WideDiagnosticRelation>(world));

        // Exercise swap-remove on a sparse subset while the source remains a wide endpoint.
        for (int i = 0; i < edges.Length; i += dirtyStride)
            world.DestroyRelation(edges[i]);
        Assert.Equal(
            edgeCount - dirtyCount,
            world.GetOutgoingRelations<WideDiagnosticRelation>(source).Count);
        AssertNoOrderedWork(RelationDiagnostics<WideDiagnosticRelation>(world));
    }

    [Fact]
    public void UnorderedShardRepresentations_HaveNoPlacementSequenceOrOrderKeyFields()
    {
        Type hierarchyStore = typeof(HierarchyDomainStore<>).MakeGenericType(typeof(DiagnosticDomain));
        Type? hierarchyUnordered = hierarchyStore.GetNestedType(
            "UnorderedChildShard",
            BindingFlags.NonPublic);
        Assert.NotNull(hierarchyUnordered);
        Assert.DoesNotContain(
            InstanceFields(hierarchyUnordered),
            static field => IsSemanticOrderMetadata(field));

        Type relationUnordered = typeof(UnorderedRelationAdjacencyShard<DiagnosticRelation>);
        Type relationOrdered = typeof(OrderedRelationAdjacencyShard<DiagnosticRelation>);
        Assert.NotEqual(relationOrdered, relationUnordered);
        Assert.Empty(relationUnordered.GetFields(
            BindingFlags.Instance |
            BindingFlags.Public |
            BindingFlags.NonPublic |
            BindingFlags.DeclaredOnly));
        Assert.DoesNotContain(
            InstanceFields(relationUnordered),
            static field => IsSemanticOrderMetadata(field));
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

    private static TopologyOrderDiagnostics HierarchyDiagnostics<TDomain>(World world)
        where TDomain : IHierarchyDomain =>
        world.Hierarchy.Domain<TDomain>().OrderDiagnostics;

    private static TopologyOrderDiagnostics RelationDiagnostics<T>(World world)
        where T : struct, IComponent =>
        world.RelationGraph.OrderDiagnostics<T>();

    private static void AssertNoOrderedWork(TopologyOrderDiagnostics diagnostics)
    {
        Assert.Equal(0, diagnostics.OrderedPathDispatches);
        Assert.Equal(0, diagnostics.OrderedIndexWorkUnits);
        Assert.Equal(0, diagnostics.PlacementMetadataWrites);
        Assert.Equal(0, diagnostics.PlacementMetadataPayloadBytesWritten);
        Assert.Equal(0, diagnostics.LivePlacementMetadataRecords);
        Assert.Equal(0, diagnostics.LivePlacementMetadataPayloadBytes);
        Assert.Equal(0, diagnostics.ExplicitOrderKeyBytes);
    }

    private static void ClearHierarchyRemovedFacts<TDomain>(World world)
        where TDomain : IHierarchyDomain
    {
        world.ClearRemoved<Parent<TDomain>>(world.CurrentTick);
        world.ClearRemoved<Children<TDomain>>(world.CurrentTick);
    }

    private static void ClearRelationMarkerRemovedFacts<T>(World world)
        where T : struct, IComponent
    {
        world.ClearRemoved<Outgoing<T>>(world.CurrentTick);
        world.ClearRemoved<Incoming<T>>(world.CurrentTick);
    }

    private static FieldInfo[] InstanceFields(Type type)
    {
        var result = new List<FieldInfo>();
        for (Type? current = type; current is not null; current = current.BaseType)
        {
            result.AddRange(current.GetFields(
                BindingFlags.Instance |
                BindingFlags.Public |
                BindingFlags.NonPublic |
                BindingFlags.DeclaredOnly));
        }
        return result.ToArray();
    }

    private static bool IsSemanticOrderMetadata(FieldInfo field)
    {
        string identity = $"{field.Name}|{field.FieldType.FullName}";
        return identity.Contains("Placement", StringComparison.OrdinalIgnoreCase) ||
               identity.Contains("Sequence", StringComparison.OrdinalIgnoreCase) ||
               identity.Contains("OrderKey", StringComparison.OrdinalIgnoreCase) ||
               identity.Contains("InsertIndex", StringComparison.OrdinalIgnoreCase) ||
               identity.Contains("Rank", StringComparison.OrdinalIgnoreCase);
    }

    private readonly struct DiagnosticDomain : IHierarchyDomain;

    private readonly struct IndependentDomain : IHierarchyDomain;

    private readonly struct WideDiagnosticDomain : IHierarchyDomain;

    private readonly struct DeepDiagnosticDomain : IHierarchyDomain;

    [RelationSchema(RelationDirection.Directed, RelationCardinality.Parallel)]
    private readonly struct DiagnosticRelation : IComponent;

    [RelationSchema(RelationDirection.Directed, RelationCardinality.Parallel)]
    private readonly struct IndependentRelation : IComponent;

    [RelationSchema(RelationDirection.Directed, RelationCardinality.Parallel)]
    private readonly struct WideDiagnosticRelation : IComponent;
}
