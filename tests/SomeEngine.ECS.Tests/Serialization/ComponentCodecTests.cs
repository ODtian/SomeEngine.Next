using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Storage;
using SomeEngine.ECS.Hierarchy;

namespace SomeEngine.ECS.Serialization.Tests;

public sealed class ComponentCodecTests
{
    [Theory]
    [InlineData(SavePurpose.RawCheckpoint)]
    [InlineData(SavePurpose.DurableSave)]
    public void GeneratedDescriptionsRestoreValuesAndEntityReferencesAcrossStorageKinds(SavePurpose purpose)
    {
        using var world = new World();
        Entity referenced = world.Create(new DescribedPosition { Value = 2 });
        Entity entity = world.Create(new DescribedPosition { Value = 7, Target = referenced });
        world.Set(entity, new DescribedGroup { Value = 4 });
        world.Set(entity, new DescribedLabel { Value = "stored" });
        world.Add(entity, new DescribedVisibility());
        world.Disable<DescribedVisibility>(entity);
        world.AddBuffer<DescribedElement>(entity);
        using (DynamicBuffer<DescribedElement> buffer = world.GetBuffer<DescribedElement>(entity))
            buffer.Add(new DescribedElement { Value = 9, Target = referenced });
        using var bytes = new MemoryStream();

        var registry = new SerializationRegistry();
        GameSerializationModule.RegisterAll(registry);

        new WorldSerializer(registry).Save(world, bytes, new SerializeOptions(Purpose: purpose));
        bytes.Position = 0;
        using World restored = WorldSerializer.ReadWorld(bytes, registry,
            new WorldLoadOptions(IdentityMode: EntityIdentityMode.Remap, RequiredPurpose: purpose));

        Entity result = Entity.Null;
        using (QueryScope scope = restored.Query<DescribedPosition>().OpenRead())
            foreach (QueryChunk chunk in scope)
                for (int row = 0; row < chunk.Count; row++)
                    if (chunk.Read<DescribedPosition>()[row].Value == 7) result = chunk.Entities[row];
        Assert.Equal(2, restored.Query<DescribedPosition>().Count);
        Entity target = restored.Read<DescribedPosition>(result).Target;
        Assert.Equal(2, restored.Read<DescribedPosition>(target).Value);
        Assert.Equal(4, restored.Read<DescribedGroup>(result).Value);
        Assert.Equal("stored", restored.Read<DescribedLabel>(result).Value);
        Assert.False(restored.IsEnabled<DescribedVisibility>(result));
        Assert.Equal(0, restored.Query<DescribedVisibility>().Count);
        Assert.Equal(1, restored.Query(new QueryDescription().WithDisabled<DescribedVisibility>()).Count);
        using ReadOnlyBuffer<DescribedElement> elements = restored.ReadBuffer<DescribedElement>(result);
        Assert.Equal(1, elements.Count);
        Assert.Equal(new DescribedElement { Value = 9, Target = target }, elements[0]);
    }

    [Fact]
    public void RegisteredCodecsKeepTheStateTheyWereRegisteredWith()
    {
        using var world = new World();
        Entity entity = world.Create(new DescribedPosition { Value = 12 });
        var format = new SerializationTypeKey(new Guid("abca3391-45df-4b49-af0e-08acd1219041"), "shifted-position", 1);
        using var bytes = new MemoryStream();

        new WorldSerializer(new SerializationRegistry().Register<DescribedPosition, ShiftedPositionCodec>(format, new(11)))
            .Save(world, bytes, new SerializeOptions(Purpose: SavePurpose.DurableSave));
        bytes.Position = 0;
        using World restored = WorldSerializer.ReadWorld(bytes,
            new SerializationRegistry().Register<DescribedPosition, ShiftedPositionCodec>(format, new(4)));

        Assert.Equal(19, restored.Read<DescribedPosition>(entity).Value);
    }

    [Fact]
    public void RegisteredBufferElementCodecsKeepTheStateTheyWereRegisteredWith()
    {
        using var world = new World();
        Entity entity = world.Create();
        world.AddBuffer<DescribedElement>(entity);
        using (DynamicBuffer<DescribedElement> elements = world.GetBuffer<DescribedElement>(entity))
            elements.Add(new DescribedElement { Value = 3 });
        var format = new SerializationTypeKey(new Guid("6ea337fd-1f64-46fd-bd4b-72f9fa4ef171"), "shifted-element", 1);
        using var bytes = new MemoryStream();

        new WorldSerializer(new SerializationRegistry().RegisterBuffer<DescribedElement, ShiftedElementCodec>(format, new(5)))
            .Save(world, bytes, new SerializeOptions(Purpose: SavePurpose.DurableSave));
        bytes.Position = 0;
        using World restored = WorldSerializer.ReadWorld(bytes,
            new SerializationRegistry().RegisterBuffer<DescribedElement, ShiftedElementCodec>(format, new(2)));

        using ReadOnlyBuffer<DescribedElement> values = restored.ReadBuffer<DescribedElement>(entity);
        Assert.Equal(1, values.Count);
        Assert.Equal(6, values[0].Value);
    }

    private readonly struct ShiftedPositionCodec(int shift) : IDurableComponentCodec<DescribedPosition>
    {
        public void Write(ref DataWriter writer, in DescribedPosition value)
        { writer.WriteInt32(value.Value + shift); writer.WriteEntity(value.Target); }
        public void Read(ref DataReader reader, out DescribedPosition value)
            => value = new() { Value = reader.ReadInt32() - shift, Target = reader.ReadEntity() };
    }

    private readonly struct ShiftedElementCodec(int shift) : IDurableComponentCodec<DescribedElement>
    {
        public void Write(ref DataWriter writer, in DescribedElement value)
        { writer.WriteInt32(value.Value + shift); writer.WriteEntity(value.Target); }
        public void Read(ref DataReader reader, out DescribedElement value)
            => value = new() { Value = reader.ReadInt32() - shift, Target = reader.ReadEntity() };
    }


}

[SerializableComponent("892eb140-5eb7-4b5e-99b4-4b515503ee01")]
public struct DescribedPosition : IComponent
{
    public int Value;
    public Entity Target;
}

[SerializableComponent("6ea337fd-1f64-46fd-bd4b-72f9fa4ef171")]
public struct DescribedElement : IBufferElement
{
    public int Value;
    public Entity Target;
}

[SerializableComponent("d1976b14-1165-422c-b007-b2265229ea9a")]
public struct DescribedGroup : ISharedComponent
{
    public int Value;
}

[SerializableComponent("e4c11701-69d8-40ab-a772-ed9fdf9074c0")]
public struct DescribedLabel : ISparseComponent
{
    public string Value;
}

[SerializableComponent("8f2ab491-1c77-4cf4-895c-71b8b7fcb88a")]
public struct DescribedVisibility : ITag, IEnableableComponent;
