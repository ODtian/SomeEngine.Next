using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Hierarchy;

namespace SomeEngine.ECS.Serialization.Tests;

[SerializableComponent("0826DE90-C146-4EA8-9139-A7CF22DF0E0E")]
public partial struct SavedRow : IComponent
{
    public int Value;
    public Entity Target;
}

public sealed class WorldBatchEncodingTests
{
    [Fact]
    public void GeneratedRowsRetainBytesEntityReferencesAndHierarchyAfterWorldRestore()
    {
        var registry = new SerializationRegistry().RegisterHierarchyDomain<DefaultHierarchyDomain>();
        GameSerializationModule.RegisterAll(registry);
        using var world = new World();
        Entity root = world.CreateEntity();
        var entities = new Entity[1200];
        for (int index = 0; index < entities.Length; index++)
        {
            entities[index] = world.CreateEntity(new SavedRow { Value = index, Target = root });
            Hierarchy<DefaultHierarchyDomain>.SetParent(world, entities[index], root);
        }
        using var first = new MemoryStream();
        using var second = new MemoryStream();
        WorldSerializer.WriteWorld(first, world, registry);
        WorldSerializer.WriteWorld(second, world, registry);
        Assert.Equal(first.ToArray(), second.ToArray());
        first.Position = 0;
        using World restored = WorldSerializer.ReadWorld(first, registry);
        for (int index = 0; index < entities.Length; index++)
        {
            SavedRow row = restored.Read<SavedRow>(entities[index]);
            Assert.Equal(index, row.Value);
            Assert.Equal(root, row.Target);
            Assert.Equal(root, Hierarchy<DefaultHierarchyDomain>.GetParent(restored, entities[index]));
        }
        Assert.Equal(entities.Length, Hierarchy<DefaultHierarchyDomain>.GetChildren(restored, root).Count);
    }

    [Fact]
    public void SharedCodecStateRetainsItsWriteOrderAndExternalReferences()
    {
        var state = new WriteCount();
        var registry = new SerializationRegistry().RegisterCodec<CountedRow, CountedRowCodec>(
            new(Guid.Parse("395299C1-7327-41CB-9CB0-9670193668EF"), "counted rows", 0x1810202600000001ul), new(state));
        using var world = new World();
        var entities = new Entity[1024];
        var expectedReferences = new HashSet<ExternalReferenceKey>();
        for (int index = 0; index < entities.Length; index++)
        {
            var reference = new ExternalReferenceKey(new Guid(index % 8 + 1, 0, 0, new byte[8]));
            entities[index] = world.CreateEntity(new CountedRow { Value = index, Asset = reference });
            expectedReferences.Add(reference);
        }
        var references = new HashSet<ExternalReferenceKey>();
        using var stream = new MemoryStream();
        WorldSerializer.WriteWorld(stream, world, registry, new(ExternalReferences: references));
        Assert.True(references.SetEquals(expectedReferences));
        stream.Position = 0;
        using World restored = WorldSerializer.ReadWorld(stream, registry);
        for (int index = 0; index < entities.Length; index++)
        {
            CountedRow row = restored.Read<CountedRow>(entities[index]);
            Assert.Equal(index, row.Value);
            Assert.Equal(index, row.Sequence);
            Assert.Equal(world.Read<CountedRow>(entities[index]).Asset, row.Asset);
        }
    }

    private struct CountedRow : IComponent
    {
        public int Value;
        public int Sequence;
        public ExternalReferenceKey Asset;
    }

    private sealed class WriteCount { internal int Value; }

    private readonly struct CountedRowCodec(WriteCount count) : IComponentCodec<CountedRow>
    {
        public void Write(ref DataWriter writer, in CountedRow value)
        {
            writer.WriteInt32(value.Value);
            writer.WriteInt32(count.Value++);
            writer.WriteExternalReference(value.Asset);
        }

        public void Read(ref DataReader reader, out CountedRow value) => value = new()
        {
            Value = reader.ReadInt32(), Sequence = reader.ReadInt32(), Asset = reader.ReadExternalReference(),
        };
    }
}
