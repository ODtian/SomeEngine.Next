using SomeEngine.Serialization;
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
            .RegisterHierarchy<DefaultHierarchyDomain>(oldKey);
        var source = new World();
        Entity parent = source.Create(new TopologyLabel { Value = 1 });
        Entity child = source.Create(new TopologyLabel { Value = 2 });
        Hierarchy<DefaultHierarchyDomain>.SetParent(source, child, parent);

        using var stream = new MemoryStream();
        new WorldSerializer(writeRegistry).Save(source, stream);
        byte[] bytes = stream.ToArray();

        var rejectRegistry = new SerializationRegistry()
            .Register<TopologyLabel>()
            .RegisterHierarchy<DefaultHierarchyDomain>(newKey);
        using var rejected = new MemoryStream(bytes, writable: false);
        InvalidDataException mismatch = Assert.Throws<InvalidDataException>(() =>
            WorldSerializer.ReadWorld(rejected, rejectRegistry));
        Assert.Equal(SerializationError.SchemaMismatch, SerializationErrors.GetError(mismatch));
    }

    [Fact]
    public void ReadWorld_RejectsUnknownTopologyStableId()
    {
        var writeRegistry = new SerializationRegistry()
            .Register<TopologyLabel>()
            .RegisterHierarchy<DefaultHierarchyDomain>(SaveTypes.Key<DefaultHierarchyDomain>());
        var source = new World();
        Entity parent = source.Create(new TopologyLabel { Value = 1 });
        Entity child = source.Create(new TopologyLabel { Value = 2 });
        Hierarchy<DefaultHierarchyDomain>.SetParent(source, child, parent);
        using var stream = new MemoryStream();
        new WorldSerializer(writeRegistry).Save(source, stream);

        var readRegistry = new SerializationRegistry()
            .Register<TopologyLabel>()
            .RegisterHierarchy<UiDomain>(SaveTypes.Key<UiDomain>());
        stream.Position = 0;
        InvalidDataException error = Assert.Throws<InvalidDataException>(() =>
            WorldSerializer.ReadWorld(stream, readRegistry));
        Assert.Equal(SerializationError.UnknownType, SerializationErrors.GetError(error));
    }

    [Fact]
    public void ReadWorld_RejectsTopologyCountsBeforeAllocatingArrays()
    {
        SerializationRegistry registry = CreateRegistry();
        var source = new World();
        Entity parent = source.Create(new TopologyLabel { Value = 1 });
        Entity child = source.Create(new TopologyLabel { Value = 2 });
        Hierarchy<DefaultHierarchyDomain>.SetParent(source, child, parent);

        using var stream = new MemoryStream();
        new WorldSerializer(registry).Save(source, stream);
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
        Assert.Equal(SerializationError.LimitExceeded, SerializationErrors.GetError(error));
    }

    [Fact]
    public void RegisterRelationTopology_CustomKeyRequiresOrdinaryTablePayload()
    {
        var registry = new SerializationRegistry().RegisterSparse<NonTableRelationPayload>(SaveTypes.Key<NonTableRelationPayload>());
        var topologyKey = new SerializationTypeKey(
            Guid.Parse("1B129490-A7B3-40CC-99ED-C6229FE8F9F5"),
            "non-table-relation-topology",
            1);

        InvalidOperationException defaultError = Assert.Throws<InvalidOperationException>(
            () => registry.RegisterRelation<NonTableRelationPayload>());
        InvalidOperationException customError = Assert.Throws<InvalidOperationException>(
            () => registry.RegisterRelation<NonTableRelationPayload>(topologyKey));

        Assert.Equal(defaultError.GetType(), customError.GetType());
    }

    [Fact]
    public void CurrentWorld_RoundTripsCanonicalHierarchyRelationAndSemanticLocalOrder()
    {
        SerializationRegistry registry = CreateRegistry();
        var source = new World();

        Entity root = source.Create(new TopologyLabel { Value = 1 });
        Entity childA = source.Create(new TopologyLabel { Value = 2 });
        Entity childB = source.Create(new TopologyLabel { Value = 3 });
        Entity uiRoot = source.Create(new TopologyLabel { Value = 4 });
        Hierarchy<DefaultHierarchyDomain>.SetOrdered(source, root, true);
        Hierarchy<DefaultHierarchyDomain>.SetParent(source, childB, root);
        Hierarchy<DefaultHierarchyDomain>.SetParent(source, childA, root, insertAt: 0);
        Hierarchy<UiDomain>.SetOrdered(source, uiRoot, true);
        Hierarchy<UiDomain>.SetParent(source, childA, uiRoot);

        Entity relationSource = source.Create(new TopologyLabel { Value = 10 });
        Entity relationTarget = source.Create(new TopologyLabel { Value = 11 });
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
        new WorldSerializer(registry).Save(source, first, progress: CheckpointProgress.Preserve);
        new WorldSerializer(registry).Save(source, second, progress: CheckpointProgress.Preserve);

        Assert.Equal(first.ToArray(), second.ToArray());

        first.Position = 0;
        World loaded = WorldSerializer.ReadWorld(first, registry);

        Assert.Equal(root, Hierarchy<DefaultHierarchyDomain>.GetParent(loaded, childA));
        Assert.Equal(root, Hierarchy<DefaultHierarchyDomain>.GetParent(loaded, childB));
        Assert.Equal(
            new[] { childA, childB },
            TopologyValues.Children<DefaultHierarchyDomain>(loaded, root));
        Assert.True(
            Hierarchy<DefaultHierarchyDomain>.IsOrdered(loaded, root));
        Assert.Equal(uiRoot, Hierarchy<UiDomain>.GetParent(loaded, childA));
        Assert.Equal(new[] { childA }, TopologyValues.Children<UiDomain>(loaded, uiRoot));

        Assert.Equal(
            new[] { edgeC, edgeA, edgeB },
            TopologyValues.Outgoing<SerializedLink>(loaded, relationSource));
        Assert.Equal(
            new[] { edgeB, edgeA, edgeC },
            TopologyValues.Incoming<SerializedLink>(loaded, relationTarget));
        Assert.Equal(10, loaded.Read<SerializedLink>(edgeA.Entity).Weight);
        Assert.Equal(20, loaded.Read<SerializedLink>(edgeB.Entity).Weight);
        Assert.Equal(30, loaded.Read<SerializedLink>(edgeC.Entity).Weight);
        Assert.Equal(
            new[] { edgeA, edgeB, edgeC }.OrderBy(static edge => edge.Entity.Id),
            TopologyValues.Between<SerializedLink>(loaded, relationSource, relationTarget)
                .ToArray()
                .OrderBy(static edge => edge.Entity.Id));

        using var rewritten = new MemoryStream();
        new WorldSerializer(registry).Save(loaded, rewritten, progress: CheckpointProgress.Preserve);
        Assert.Equal(second.ToArray(), rewritten.ToArray());
    }

    [Fact]
    public void CurrentWorld_RoundTripsUndirectedFixedSlotsAndIncidentOrder()
    {
        SerializationRegistry registry = CreateRegistry();
        var source = new World();
        Entity endpointA = source.Create(new TopologyLabel { Value = 20 });
        Entity endpointB = source.Create(new TopologyLabel { Value = 21 });
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
        new WorldSerializer(registry).Save(source, stream);
        stream.Position = 0;
        World loaded = WorldSerializer.ReadWorld(stream, registry);

        RelationEndpoints<SerializedBond> endpoints =
            loaded.Read<RelationEndpoints<SerializedBond>>(firstEdge.Entity);
        Assert.Equal(endpointA, endpoints.Source);
        Assert.Equal(endpointB, endpoints.Target);
        Assert.Equal(1, loaded.Read<SerializedBond>(firstEdge.Entity).AnchorA);
        Assert.Equal(2, loaded.Read<SerializedBond>(firstEdge.Entity).AnchorB);
        Assert.Equal(
            new[] { selfEdge, firstEdge },
            TopologyValues.Incident<SerializedBond>(loaded, endpointA));
        Assert.Single(TopologyValues.Incident<SerializedBond>(loaded, endpointA), edge => edge == selfEdge);
    }

    [Fact]
    public void DeferredRelationState_PreserveRestoresValuesAndAppliedOrder()
    {
        SerializationRegistry registry = CreateRegistry();
        using var source = new World();
        Entity oldSource = source.Create(new TopologyLabel { Value = 30 });
        Entity newSource = source.Create(new TopologyLabel { Value = 31 });
        Entity targetA = source.Create(new TopologyLabel { Value = 32 });
        Entity targetB = source.Create(new TopologyLabel { Value = 33 });
        Relations<SerializedLink>.SetOrdered(source, newSource, RelationAdjacencyRole.Outgoing, true);
        RelationEdge<SerializedLink> moved = source.CreateRelation(oldSource, targetA, new SerializedLink { Weight = 40 });
        RelationEdge<SerializedLink> resident = source.CreateRelation(newSource, targetB, new SerializedLink { Weight = 41 });
        source.RetargetRelationDeferred(moved, newSource, targetA);
        RelationEdge<SerializedLink>[] oldBefore = TopologyValues.Outgoing<SerializedLink>(source, oldSource);
        RelationEdge<SerializedLink>[] newBefore = TopologyValues.Outgoing<SerializedLink>(source, newSource);

        using var stream = new MemoryStream();
        new WorldSerializer(registry).Save(source, stream, progress: CheckpointProgress.Preserve);
        Assert.Equal(oldBefore, TopologyValues.Outgoing<SerializedLink>(source, oldSource));
        Assert.Equal(newBefore, TopologyValues.Outgoing<SerializedLink>(source, newSource));
        stream.Position = 0;
        using World loaded = WorldSerializer.ReadWorld(stream, registry);
        Assert.Equal(newSource, loaded.Read<RelationEndpoints<SerializedLink>>(moved.Entity).Source);
        Assert.Equal(new[] { moved }, TopologyValues.Outgoing<SerializedLink>(loaded, oldSource));
        Assert.Equal(new[] { resident }, TopologyValues.Outgoing<SerializedLink>(loaded, newSource));
        loaded.MaintainRelations<SerializedLink>();
        loaded.ReorderRelationAdjacency(newSource, RelationAdjacencyRole.Outgoing, moved, insertIndex: 0);
        Assert.Empty(TopologyValues.Outgoing<SerializedLink>(loaded, oldSource));
        Assert.Equal(new[] { moved, resident }, TopologyValues.Outgoing<SerializedLink>(loaded, newSource));
        Assert.Equal(40, loaded.Read<SerializedLink>(moved.Entity).Weight);
        Assert.Equal(41, loaded.Read<SerializedLink>(resident.Entity).Weight);
    }

    [Fact]
    public void DeferredHierarchyState_PreserveRestoresValuesAndAppliedOrder()
    {
        SerializationRegistry registry = CreateRegistry();
        using var source = new World();
        Entity oldParent = source.Create(new TopologyLabel { Value = 50 });
        Entity newParent = source.Create(new TopologyLabel { Value = 51 });
        Entity moved = source.Create(new TopologyLabel { Value = 52 });
        Entity resident = source.Create(new TopologyLabel { Value = 53 });
        Hierarchy<DefaultHierarchyDomain>.SetOrdered(source, oldParent, true);
        Hierarchy<DefaultHierarchyDomain>.SetOrdered(source, newParent, true);
        Hierarchy<DefaultHierarchyDomain>.SetParent(source, moved, oldParent);
        Hierarchy<DefaultHierarchyDomain>.SetParent(source, resident, newParent);
        Hierarchy<DefaultHierarchyDomain>.SetParentDeferred(source, moved, newParent, insertAt: 0);
        Entity[] oldBefore = TopologyValues.Children<DefaultHierarchyDomain>(source, oldParent);
        Entity[] newBefore = TopologyValues.Children<DefaultHierarchyDomain>(source, newParent);

        using var stream = new MemoryStream();
        new WorldSerializer(registry).Save(source, stream, progress: CheckpointProgress.Preserve);
        Assert.Equal(oldBefore, TopologyValues.Children<DefaultHierarchyDomain>(source, oldParent));
        Assert.Equal(newBefore, TopologyValues.Children<DefaultHierarchyDomain>(source, newParent));
        stream.Position = 0;
        using World loaded = WorldSerializer.ReadWorld(stream, registry);
        Assert.Equal(newParent, Hierarchy<DefaultHierarchyDomain>.GetParent(loaded, moved));
        Assert.Equal(new[] { moved }, TopologyValues.Children<DefaultHierarchyDomain>(loaded, oldParent));
        Assert.Equal(new[] { resident }, TopologyValues.Children<DefaultHierarchyDomain>(loaded, newParent));
        Hierarchy<DefaultHierarchyDomain>.Maintain(loaded);
        Assert.Empty(TopologyValues.Children<DefaultHierarchyDomain>(loaded, oldParent));
        Assert.Equal(new[] { moved, resident }, TopologyValues.Children<DefaultHierarchyDomain>(loaded, newParent));
    }

    private static SerializationRegistry CreateRegistry() =>
        new SerializationRegistry()
            .Register<TopologyLabel>()
            .Register<SerializedLink>()
            .Register<SerializedBond>()
            .RegisterHierarchy<DefaultHierarchyDomain>(SaveTypes.Key<DefaultHierarchyDomain>())
            .RegisterHierarchy<UiDomain>(SaveTypes.Key<UiDomain>())
            .RegisterRelation<SerializedLink>()
            .RegisterRelation<SerializedBond>();

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
