using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Hierarchy;
using SomeEngine.ECS.Relations;
using SomeEngine.ECS.Serialization;
using Xunit;

namespace SomeEngine.ECS.Serialization.Tests;

public sealed class TopologySerializationTests
{
    [Fact]
    public void TopologySchemaMismatch_FailsClosedWithoutMigrationSurface()
    {
        Guid stableId = Guid.Parse("A16935CE-BE68-47E2-92CE-D27AE6FCFCF1");
        var oldKey = new SerializationTypeKey(
            stableId,
            "test-hierarchy",
            0x11111111A5A5A5A5ul);
        var newKey = new SerializationTypeKey(
            stableId,
            "test-hierarchy",
            0x22222222A5A5A5A5ul);
        var writeRegistry = new SerializationRegistry()
            .Register<TopologyLabel>()
            .RegisterHierarchyDomain<DefaultHierarchyDomain>(oldKey);
        var source = new World();
        Entity parent = source.CreateEntity(new TopologyLabel { Value = 1 });
        Entity child = source.CreateEntity(new TopologyLabel { Value = 2 });
        Hierarchy<DefaultHierarchyDomain>.SetParent(source, child, parent);

        using var stream = new MemoryStream();
        WorldSerializer.WriteWorld(stream, source, writeRegistry);
        byte[] bytes = stream.ToArray();

        var rejectRegistry = new SerializationRegistry()
            .Register<TopologyLabel>()
            .RegisterHierarchyDomain<DefaultHierarchyDomain>(newKey);
        using var rejected = new MemoryStream(bytes, writable: false);
        InvalidDataException mismatch = Assert.Throws<InvalidDataException>(() =>
            WorldSerializer.ReadWorld(rejected, rejectRegistry));
        Assert.Contains("Topology schema mismatch", mismatch.Message);

    }

    [Fact]
    public void ReadWorld_RejectsUnknownTopologyStableId()
    {
        var writeRegistry = new SerializationRegistry()
            .Register<TopologyLabel>()
            .RegisterHierarchyDomain<DefaultHierarchyDomain>();
        var source = new World();
        Entity parent = source.CreateEntity(new TopologyLabel { Value = 1 });
        Entity child = source.CreateEntity(new TopologyLabel { Value = 2 });
        Hierarchy<DefaultHierarchyDomain>.SetParent(source, child, parent);
        using var stream = new MemoryStream();
        WorldSerializer.WriteWorld(stream, source, writeRegistry);

        var readRegistry = new SerializationRegistry()
            .Register<TopologyLabel>()
            .RegisterHierarchyDomain<UiDomain>();
        stream.Position = 0;
        InvalidDataException error = Assert.Throws<InvalidDataException>(() =>
            WorldSerializer.ReadWorld(stream, readRegistry));
        Assert.Contains("Unknown serialized Hierarchy topology", error.Message);
    }

    [Fact]
    public void ReadWorld_RejectsTopologyCountsBeforeAllocatingArrays()
    {
        SerializationRegistry registry = CreateRegistry();
        var source = new World();
        Entity parent = source.CreateEntity(new TopologyLabel { Value = 1 });
        Entity child = source.CreateEntity(new TopologyLabel { Value = 2 });
        Hierarchy<DefaultHierarchyDomain>.SetParent(source, child, parent);

        using var stream = new MemoryStream();
        WorldSerializer.WriteWorld(stream, source, registry);
        stream.Position = 0;

        var limits = SerializationReadLimits.Default with
        {
            MaxTopologyEntriesPerSection = 0,
        };
        var error = Assert.Throws<InvalidDataException>(() =>
            WorldSerializer.ReadWorld(
                stream,
                registry,
                new WorldLoadOptions(ReadLimits: limits)));
        Assert.Contains("hierarchy", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("limit", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RegisterRelationTopology_CustomKeyRequiresOrdinaryTablePayload()
    {
        var registry = new SerializationRegistry().RegisterSparse<NonTableRelationPayload>();
        var topologyKey = new SerializationTypeKey(
            Guid.Parse("1B129490-A7B3-40CC-99ED-C6229FE8F9F5"),
            "non-table-relation-topology",
            1);

        InvalidOperationException defaultError = Assert.Throws<InvalidOperationException>(
            () => registry.RegisterRelationTopology<NonTableRelationPayload>());
        InvalidOperationException customError = Assert.Throws<InvalidOperationException>(
            () => registry.RegisterRelationTopology<NonTableRelationPayload>(topologyKey));

        Assert.Contains("ordinary table component", defaultError.Message);
        Assert.Equal(defaultError.Message, customError.Message);
    }

    [Fact]
    public void CurrentWorld_RoundTripsCanonicalHierarchyRelationAndSemanticLocalOrder()
    {
        SerializationRegistry registry = CreateRegistry();
        var source = new World();

        Entity root = source.CreateEntity(new TopologyLabel { Value = 1 });
        Entity childA = source.CreateEntity(new TopologyLabel { Value = 2 });
        Entity childB = source.CreateEntity(new TopologyLabel { Value = 3 });
        Entity uiRoot = source.CreateEntity(new TopologyLabel { Value = 4 });
        Hierarchy<DefaultHierarchyDomain>.SetChildOrderPolicy(source, root, ChildOrderPolicy.Ordered);
        Hierarchy<DefaultHierarchyDomain>.SetParent(source, childB, root);
        Hierarchy<DefaultHierarchyDomain>.SetParent(source, childA, root, insertIndex: 0);
        Hierarchy<UiDomain>.SetChildOrderPolicy(source, uiRoot, ChildOrderPolicy.Ordered);
        Hierarchy<UiDomain>.SetParent(source, childA, uiRoot);

        Entity relationSource = source.CreateEntity(new TopologyLabel { Value = 10 });
        Entity relationTarget = source.CreateEntity(new TopologyLabel { Value = 11 });
        source.SetRelationAdjacencyOrder<SerializedLink>(
            relationSource,
            RelationAdjacencyRole.Outgoing,
            RelationAdjacencyOrderPolicy.Ordered);
        source.SetRelationAdjacencyOrder<SerializedLink>(
            relationTarget,
            RelationAdjacencyRole.Incoming,
            RelationAdjacencyOrderPolicy.Ordered);
        RelationEdge<SerializedLink> edgeA = source.CreateRelation(
            relationSource,
            relationTarget,
            new SerializedLink { Weight = 10 });
        RelationEdge<SerializedLink> edgeB = source.CreateRelation(
            relationSource,
            relationTarget,
            new SerializedLink { Weight = 20 });
        RelationEdge<SerializedLink> edgeC = source.CreateRelation(
            relationSource,
            relationTarget,
            new SerializedLink { Weight = 30 });
        source.ReorderRelationAdjacency(
            relationSource,
            RelationAdjacencyRole.Outgoing,
            edgeC,
            insertIndex: 0);
        source.ReorderRelationAdjacency(
            relationTarget,
            RelationAdjacencyRole.Incoming,
            edgeB,
            insertIndex: 0);

        using var first = new MemoryStream();
        using var second = new MemoryStream();
        WorldSerializer.WriteWorld(first, source, registry);
        WorldSerializer.WriteWorld(second, source, registry);

        Assert.Equal(first.ToArray(), second.ToArray());

        first.Position = 0;
        World loaded = WorldSerializer.ReadWorld(first, registry);

        Assert.Equal(root, Hierarchy<DefaultHierarchyDomain>.GetParent(loaded, childA));
        Assert.Equal(root, Hierarchy<DefaultHierarchyDomain>.GetParent(loaded, childB));
        Assert.Equal(
            new[] { childA, childB },
            Hierarchy<DefaultHierarchyDomain>.GetChildren(loaded, root).ToArray());
        Assert.Equal(ChildOrderPolicy.Ordered,
            Hierarchy<DefaultHierarchyDomain>.GetChildOrderPolicy(loaded, root));
        Assert.Equal(uiRoot, Hierarchy<UiDomain>.GetParent(loaded, childA));
        Assert.Equal(new[] { childA }, Hierarchy<UiDomain>.GetChildren(loaded, uiRoot).ToArray());

        Assert.Equal(
            new[] { edgeC, edgeA, edgeB },
            loaded.GetOrderedOutgoingRelations<SerializedLink>(relationSource)
                .Entries.ToArray().Select(static entry => entry.Edge));
        Assert.Equal(
            new[] { edgeB, edgeA, edgeC },
            loaded.GetOrderedIncomingRelations<SerializedLink>(relationTarget)
                .Entries.ToArray().Select(static entry => entry.Edge));
        Assert.Equal(10, loaded.Read<SerializedLink>(edgeA.Entity).Weight);
        Assert.Equal(20, loaded.Read<SerializedLink>(edgeB.Entity).Weight);
        Assert.Equal(30, loaded.Read<SerializedLink>(edgeC.Entity).Weight);
        Assert.Equal(
            new[] { edgeA, edgeB, edgeC }.OrderBy(static edge => edge.Entity.Index),
            loaded.GetRelationEdgesBetween<SerializedLink>(relationSource, relationTarget)
                .ToArray()
                .OrderBy(static edge => edge.Entity.Index));

        using var rewritten = new MemoryStream();
        WorldSerializer.WriteWorld(rewritten, loaded, registry);
        Assert.Equal(second.ToArray(), rewritten.ToArray());
    }

    [Fact]
    public void CurrentWorld_RoundTripsUndirectedFixedSlotsAndIncidentOrder()
    {
        SerializationRegistry registry = CreateRegistry();
        var source = new World();
        Entity endpointA = source.CreateEntity(new TopologyLabel { Value = 20 });
        Entity endpointB = source.CreateEntity(new TopologyLabel { Value = 21 });
        source.SetRelationAdjacencyOrder<SerializedBond>(
            endpointA,
            RelationAdjacencyRole.Incident,
            RelationAdjacencyOrderPolicy.Ordered);
        RelationEdge<SerializedBond> firstEdge = source.CreateRelation(
            endpointA,
            endpointB,
            new SerializedBond { AnchorA = 1, AnchorB = 2 });
        RelationEdge<SerializedBond> selfEdge = source.CreateRelation(
            endpointA,
            endpointA,
            new SerializedBond { AnchorA = 3, AnchorB = 4 });
        source.ReorderRelationAdjacency(
            endpointA,
            RelationAdjacencyRole.Incident,
            selfEdge,
            insertIndex: 0);

        using var stream = new MemoryStream();
        WorldSerializer.WriteWorld(stream, source, registry);
        stream.Position = 0;
        World loaded = WorldSerializer.ReadWorld(stream, registry);

        UndirectedRelationEndpoints<SerializedBond> endpoints =
            loaded.GetUndirectedRelationEndpoints(firstEdge);
        Assert.Equal(endpointA, endpoints.EndpointA);
        Assert.Equal(endpointB, endpoints.EndpointB);
        Assert.Equal(1, loaded.Read<SerializedBond>(firstEdge.Entity).AnchorA);
        Assert.Equal(2, loaded.Read<SerializedBond>(firstEdge.Entity).AnchorB);
        Assert.Equal(
            new[] { selfEdge, firstEdge },
            loaded.GetOrderedIncidentRelations<SerializedBond>(endpointA)
                .Entries.ToArray().Select(static entry => entry.Edge));
        Assert.Single(
            loaded.GetIncidentRelations<SerializedBond>(endpointA).Entries.ToArray(),
            entry => entry.Edge == selfEdge);
    }

    [Fact]
    public void DeferredRelationState_FailsClosedBeforeSerializationOutput()
    {
        SerializationRegistry registry = CreateRegistry();
        var source = new World();
        Entity oldSource = source.CreateEntity(new TopologyLabel { Value = 30 });
        Entity newSource = source.CreateEntity(new TopologyLabel { Value = 31 });
        Entity targetA = source.CreateEntity(new TopologyLabel { Value = 32 });
        Entity targetB = source.CreateEntity(new TopologyLabel { Value = 33 });
        source.SetRelationAdjacencyOrder<SerializedLink>(
            newSource,
            RelationAdjacencyRole.Outgoing,
            RelationAdjacencyOrderPolicy.Ordered);
        RelationEdge<SerializedLink> moved = source.CreateRelation(
            oldSource,
            targetA,
            new SerializedLink { Weight = 40 });
        RelationEdge<SerializedLink> resident = source.CreateRelation(
            newSource,
            targetB,
            new SerializedLink { Weight = 41 });

        source.RetargetRelationDeferred(
            moved,
            newSource,
            targetA,
            new DirectedRelationPlacement(OutgoingIndex: 0));
        RelationAdjacencySnapshot<SerializedLink> oldBefore =
            source.GetOutgoingRelations<SerializedLink>(oldSource);
        RelationAdjacencySnapshot<SerializedLink> newBefore =
            source.GetOutgoingRelations<SerializedLink>(newSource);

        using var stream = new MemoryStream();
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
            WorldSerializer.WriteWorld(stream, source, registry));
        Assert.Contains("dirty or in-progress state", error.Message);
        Assert.Equal(0, stream.Length);

        RelationAdjacencySnapshot<SerializedLink> oldAfter =
            source.GetOutgoingRelations<SerializedLink>(oldSource);
        RelationAdjacencySnapshot<SerializedLink> newAfter =
            source.GetOutgoingRelations<SerializedLink>(newSource);
        Assert.Equal(oldBefore.Generation, oldAfter.Generation);
        Assert.Equal(newBefore.Generation, newAfter.Generation);
        Assert.Equal(
            oldBefore.Entries.ToArray(),
            oldAfter.Entries.ToArray());
        Assert.Equal(
            newBefore.Entries.ToArray(),
            newAfter.Entries.ToArray());
        Assert.Equal(
            moved,
            Assert.Single(oldAfter.Entries.ToArray()).Edge);
        Assert.Equal(
            resident,
            Assert.Single(newAfter.Entries.ToArray()).Edge);

    }

    [Fact]
    public void DeferredHierarchyState_FailsClosedBeforeSerializationOutput()
    {
        SerializationRegistry registry = CreateRegistry();
        var source = new World();
        Entity oldParent = source.CreateEntity(new TopologyLabel { Value = 50 });
        Entity newParent = source.CreateEntity(new TopologyLabel { Value = 51 });
        Entity moved = source.CreateEntity(new TopologyLabel { Value = 52 });
        Entity resident = source.CreateEntity(new TopologyLabel { Value = 53 });
        Hierarchy<DefaultHierarchyDomain>.SetChildOrderPolicy(
            source,
            oldParent,
            ChildOrderPolicy.Ordered);
        Hierarchy<DefaultHierarchyDomain>.SetChildOrderPolicy(
            source,
            newParent,
            ChildOrderPolicy.Ordered);
        Hierarchy<DefaultHierarchyDomain>.SetParent(source, moved, oldParent);
        Hierarchy<DefaultHierarchyDomain>.SetParent(source, resident, newParent);

        Hierarchy<DefaultHierarchyDomain>.SetParentDeferred(
            source,
            moved,
            newParent,
            insertIndex: 0);
        HierarchyChildrenSnapshot<DefaultHierarchyDomain> oldBefore =
            Hierarchy<DefaultHierarchyDomain>.GetChildren(source, oldParent);
        HierarchyChildrenSnapshot<DefaultHierarchyDomain> newBefore =
            Hierarchy<DefaultHierarchyDomain>.GetChildren(source, newParent);

        using var stream = new MemoryStream();
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
            WorldSerializer.WriteWorld(stream, source, registry));
        Assert.Contains("deferred or dirty inverse state", error.Message);
        Assert.Equal(0, stream.Length);

        HierarchyChildrenSnapshot<DefaultHierarchyDomain> oldAfter =
            Hierarchy<DefaultHierarchyDomain>.GetChildren(source, oldParent);
        HierarchyChildrenSnapshot<DefaultHierarchyDomain> newAfter =
            Hierarchy<DefaultHierarchyDomain>.GetChildren(source, newParent);
        Assert.Equal(oldBefore.Generation, oldAfter.Generation);
        Assert.Equal(newBefore.Generation, newAfter.Generation);
        Assert.Equal(oldBefore.ToArray(), oldAfter.ToArray());
        Assert.Equal(newBefore.ToArray(), newAfter.ToArray());
        Assert.Equal(newParent, Hierarchy<DefaultHierarchyDomain>.GetParent(source, moved));
        Assert.Equal(new[] { moved }, oldAfter.ToArray());
        Assert.Equal(new[] { resident }, newAfter.ToArray());

    }

    private static SerializationRegistry CreateRegistry() =>
        new SerializationRegistry()
            .Register<TopologyLabel>()
            .Register<SerializedLink>()
            .Register<SerializedBond>()
            .RegisterHierarchyDomain<DefaultHierarchyDomain>()
            .RegisterHierarchyDomain<UiDomain>()
            .RegisterRelationTopology<SerializedLink>()
            .RegisterRelationTopology<SerializedBond>();

    private readonly struct UiDomain : IHierarchyDomain { }

    private struct TopologyLabel : IComponent
    {
        public int Value;
    }

    [RelationSchema(RelationDirection.Directed, RelationCardinality.Parallel)]
    private struct SerializedLink : IComponent
    {
        public int Weight;
    }

    [RelationSchema(RelationDirection.Undirected, RelationCardinality.Parallel)]
    private struct SerializedBond : IComponent
    {
        public int AnchorA;
        public int AnchorB;
    }

    private struct NonTableRelationPayload : IComponent, ISparseComponent
    {
    }

}
