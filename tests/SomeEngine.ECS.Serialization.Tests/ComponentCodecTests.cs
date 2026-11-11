using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Storage;
using SomeEngine.ECS.Hierarchy;

namespace SomeEngine.ECS.Serialization.Tests;

public sealed class ComponentCodecTests
{
    [Fact]
    public void ClosedHierarchyDescriptionsEncodeValuesBeforeAWorldUsesTheDomain()
    {
        var parent = new Parent<DeclaredHierarchy>(new Entity(12, 3));
        ComponentCodec codec = ComponentCodec.Create<Parent<DeclaredHierarchy>, ParentValueCodec>(new());
        using var bytes = new MemoryStream();

        WorldSerializer.WriteComponent(bytes, parent, new(Purpose: SavePurpose.DurableSave, Codecs: [codec]));
        bytes.Position = 0;
        Parent<DeclaredHierarchy> restored = WorldSerializer.ReadComponent<Parent<DeclaredHierarchy>>(bytes,
            new SerializationReadOptions(Codecs: [codec]));

        Assert.Same(ComponentType.Of<Parent<DeclaredHierarchy>>(), codec.Component);
        Assert.Equal(parent, restored);
    }

    [Theory]
    [InlineData(SavePurpose.RawCheckpoint)]
    [InlineData(SavePurpose.DurableSave)]
    public void GeneratedDescriptionsRestoreValuesAndEntityReferencesAcrossStorageKinds(SavePurpose purpose)
    {
        using var world = new World();
        Entity referenced = world.Create(new DescribedPosition { Value = 2 });
        Entity entity = world.Create(new DescribedPosition { Value = 7, Target = referenced });
        world.SetShared(entity, new DescribedGroup { Value = 4 });
        world.SetSparse(entity, new DescribedLabel { Value = "stored" });
        world.Add(entity, new DescribedVisibility());
        world.Disable<DescribedVisibility>(entity);
        world.AddBuffer<DescribedElement>(entity);
        using (DynamicBuffer<DescribedElement> buffer = world.GetBuffer<DescribedElement>(entity))
            buffer.Add(new DescribedElement { Value = 9, Target = referenced });
        using var bytes = new MemoryStream();

        WorldSerializer.WriteWorld(bytes, world, new(Purpose: purpose));
        bytes.Position = 0;
        using World restored = WorldSerializer.ReadWorld(bytes, new WorldLoadOptions(IdentityMode: EntityIdentityMode.Remap, RequiredPurpose: purpose));

        Entity result = Entity.Null;
        using (QueryScope scope = restored.Query<DescribedPosition>().OpenRead())
            foreach (QueryChunk chunk in scope)
                for (int row = 0; row < chunk.Count; row++)
                    if (chunk.Read<DescribedPosition>()[row].Value == 7) result = chunk.Entities[row];
        Assert.Equal(2, restored.Query<DescribedPosition>().Count);
        Entity target = restored.Read<DescribedPosition>(result).Target;
        Assert.Equal(2, restored.Read<DescribedPosition>(target).Value);
        Assert.Equal(4, restored.ReadShared<DescribedGroup>(result).Value);
        Assert.Equal("stored", restored.ReadSparse<DescribedLabel>(result).Value);
        Assert.False(restored.IsEnabled<DescribedVisibility>(result));
        Assert.Equal(0, restored.Query<DescribedVisibility>().Count);
        Assert.Equal(1, restored.Query(new QueryDescription().WithDisabled<DescribedVisibility>()).Count);
        using ReadOnlyBuffer<DescribedElement> elements = restored.ReadBuffer<DescribedElement>(result);
        Assert.Equal(1, elements.Count);
        Assert.Equal(new DescribedElement { Value = 9, Target = target }, elements[0]);
    }

    [Fact]
    public void CallCodecsUseTheirStateAndFormatWhileOtherValuesUseGeneratedDescriptions()
    {
        using var world = new World();
        Entity entity = world.Create(new DescribedPosition { Value = 12 });
        world.SetSparse(entity, new DescribedLabel { Value = "default" });
        var format = new SerializationTypeKey(new Guid("abca3391-45df-4b49-af0e-08acd1219041"), "shifted-position", 1);
        ComponentCodec write = ComponentCodec.Create<DescribedPosition, ShiftedPositionCodec>(format, new(11));
        ComponentCodec read = ComponentCodec.Create<DescribedPosition, ShiftedPositionCodec>(format, new(4));
        using var bytes = new MemoryStream();

        WorldSerializer.WriteWorld(bytes, world, new(Purpose: SavePurpose.DurableSave, Codecs: [write]));
        bytes.Position = 0;
        using World restored = WorldSerializer.ReadWorld(bytes, new WorldLoadOptions(Codecs: [read]));

        Assert.Same(ComponentType.Of<DescribedPosition>(), write.Component);
        Assert.Equal(format, write.Format);
        Assert.Equal(19, restored.Read<DescribedPosition>(entity).Value);
        Assert.Equal("default", restored.ReadSparse<DescribedLabel>(entity).Value);
        Assert.Equal(1, restored.Query<DescribedPosition>().Count);
    }

    [Fact]
    public void BufferElementCodecsUseTheGeneratedFormatAndCallState()
    {
        using var world = new World();
        Entity entity = world.Create(new DescribedPosition { Value = 1 });
        world.AddBuffer<DescribedElement>(entity);
        using (DynamicBuffer<DescribedElement> elements = world.GetBuffer<DescribedElement>(entity))
            elements.Add(new DescribedElement { Value = 3 });
        ComponentCodec write = ComponentCodec.Create<DescribedElement, ShiftedElementCodec>(new(5));
        ComponentCodec read = ComponentCodec.Create<DescribedElement, ShiftedElementCodec>(new(2));
        using var bytes = new MemoryStream();

        WorldSerializer.WriteWorld(bytes, world, new(Purpose: SavePurpose.DurableSave, Codecs: [write]));
        bytes.Position = 0;
        using World restored = WorldSerializer.ReadWorld(bytes, new WorldLoadOptions(Codecs: [read]));

        Assert.Same(ComponentType.Of<Buffer<DescribedElement>>(), write.Component);
        Assert.Equal(new Guid("6ea337fd-1f64-46fd-bd4b-72f9fa4ef171"), write.Format.StableId);
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

    private readonly struct ParentValueCodec : IDurableComponentCodec<Parent<DeclaredHierarchy>>
    {
        public void Write(ref DataWriter writer, in Parent<DeclaredHierarchy> value) => writer.WriteEntity(value.Value);
        public void Read(ref DataReader reader, out Parent<DeclaredHierarchy> value) => value = new(reader.ReadEntity());
    }
}

public readonly struct DeclaredHierarchy : IHierarchyDomain;

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
