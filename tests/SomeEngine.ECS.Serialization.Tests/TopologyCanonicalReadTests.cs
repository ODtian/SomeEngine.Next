using System.Runtime.InteropServices;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Hierarchy;
using SomeEngine.ECS.Relations;
using SomeEngine.ECS.Serialization;
using Xunit;

namespace SomeEngine.ECS.Serialization.Tests;

public sealed class TopologyCanonicalReadTests
{
    private static readonly SerializationTypeKey s_hierarchyKeyA = new(
        Guid.Parse("10000000-0000-0000-0000-000000000001"),
        "topology-a",
        0x1111111111111111ul);

    private static readonly SerializationTypeKey s_hierarchyKeyB = new(
        Guid.Parse("20000000-0000-0000-0000-000000000002"),
        "topology-b",
        0x2222222222222222ul);

    private static readonly SerializationTypeKey s_relationPayloadKey = new(
        Guid.Parse("30000000-0000-0000-0000-000000000003"),
        "canonical-relation-payload",
        0x3333333333333333ul);

    private static readonly SerializationTypeKey s_relationTopologyKey = new(
        Guid.Parse("40000000-0000-0000-0000-000000000004"),
        "canonical-relation-topology",
        0x4444444444444444ul);

    [Fact]
    public void TopologyRead_RejectsRuntimeCountDifferentFromRegistry()
    {
        var registry = new SerializationRegistry()
            .RegisterHierarchyDomain<HierarchyDomainA>(s_hierarchyKeyA);
        using var world = new World();
        byte[] bytes = Encode(writer => writer.Write(0));

        InvalidDataException error = Assert.Throws<InvalidDataException>(() =>
            ReadTopologySection(bytes, registry, world));

        Assert.Contains("count", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("exactly match", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TopologyRead_RejectsStableNameAliasBeforePayloadCodec()
    {
        var registry = new SerializationRegistry()
            .RegisterHierarchyDomain<HierarchyDomainA>(s_hierarchyKeyA);
        var alias = s_hierarchyKeyA with { StableName = "topology-z" };
        using var world = new World();
        byte[] bytes = Encode(writer =>
        {
            writer.Write(1);
            writer.Write((byte)TopologySerializationKind.Hierarchy);
            PayloadFormat.WriteTypeKey(writer, alias);
        });

        InvalidDataException error = Assert.Throws<InvalidDataException>(() =>
            ReadTopologySection(bytes, registry, world));

        Assert.Contains("does not exactly match", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TopologyRead_RejectsRegisteredSectionsOutsideCanonicalOrdinal()
    {
        var registry = new SerializationRegistry()
            .RegisterHierarchyDomain<HierarchyDomainB>(s_hierarchyKeyB)
            .RegisterHierarchyDomain<HierarchyDomainA>(s_hierarchyKeyA);
        using var world = new World();
        byte[] bytes = Encode(writer =>
        {
            writer.Write(2);
            writer.Write((byte)TopologySerializationKind.Hierarchy);
            PayloadFormat.WriteTypeKey(writer, s_hierarchyKeyB);
        });

        InvalidDataException error = Assert.Throws<InvalidDataException>(() =>
            ReadTopologySection(bytes, registry, world));

        Assert.Contains("canonical registry order", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void HierarchyRead_RejectsParentRecordsOutsideCanonicalEntityOrder()
    {
        using var world = new World();
        Entity parentA = world.CreateEntity();
        Entity childA = world.CreateEntity();
        Entity parentB = world.CreateEntity();
        Entity childB = world.CreateEntity();
        var runtime = new HierarchyTopologySerializationRuntime<HierarchyDomainA>(s_hierarchyKeyA);
        byte[] bytes = Encode(writer =>
        {
            var data = new DataWriter(writer);
            writer.Write(2);
            data.WriteEntity(childB);
            data.WriteEntity(parentB);
            data.WriteEntity(childA);
            data.WriteEntity(parentA);
            writer.Write(0);
        });

        InvalidDataException error = Assert.Throws<InvalidDataException>(() =>
            ReadRuntime(bytes, runtime, world));

        Assert.Contains("hierarchy Parent child", error.Message, StringComparison.Ordinal);
        Assert.Contains("canonical entity order", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void HierarchyRead_RejectsOrderedSequencesOutsideCanonicalEntityOrder()
    {
        using var world = new World();
        Entity parentA = world.CreateEntity();
        Entity childA = world.CreateEntity();
        Entity parentB = world.CreateEntity();
        Entity childB = world.CreateEntity();
        var runtime = new HierarchyTopologySerializationRuntime<HierarchyDomainA>(s_hierarchyKeyA);
        byte[] bytes = Encode(writer =>
        {
            var data = new DataWriter(writer);
            writer.Write(2);
            data.WriteEntity(childA);
            data.WriteEntity(parentA);
            data.WriteEntity(childB);
            data.WriteEntity(parentB);
            writer.Write(2);
            data.WriteEntity(parentB);
            writer.Write(1);
            data.WriteEntity(childB);
            data.WriteEntity(parentA);
            writer.Write(1);
            data.WriteEntity(childA);
        });

        InvalidDataException error = Assert.Throws<InvalidDataException>(() =>
            ReadRuntime(bytes, runtime, world));

        Assert.Contains("ordered hierarchy sequence parent", error.Message, StringComparison.Ordinal);
        Assert.Contains("canonical entity order", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DeepHierarchyImport_SealsWithOneVisitPerParentAndReleasesMetadata()
    {
        const int parentCount = 4_096;
        using var world = new World();
        var entities = new Entity[parentCount + 1];
        for (int i = 0; i < entities.Length; i++)
            entities[i] = world.CreateEntity();

        var import =
            world.BeginHierarchyTopologyImport<HierarchyDomainA>(parentCount);
        for (int i = 1; i < entities.Length; i++)
            import.AddParent(entities[i], entities[i - 1]);

        import.SealParents();

        Assert.Equal(1, import.CycleValidationPasses);
        Assert.Equal(parentCount, import.CycleValidationEntityVisits);
        Assert.Equal(0, import.RetainedCycleMetadataCount);
        import.SetOrderedSequenceCount(0);
        long fullScansBeforeComplete = import.CanonicalParentFullScanCount;
        import.Complete();
        Assert.Equal(fullScansBeforeComplete, import.CanonicalParentFullScanCount);
        Assert.Equal(0, import.RetainedAllocationMetadataCount);
        Assert.Equal(
            entities[^2],
            Hierarchy<HierarchyDomainA>.GetParent(world, entities[^1]));
    }

    [Fact]
    public void HierarchyImportCycleSeal_ReportsTheWireEdgeThatClosedTheCycle()
    {
        using var world = new World();
        Entity first = world.CreateEntity();
        Entity second = world.CreateEntity();
        Entity third = world.CreateEntity();
        var import =
            world.BeginHierarchyTopologyImport<HierarchyDomainA>(3);
        import.AddParent(first, second);
        import.AddParent(second, third);
        import.AddParent(third, first);

        InvalidDataException error = Assert.Throws<InvalidDataException>(import.SealParents);

        Assert.Contains($"{third} -> {first}", error.Message, StringComparison.Ordinal);
        InvalidOperationException inner = Assert.IsType<InvalidOperationException>(error.InnerException);
        Assert.Equal("Parent would create a hierarchy cycle.", inner.Message);
        Assert.InRange(import.CycleValidationEntityVisits, 1, 3);
    }

    [Fact]
    public void HierarchyImportCycleSeal_ReportsEarliestClosingEdgeAcrossCycles()
    {
        using var world = new World();
        Entity lateFirst = world.CreateEntity();
        Entity lateSecond = world.CreateEntity();
        Entity earlyFirst = world.CreateEntity();
        Entity earlySecond = world.CreateEntity();
        var import =
            world.BeginHierarchyTopologyImport<HierarchyDomainA>(4);

        import.AddParent(lateFirst, lateSecond);     // ordinal 0
        import.AddParent(earlyFirst, earlySecond);  // ordinal 1
        import.AddParent(earlySecond, earlyFirst);  // ordinal 2: first closed cycle
        import.AddParent(lateSecond, lateFirst);    // ordinal 3

        InvalidDataException error = Assert.Throws<InvalidDataException>(import.SealParents);

        Assert.Contains($"{earlySecond} -> {earlyFirst}", error.Message, StringComparison.Ordinal);
        InvalidOperationException inner = Assert.IsType<InvalidOperationException>(error.InnerException);
        Assert.Equal("Parent would create a hierarchy cycle.", inner.Message);
        Assert.InRange(import.CycleValidationEntityVisits, 1, 4);
    }

    [Fact]
    public void HierarchyRuntime_SealsCyclicParentsBeforeReadingOrderedSection()
    {
        using var world = new World();
        Entity first = world.CreateEntity();
        Entity second = world.CreateEntity();
        Entity third = world.CreateEntity();
        var runtime = new HierarchyTopologySerializationRuntime<HierarchyDomainA>(s_hierarchyKeyA);
        byte[] bytes = Encode(writer =>
        {
            var data = new DataWriter(writer);
            writer.Write(3);
            data.WriteEntity(first);
            data.WriteEntity(second);
            data.WriteEntity(second);
            data.WriteEntity(third);
            data.WriteEntity(third);
            data.WriteEntity(first);
        });

        InvalidDataException error = Assert.Throws<InvalidDataException>(() =>
            ReadRuntime(bytes, runtime, world));

        Assert.Contains("hierarchy cycle", error.InnerException?.Message ?? error.Message,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void HierarchyImportCannotPublishBeforeParentSeal()
    {
        using var world = new World();
        var import =
            world.BeginHierarchyTopologyImport<HierarchyDomainA>(0);

        Assert.Throws<InvalidOperationException>(() => import.SetOrderedSequenceCount(0));
        Assert.Throws<InvalidOperationException>(import.Complete);

        import.SealParents();
        Assert.Throws<InvalidOperationException>(() =>
            import.AddParent(world.CreateEntity(), world.CreateEntity()));
        import.SetOrderedSequenceCount(0);
        import.Complete();
    }

    [Fact]
    public void RelationRead_RejectsEdgeRecordsOutsideCanonicalEntityOrder()
    {
        using var world = new World();
        Entity first = world.CreateEntity();
        Entity second = world.CreateEntity();
        Entity edgeA = world.CreateEntity(new CanonicalRelationPayload { Value = 1 });
        Entity edgeB = world.CreateEntity(new CanonicalRelationPayload { Value = 2 });
        RelationTopologySerializationRuntime<CanonicalRelationPayload> runtime = CreateRelationRuntime();
        byte[] bytes = Encode(writer =>
        {
            var data = new DataWriter(writer);
            WriteRelationSchema(writer);
            writer.Write(2);
            data.WriteEntity(edgeB);
            data.WriteEntity(first);
            data.WriteEntity(second);
            data.WriteEntity(edgeA);
            data.WriteEntity(first);
            data.WriteEntity(second);
            writer.Write(0);
        });

        InvalidDataException error = Assert.Throws<InvalidDataException>(() =>
            ReadRuntime(bytes, runtime, world));

        Assert.Contains("relation edge", error.Message, StringComparison.Ordinal);
        Assert.Contains("canonical entity order", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RelationRead_RejectsOrderedSequencesOutsideCanonicalEndpointRoleOrder()
    {
        using var world = new World();
        Entity endpointA = world.CreateEntity();
        Entity endpointB = world.CreateEntity();
        Entity endpointC = world.CreateEntity();
        Entity outgoingEdge = world.CreateEntity(new CanonicalRelationPayload { Value = 1 });
        Entity incomingEdge = world.CreateEntity(new CanonicalRelationPayload { Value = 2 });
        RelationTopologySerializationRuntime<CanonicalRelationPayload> runtime = CreateRelationRuntime();
        byte[] bytes = Encode(writer =>
        {
            var data = new DataWriter(writer);
            WriteRelationSchema(writer);
            writer.Write(2);
            data.WriteEntity(outgoingEdge);
            data.WriteEntity(endpointA);
            data.WriteEntity(endpointB);
            data.WriteEntity(incomingEdge);
            data.WriteEntity(endpointC);
            data.WriteEntity(endpointA);
            writer.Write(2);
            data.WriteEntity(endpointA);
            writer.Write((byte)RelationAdjacencyRole.Incoming);
            writer.Write(1);
            data.WriteEntity(incomingEdge);
            data.WriteEntity(endpointA);
            writer.Write((byte)RelationAdjacencyRole.Outgoing);
            writer.Write(1);
            data.WriteEntity(outgoingEdge);
        });

        InvalidDataException error = Assert.Throws<InvalidDataException>(() =>
            ReadRuntime(bytes, runtime, world));

        Assert.Contains("canonical endpoint/role order", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void OrderedRelationImport_UsesOneLookupPerMemberAndPublishesTheFinalArray()
    {
        const int edgeCount = 2_048;
        using var world = new World();
        Entity source = world.CreateEntity();
        var targets = new Entity[edgeCount];
        var edges = new Entity[edgeCount];
        for (int i = 0; i < edgeCount; i++)
        {
            targets[i] = world.CreateEntity();
            edges[i] = world.CreateEntity(new CanonicalRelationPayload { Value = i });
        }

        RelationSchema schema = RelationSchema.For<CanonicalRelationPayload>();
        RelationTopologyImport<CanonicalRelationPayload> import =
            world.BeginRelationTopologyImport<CanonicalRelationPayload>(
                schema.Direction,
                schema.Cardinality,
                schema.AllowSelfEdge,
                edgeCount);
        for (int i = 0; i < edgeCount; i++)
            import.AddEdge(edges[i], source, targets[i]);

        import.SetOrderedSequenceCount(1);
        RelationTopologyImport<CanonicalRelationPayload>.OrderedSequence sequence =
            import.BeginOrderedSequence(source, RelationAdjacencyRole.Outgoing, edgeCount);
        object finalBacking = Assert.IsType<RelationAdjacencyEntry<CanonicalRelationPayload>[]>(
            sequence.PendingBackingIdentity);
        for (int i = 0; i < edgeCount; i++)
            sequence.AddEdge(edges[i]);

        Assert.Equal(edgeCount, sequence.DuplicateLookupCount);
        Assert.Equal(edgeCount, sequence.RetainedDuplicateMetadataCount);
        sequence.Complete();
        Assert.Equal(0, sequence.RetainedDuplicateMetadataCount);
        Assert.Null(sequence.PendingBackingIdentity);
        import.Complete();
        Assert.Equal(0, import.RetainedMembershipMetadataCount);

        RelationTypeState<CanonicalRelationPayload> state =
            Assert.IsType<RelationTypeState<CanonicalRelationPayload>>(
                world.RelationGraph.PrepareSerializationWrite<CanonicalRelationPayload>());
        Assert.True(MemoryMarshal.TryGetArray(
            state.SerializationGeneration.Outgoing[source].EntryMemory,
            out ArraySegment<RelationAdjacencyEntry<CanonicalRelationPayload>> segment));
        Assert.Same(finalBacking, segment.Array);
    }

    [Fact]
    public void OrderedRelationImport_StillRejectsDuplicateMembers()
    {
        using var world = new World();
        Entity source = world.CreateEntity();
        Entity target = world.CreateEntity();
        Entity firstEdge = world.CreateEntity(new CanonicalRelationPayload { Value = 1 });
        Entity secondEdge = world.CreateEntity(new CanonicalRelationPayload { Value = 2 });
        RelationSchema schema = RelationSchema.For<CanonicalRelationPayload>();
        RelationTopologyImport<CanonicalRelationPayload> import =
            world.BeginRelationTopologyImport<CanonicalRelationPayload>(
                schema.Direction,
                schema.Cardinality,
                schema.AllowSelfEdge,
                2);
        import.AddEdge(firstEdge, source, target);
        import.AddEdge(secondEdge, source, target);
        import.SetOrderedSequenceCount(1);
        RelationTopologyImport<CanonicalRelationPayload>.OrderedSequence sequence =
            import.BeginOrderedSequence(source, RelationAdjacencyRole.Outgoing, 2);
        sequence.AddEdge(firstEdge);

        InvalidDataException error = Assert.Throws<InvalidDataException>(() =>
            sequence.AddEdge(firstEdge));

        Assert.Contains("repeats edge", error.Message, StringComparison.Ordinal);
        Assert.Equal(2, sequence.DuplicateLookupCount);
    }

    private static RelationTopologySerializationRuntime<CanonicalRelationPayload>
        CreateRelationRuntime()
    {
        var registry = new SerializationRegistry()
            .Register<CanonicalRelationPayload, CanonicalRelationPayloadCodec>(s_relationPayloadKey);
        return new RelationTopologySerializationRuntime<CanonicalRelationPayload>(
            s_relationTopologyKey,
            Assert.Single(registry.Entries.ToArray()));
    }

    private static void WriteRelationSchema(BinaryWriter writer)
    {
        RelationSchema schema = RelationSchema.For<CanonicalRelationPayload>();
        writer.Write((byte)schema.Direction);
        writer.Write((byte)schema.Cardinality);
        writer.Write(schema.AllowSelfEdge);
    }

    private static void ReadTopologySection(
        byte[] bytes,
        SerializationRegistry registry,
        World world)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        using var reader = new BinaryReader(stream, SerializationBinary.StrictUtf8, leaveOpen: true);
        TopologyCodec.ReadApply(
            reader,
            registry,
            new SerializationReadBudget(new SerializationReadLimits()),
            SerializationContract.DurableSave,
            world,
            remapper: null);
    }

    private static void ReadRuntime(
        byte[] bytes,
        TopologySerializationRuntime runtime,
        World world)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        using var reader = new BinaryReader(stream, SerializationBinary.StrictUtf8, leaveOpen: true);
        runtime.ReadApply(
            reader,
            new SerializationReadBudget(new SerializationReadLimits()),
            world,
            remapper: null);
    }

    private static byte[] Encode(Action<BinaryWriter> write)
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, SerializationBinary.StrictUtf8, leaveOpen: true))
        {
            write(writer);
            writer.Flush();
        }
        return stream.ToArray();
    }

    private readonly struct HierarchyDomainA : IHierarchyDomain { }

    private readonly struct HierarchyDomainB : IHierarchyDomain { }

    [RelationSchema(RelationDirection.Directed, RelationCardinality.Parallel)]
    private struct CanonicalRelationPayload : IComponent
    {
        public int Value;
    }

    private readonly struct CanonicalRelationPayloadCodec : IComponentCodec<CanonicalRelationPayload>
    {
        public void Write(ref DataWriter writer, in CanonicalRelationPayload value) =>
            writer.WriteInt32(value.Value);

        public void Read(ref DataReader reader, out CanonicalRelationPayload value) =>
            value = new CanonicalRelationPayload { Value = reader.ReadInt32() };
    }
}
