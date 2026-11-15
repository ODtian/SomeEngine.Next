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
    public void TopologyRead_RejectsServiceCountDifferentFromManifest()
    {
        using var world = new World();
        Hierarchy<HierarchyDomainA>.SetParent(world, world.Create(), world.Create());
        var registry = new SerializationRegistry().RegisterHierarchy<HierarchyDomainA>(s_hierarchyKeyA);
        TopologyFile file = TopologyFile.Save(world, registry);
        file.SetInt(file.ServiceCountOffset, 2);
        InvalidDataException error = Assert.Throws<InvalidDataException>(() => file.Read(registry));
        Assert.Equal(SerializationError.InvalidData, SerializationErrors.GetError(error));
    }

    [Fact]
    public void TopologyRead_RejectsStableNameAliasBeforePayloadCodec()
    {
        using var world = new World();
        Hierarchy<HierarchyDomainA>.SetParent(world, world.Create(), world.Create());
        var registry = new SerializationRegistry().RegisterHierarchy<HierarchyDomainA>(s_hierarchyKeyA);
        TopologyFile file = TopologyFile.Save(world, registry);
        TopologyFile.TypeField parent = Assert.Single(file.Types, field => field.Storage == 6);
        file.Bytes[parent.NameOffset] ^= 1;
        InvalidDataException error = Assert.Throws<InvalidDataException>(() => file.Read(registry));
        Assert.Equal(SerializationError.SchemaMismatch, SerializationErrors.GetError(error));
    }

    [Fact]
    public void TopologyRead_RejectsServicesOutsideRegistrationOrder()
    {
        using var world = new World();
        Entity parent = world.Create();
        Entity child = world.Create();
        Hierarchy<HierarchyDomainB>.SetParent(world, child, parent);
        Hierarchy<HierarchyDomainA>.SetParent(world, child, parent);
        var registry = new SerializationRegistry()
            .RegisterHierarchy<HierarchyDomainB>(s_hierarchyKeyB)
            .RegisterHierarchy<HierarchyDomainA>(s_hierarchyKeyA);
        TopologyFile file = TopologyFile.Save(world, registry);
        Assert.Equal(2, file.ServiceIndices.Count);
        file.Bytes.AsSpan(file.ServiceIndices[1], 4).CopyTo(file.Bytes.AsSpan(file.ServiceIndices[0], 4));
        InvalidDataException error = Assert.Throws<InvalidDataException>(() => file.Read(registry));
        Assert.Equal(SerializationError.InvalidData, SerializationErrors.GetError(error));
    }

    [Fact]
    public void HierarchyRead_RejectsDuplicateParentMainValues()
    {
        using var world = new World();
        Entity parent = world.Create();
        Hierarchy<HierarchyDomainA>.SetParent(world, world.Create(), parent);
        Hierarchy<HierarchyDomainA>.SetParent(world, world.Create(), parent);
        var registry = new SerializationRegistry().RegisterHierarchy<HierarchyDomainA>(s_hierarchyKeyA);
        TopologyFile file = TopologyFile.Save(world, registry);
        TopologyFile.ValueField[] parents = file.Values.Where(value => file.Types[value.TypeIndex].Storage == 6).ToArray();
        Assert.Equal(2, parents.Length);
        file.CopyEntity(parents[0].EntityOffset, parents[1].EntityOffset);
        InvalidDataException error = Assert.Throws<InvalidDataException>(() => file.Read(registry));
        Assert.Equal(SerializationError.InvalidData, SerializationErrors.GetError(error));
    }

    [Fact]
    public void HierarchyRead_PreservesOrderedChildrenAcrossTableRanges()
    {
        using var world = new World();
        Entity parentA = world.Create();
        Entity childA = world.Create();
        Entity parentB = world.Create();
        Entity childB = world.Create();
        Hierarchy<HierarchyDomainA>.SetOrdered(world, parentB, true);
        Hierarchy<HierarchyDomainA>.SetParent(world, childB, parentB);
        Hierarchy<HierarchyDomainA>.SetOrdered(world, parentA, true);
        Hierarchy<HierarchyDomainA>.SetParent(world, childA, parentA);
        var registry = new SerializationRegistry().RegisterHierarchy<HierarchyDomainA>(s_hierarchyKeyA);
        TopologyFile file = TopologyFile.Save(world, registry);
        using var input = new MemoryStream(file.Bytes, writable: false);
        using World loaded = WorldSerializer.ReadWorld(input, registry);
        Assert.Equal(new[] { childA }, TopologyValues.Children<HierarchyDomainA>(loaded, parentA));
        Assert.Equal(new[] { childB }, TopologyValues.Children<HierarchyDomainA>(loaded, parentB));
        Assert.True(Hierarchy<HierarchyDomainA>.IsOrdered(loaded, parentA));
        Assert.True(Hierarchy<HierarchyDomainA>.IsOrdered(loaded, parentB));
    }

    [Fact]
    public void DeepHierarchySavePreservesEveryParent()
    {
        const int parentCount = 4096;
        using var world = new World();
        var entities = new Entity[parentCount + 1];
        for (int i = 0; i < entities.Length; i++) entities[i] = world.Create();
        for (int i = 1; i < entities.Length; i++) Hierarchy<HierarchyDomainA>.SetParent(world, entities[i], entities[i - 1]);
        var registry = new SerializationRegistry().RegisterHierarchy<HierarchyDomainA>(s_hierarchyKeyA);
        using var stream = new MemoryStream();
        new WorldSerializer(registry).Save(world, stream, new SerializeOptions(Purpose: SavePurpose.DurableSave));
        stream.Position = 0;
        using World loaded = WorldSerializer.ReadWorld(stream, registry);
        for (int i = 1; i < entities.Length; i++) Assert.Equal(entities[i - 1], Hierarchy<HierarchyDomainA>.GetParent(loaded, entities[i]));
    }

    [Fact]
    public void HierarchyReadRejectsParentCycles()
    {
        using var world = new World();
        Entity first = world.Create();
        Entity second = world.Create();
        Entity third = world.Create();
        Hierarchy<HierarchyDomainA>.SetParent(world, first, second);
        Hierarchy<HierarchyDomainA>.SetParent(world, second, third);
        var registry = new SerializationRegistry().RegisterHierarchy<HierarchyDomainA>(s_hierarchyKeyA);
        TopologyFile file = TopologyFile.Save(world, registry);
        TopologyFile.ValueField parent = Assert.Single(file.Values, value => file.Types[value.TypeIndex].Storage == 6
            && System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(file.Bytes.AsSpan(value.EntityOffset)) == second.Id);
        file.SetInt(parent.ValueOffset, first.Id);
        file.SetInt(parent.ValueOffset + 4, first.Generation);
        InvalidDataException error = Assert.Throws<InvalidDataException>(() => file.Read(registry));
        Assert.Equal(SerializationError.InvalidData, SerializationErrors.GetError(error));
    }

    [Fact]
    public void RelationRead_RejectsDuplicateEdgeMainValues()
    {
        using var world = new World();
        Entity source = world.Create(); Entity target = world.Create();
        world.CreateRelation(source, target, new CanonicalRelationPayload { Value = 1 });
        world.CreateRelation(source, target, new CanonicalRelationPayload { Value = 2 });
        SerializationRegistry registry = CreateRelationRegistry();
        TopologyFile file = TopologyFile.Save(world, registry);
        TopologyFile.ValueField[] edges = file.Values.Where(value => file.Types[value.TypeIndex].Storage == 7).ToArray();
        Assert.Equal(2, edges.Length);
        file.CopyEntity(edges[0].EntityOffset, edges[1].EntityOffset);
        InvalidDataException error = Assert.Throws<InvalidDataException>(() => file.Read(registry));
        Assert.Equal(SerializationError.InvalidData, SerializationErrors.GetError(error));
    }

    [Fact]
    public void RelationRead_RejectsAdjacencyMemberFromDifferentEndpointRole()
    {
        using var world = new World();
        Entity endpointA = world.Create(); Entity endpointB = world.Create(); Entity endpointC = world.Create();
        world.CreateRelation(endpointA, endpointB, new CanonicalRelationPayload { Value = 1 });
        RelationEdge<CanonicalRelationPayload> incoming = world.CreateRelation(endpointC, endpointA, new CanonicalRelationPayload { Value = 2 });
        Relations<CanonicalRelationPayload>.SetOrdered(world, endpointA, RelationAdjacencyRole.Outgoing, true);
        Relations<CanonicalRelationPayload>.SetOrdered(world, endpointA, RelationAdjacencyRole.Incoming, true);
        SerializationRegistry registry = CreateRelationRegistry();
        TopologyFile file = TopologyFile.Save(world, registry);
        TopologyFile.HeadField outgoing = Assert.Single(file.Heads, head => !head.Incoming && head.EntityId == endpointA.Id);
        file.SetInt(outgoing.MembersOffset + 8, incoming.Entity.Id);
        file.SetInt(outgoing.MembersOffset + 12, incoming.Entity.Generation);
        InvalidDataException error = Assert.Throws<InvalidDataException>(() => file.Read(registry));
        Assert.Equal(SerializationError.InvalidData, SerializationErrors.GetError(error));
    }

    [Fact]
    public void OrderedRelationSavePreservesEdgeOrder()
    {
        const int edgeCount = 2048;
        using var world = new World();
        Entity source = world.Create();
        world.SetRelationAdjacencyOrder<CanonicalRelationPayload>(source, RelationAdjacencyRole.Outgoing, RelationAdjacencyOrderPolicy.Ordered);
        var edges = new RelationEdge<CanonicalRelationPayload>[edgeCount];
        for (int i = 0; i < edgeCount; i++) edges[i] = world.CreateRelation(source, world.Create(), new CanonicalRelationPayload { Value = i });
        world.ReorderRelationAdjacency(source, RelationAdjacencyRole.Outgoing, edges[^1], 0);
        RelationEdge<CanonicalRelationPayload>[] expected = [edges[^1], .. edges[..^1]];
        SerializationRegistry registry = CreateRelationRegistry();
        using var stream = new MemoryStream();
        new WorldSerializer(registry).Save(world, stream, new SerializeOptions(Purpose: SavePurpose.DurableSave));
        stream.Position = 0;
        using World loaded = WorldSerializer.ReadWorld(stream, registry);
        Assert.Equal(expected, TopologyValues.Outgoing<CanonicalRelationPayload>(loaded, source));
    }

    [Fact]
    public void OrderedRelationReadRejectsDuplicateMembers()
    {
        using var world = new World();
        Entity source = world.Create(); Entity target = world.Create();
        world.CreateRelation(source, target, new CanonicalRelationPayload { Value = 1 });
        world.CreateRelation(source, target, new CanonicalRelationPayload { Value = 2 });
        Relations<CanonicalRelationPayload>.SetOrdered(world, source, RelationAdjacencyRole.Outgoing, true);
        SerializationRegistry registry = CreateRelationRegistry();
        TopologyFile file = TopologyFile.Save(world, registry);
        TopologyFile.HeadField head = Assert.Single(file.Heads, head => !head.Incoming && head.EntityId == source.Id);
        Assert.Equal(2, head.Count);
        file.Bytes.AsSpan(head.MembersOffset, 16).CopyTo(file.Bytes.AsSpan(head.MembersOffset + 16, 16));
        Assert.Throws<InvalidDataException>(() => file.Read(registry));
    }

    private static SerializationRegistry CreateRelationRegistry() => new SerializationRegistry()
        .Register<CanonicalRelationPayload, CanonicalRelationPayloadCodec>(s_relationPayloadKey)
        .RegisterRelation<CanonicalRelationPayload>(s_relationTopologyKey);

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
