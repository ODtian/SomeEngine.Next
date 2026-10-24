using SomeEngine.Serialization;
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

        Assert.Equal(SerializationError.InvalidData, SerializationErrors.GetError(error));
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
            writer.Write((byte)0);
            SaveBytes.WriteTypeKey(writer, alias);
        });

        InvalidDataException error = Assert.Throws<InvalidDataException>(() =>
            ReadTopologySection(bytes, registry, world));

        Assert.Equal(SerializationError.SchemaMismatch, SerializationErrors.GetError(error));
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
            writer.Write((byte)0);
            SaveBytes.WriteTypeKey(writer, s_hierarchyKeyB);
        });

        InvalidDataException error = Assert.Throws<InvalidDataException>(() =>
            ReadTopologySection(bytes, registry, world));

        Assert.Equal(SerializationError.InvalidData, SerializationErrors.GetError(error));
    }

    [Fact]
    public void HierarchyRead_RejectsParentRecordsOutsideCanonicalEntityOrder()
    {
        using var world = new World();
        Entity parentA = world.CreateEntity();
        Entity childA = world.CreateEntity();
        Entity parentB = world.CreateEntity();
        Entity childB = world.CreateEntity();
        var registry = new SerializationRegistry().RegisterHierarchyDomain<HierarchyDomainA>(s_hierarchyKeyA);
        byte[] bytes = Encode(writer =>
        {
            writer.Write(2);
            SaveBytes.WriteEntity(writer, childB);
            SaveBytes.WriteEntity(writer, parentB);
            SaveBytes.WriteEntity(writer, childA);
            SaveBytes.WriteEntity(writer, parentA);
            writer.Write(0);
        });

        InvalidDataException error = Assert.Throws<InvalidDataException>(() =>
            ReadHierarchyPayload(bytes, registry, world));

        Assert.Equal(SerializationError.InvalidData, SerializationErrors.GetError(error));
    }

    [Fact]
    public void HierarchyRead_RejectsOrderedSequencesOutsideCanonicalEntityOrder()
    {
        using var world = new World();
        Entity parentA = world.CreateEntity();
        Entity childA = world.CreateEntity();
        Entity parentB = world.CreateEntity();
        Entity childB = world.CreateEntity();
        var registry = new SerializationRegistry().RegisterHierarchyDomain<HierarchyDomainA>(s_hierarchyKeyA);
        byte[] bytes = Encode(writer =>
        {
            writer.Write(2);
            SaveBytes.WriteEntity(writer, childA);
            SaveBytes.WriteEntity(writer, parentA);
            SaveBytes.WriteEntity(writer, childB);
            SaveBytes.WriteEntity(writer, parentB);
            writer.Write(2);
            SaveBytes.WriteEntity(writer, parentB);
            writer.Write(1);
            SaveBytes.WriteEntity(writer, childB);
            SaveBytes.WriteEntity(writer, parentA);
            writer.Write(1);
            SaveBytes.WriteEntity(writer, childA);
        });

        InvalidDataException error = Assert.Throws<InvalidDataException>(() =>
            ReadHierarchyPayload(bytes, registry, world));

        Assert.Equal(SerializationError.InvalidData, SerializationErrors.GetError(error));
    }

    [Fact]
    public void DeepHierarchySavePreservesEveryParent()
    {
        const int parentCount = 4096;
        using var world = new World();
        var entities = new Entity[parentCount + 1];
        for (int i = 0; i < entities.Length; i++)
            entities[i] = world.CreateEntity();
        for (int i = 1; i < entities.Length; i++)
            Hierarchy<HierarchyDomainA>.SetParent(world, entities[i], entities[i - 1]);
        var registry = new SerializationRegistry().RegisterHierarchyDomain<HierarchyDomainA>(s_hierarchyKeyA);
        using var stream = new MemoryStream();
        WorldSerializer.WriteDurableWorld(stream, world, registry);
        stream.Position = 0;
        using World loaded = WorldSerializer.ReadDurableWorld(stream, registry);
        for (int i = 1; i < entities.Length; i++)
            Assert.Equal(entities[i - 1], Hierarchy<HierarchyDomainA>.GetParent(loaded, entities[i]));
    }

    [Fact]
    public void HierarchyReadRejectsCyclesBeforeReadingTheOrderedSection()
    {
        using var world = new World();
        Entity first = world.CreateEntity();
        Entity second = world.CreateEntity();
        Entity third = world.CreateEntity();
        var registry = new SerializationRegistry().RegisterHierarchyDomain<HierarchyDomainA>(s_hierarchyKeyA);
        byte[] bytes = Encode(writer =>
        {
            writer.Write(3);
            SaveBytes.WriteEntity(writer, first);
            SaveBytes.WriteEntity(writer, second);
            SaveBytes.WriteEntity(writer, second);
            SaveBytes.WriteEntity(writer, third);
            SaveBytes.WriteEntity(writer, third);
            SaveBytes.WriteEntity(writer, first);
        });

        InvalidDataException error = Assert.Throws<InvalidDataException>(() =>
            ReadHierarchyPayload(bytes, registry, world));

        Assert.Equal(SerializationError.InvalidData, SerializationErrors.GetError(error));
    }

    [Fact]
    public void RelationRead_RejectsEdgeRecordsOutsideCanonicalEntityOrder()
    {
        using var world = new World();
        Entity first = world.CreateEntity();
        Entity second = world.CreateEntity();
        Entity edgeA = world.CreateEntity(new CanonicalRelationPayload { Value = 1 });
        Entity edgeB = world.CreateEntity(new CanonicalRelationPayload { Value = 2 });
        SerializationRegistry registry = CreateRelationRegistry();
        byte[] bytes = Encode(writer =>
        {
            WriteRelationSchema(writer);
            writer.Write(2);
            SaveBytes.WriteEntity(writer, edgeB);
            SaveBytes.WriteEntity(writer, first);
            SaveBytes.WriteEntity(writer, second);
            SaveBytes.WriteEntity(writer, edgeA);
            SaveBytes.WriteEntity(writer, first);
            SaveBytes.WriteEntity(writer, second);
            writer.Write(0);
        });

        InvalidDataException error = Assert.Throws<InvalidDataException>(() =>
            ReadRelationPayload(bytes, registry, world));

        Assert.Equal(SerializationError.InvalidData, SerializationErrors.GetError(error));
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
        SerializationRegistry registry = CreateRelationRegistry();
        byte[] bytes = Encode(writer =>
        {
            WriteRelationSchema(writer);
            writer.Write(2);
            SaveBytes.WriteEntity(writer, outgoingEdge);
            SaveBytes.WriteEntity(writer, endpointA);
            SaveBytes.WriteEntity(writer, endpointB);
            SaveBytes.WriteEntity(writer, incomingEdge);
            SaveBytes.WriteEntity(writer, endpointC);
            SaveBytes.WriteEntity(writer, endpointA);
            writer.Write(2);
            SaveBytes.WriteEntity(writer, endpointA);
            writer.Write((byte)RelationAdjacencyRole.Incoming);
            writer.Write(1);
            SaveBytes.WriteEntity(writer, incomingEdge);
            SaveBytes.WriteEntity(writer, endpointA);
            writer.Write((byte)RelationAdjacencyRole.Outgoing);
            writer.Write(1);
            SaveBytes.WriteEntity(writer, outgoingEdge);
        });

        InvalidDataException error = Assert.Throws<InvalidDataException>(() =>
            ReadRelationPayload(bytes, registry, world));

        Assert.Equal(SerializationError.InvalidData, SerializationErrors.GetError(error));
    }

    [Fact]
    public void OrderedRelationSavePreservesEdgeOrder()
    {
        const int edgeCount = 2048;
        using var world = new World();
        Entity source = world.CreateEntity();
        world.SetRelationAdjacencyOrder<CanonicalRelationPayload>(source,
            RelationAdjacencyRole.Outgoing, RelationAdjacencyOrderPolicy.Ordered);
        var edges = new RelationEdge<CanonicalRelationPayload>[edgeCount];
        for (int i = 0; i < edgeCount; i++)
            edges[i] = world.CreateRelation(source, world.CreateEntity(), new CanonicalRelationPayload { Value = i });
        world.ReorderRelationAdjacency(source, RelationAdjacencyRole.Outgoing, edges[^1], 0);
        RelationEdge<CanonicalRelationPayload>[] expected = [edges[^1], .. edges[..^1]];
        var registry = CreateRelationRegistry();
        using var stream = new MemoryStream();
        WorldSerializer.WriteDurableWorld(stream, world, registry);
        stream.Position = 0;
        using World loaded = WorldSerializer.ReadDurableWorld(stream, registry);
        Assert.Equal(expected, loaded.GetOrderedOutgoingRelations<CanonicalRelationPayload>(source)
            .Entries.ToArray().Select(entry => entry.Edge));
    }

    [Fact]
    public void OrderedRelationReadRejectsDuplicateMembers()
    {
        using var world = new World();
        Entity source = world.CreateEntity();
        Entity target = world.CreateEntity();
        Entity firstEdge = world.CreateEntity(new CanonicalRelationPayload { Value = 1 });
        Entity secondEdge = world.CreateEntity(new CanonicalRelationPayload { Value = 2 });
        byte[] bytes = Encode(writer =>
        {
            WriteRelationSchema(writer);
            writer.Write(2);
            SaveBytes.WriteEntity(writer, firstEdge);
            SaveBytes.WriteEntity(writer, source);
            SaveBytes.WriteEntity(writer, target);
            SaveBytes.WriteEntity(writer, secondEdge);
            SaveBytes.WriteEntity(writer, source);
            SaveBytes.WriteEntity(writer, target);
            writer.Write(1);
            SaveBytes.WriteEntity(writer, source);
            writer.Write((byte)RelationAdjacencyRole.Outgoing);
            writer.Write(2);
            SaveBytes.WriteEntity(writer, firstEdge);
            SaveBytes.WriteEntity(writer, firstEdge);
        });
        Assert.Throws<InvalidDataException>(() => ReadRelationPayload(bytes, CreateRelationRegistry(), world));
    }

    private static SerializationRegistry CreateRelationRegistry() => new SerializationRegistry()
        .Register<CanonicalRelationPayload, CanonicalRelationPayloadCodec>(s_relationPayloadKey)
        .RegisterRelationTopology<CanonicalRelationPayload>(s_relationTopologyKey);

    private static void WriteRelationSchema(BinaryWriter writer)
    {
        RelationSchema schema = RelationSchema.For<CanonicalRelationPayload>();
        writer.Write((byte)schema.Direction);
        writer.Write((byte)schema.Cardinality);
        writer.Write(schema.AllowSelfEdge);
    }

    private static void ReadTopologySection(byte[] bytes, SerializationRegistry registry, World world)
    {
        // The component-only save ends with a zero topology count. Replace that complete section.
        var values = new SerializationRegistry()
            .Register<CanonicalRelationPayload, CanonicalRelationPayloadCodec>(s_relationPayloadKey);
        using var stream = new MemoryStream();
        WorldSerializer.WriteDurableWorld(stream, world, values);
        stream.SetLength(stream.Length - sizeof(int));
        stream.Position = stream.Length;
        stream.Write(bytes);
        stream.Position = 0;
        using World loaded = WorldSerializer.ReadDurableWorld(stream, registry);
    }

    private static void ReadHierarchyPayload(byte[] bytes, SerializationRegistry registry, World world) =>
        ReadTopologyPayload(bytes, registry, world, 0, s_hierarchyKeyA);

    private static void ReadRelationPayload(byte[] bytes, SerializationRegistry registry, World world) =>
        ReadTopologyPayload(bytes, registry, world, 1, s_relationTopologyKey);

    private static void ReadTopologyPayload(byte[] bytes, SerializationRegistry registry, World world,
        byte kind, SerializationTypeKey key)
    {
        byte[] section = Encode(writer =>
        {
            writer.Write(1);
            writer.Write(kind);
            SaveBytes.WriteTypeKey(writer, key);
            writer.Write(bytes);
            writer.Write(bytes.Length);
        });
        ReadTopologySection(section, registry, world);
    }

    private static byte[] Encode(Action<BinaryWriter> write)
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, SaveBytes.Utf8, leaveOpen: true))
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
