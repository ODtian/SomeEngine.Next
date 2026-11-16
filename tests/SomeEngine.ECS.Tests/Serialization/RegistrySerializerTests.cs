using SomeEngine.ECS.Entities;
using SomeEngine.Serialization;

namespace SomeEngine.ECS.Serialization.Tests;

public sealed class RegistrySerializerTests
{
    [Fact]
    public void RegistriesUseTheirOwnSerializerData()
    {
        var key = new SavedTypeKey(Guid.Parse("179ec61a-31af-433f-ab45-ae3b2df86711"), "position", 1);
        var firstReads = new List<float>();
        var secondReads = new List<float>();
        var first = new SaveRegistry()
            .Register<SerPosition, RecordingPositionSerializer>(key, new(firstReads));
        var second = new SaveRegistry()
            .Register<SerPosition, RecordingPositionSerializer>(key, new(secondReads));
        using var stream = new MemoryStream();
        WorldSerializer.WriteComponent(stream, new SerPosition { X = 4, Y = 5 }, first, new SerializeOptions(Purpose: SavePurpose.DurableSave));

        stream.Position = 0;
        Assert.Equal(4, WorldSerializer.ReadComponent<SerPosition>(stream, first, new SerializationReadOptions(RequiredPurpose: SavePurpose.DurableSave)).X);
        stream.Position = 0;
        Assert.Equal(4, WorldSerializer.ReadComponent<SerPosition>(stream, second, new SerializationReadOptions(RequiredPurpose: SavePurpose.DurableSave)).X);
        stream.Position = 0;
        Assert.Equal(4, WorldSerializer.ReadComponent<SerPosition>(stream, second, new SerializationReadOptions(RequiredPurpose: SavePurpose.DurableSave)).X);
        Assert.Equal(new float[] { 4 }, firstReads);
        Assert.Equal(new float[] { 4, 4 }, secondReads);
    }

    [Fact]
    public void BufferSerializerDataAppliesToDurableWorldReadsAndWrites()
    {
        var key = new SavedTypeKey(Guid.Parse("6dc941a1-ff74-466c-9c9f-01c24a5bbd63"), "element", 1);
        var written = new List<int>();
        var read = new List<int>();
        var registry = new SaveRegistry()
            .RegisterBuffer<SerElement, RecordingElementSerializer>(key, new(written, read));
        using var source = new World();
        Entity entity = source.Create();
        source.AddBuffer<SerElement>(entity);
        {
            using DynamicBuffer<SerElement> buffer = source.GetBuffer<SerElement>(entity);

            buffer.Add(new SerElement { Value = 7 });
            buffer.Add(new SerElement { Value = 11 });
        }
        using var stream = new MemoryStream();
        new WorldSerializer(registry).Save(source, stream, new SerializeOptions(Purpose: SavePurpose.DurableSave));
        stream.Position = 0;
        using World loaded = WorldSerializer.ReadWorld(stream, registry, new WorldLoadOptions(RequiredPurpose: SavePurpose.DurableSave));
        var values = new List<int>();
        {
            using ReadOnlyBuffer<SerElement> buffer = loaded.ReadBuffer<SerElement>(entity);

            foreach (SerElement value in buffer.AsSpan())
                values.Add(value.Value);
        }
        Assert.Equal(new[] { 7, 11 }, values);
        Assert.Equal(values, written);
        Assert.Equal(values, read);
    }

    [Fact]
    public void CustomSerializerFailuresKeepTheirCategoryAndCause()
    {
        var key = new SavedTypeKey(Guid.Parse("0bc3c8af-3916-4c9c-8368-61676d81b27e"), "position", 1);
        var cause = new IOException();
        InvalidDataException expected = SerializationErrors.CreateDataException(
            SerializationError.UnknownType, "Serializer-selected read failure.", cause);
        var writer = new SaveRegistry().Register<SerPosition, SerPositionFullSerializer>(key);
        var reader = new SaveRegistry()
            .Register<SerPosition, FailingPositionSerializer>(key, new(expected));
        using var stream = new MemoryStream();
        WorldSerializer.WriteComponent(stream, new SerPosition { X = 4, Y = 5 }, writer, new SerializeOptions(Purpose: SavePurpose.DurableSave));
        stream.Position = 0;
        InvalidDataException actual = Assert.Throws<InvalidDataException>(() =>
            WorldSerializer.ReadComponent<SerPosition>(stream, reader, new SerializationReadOptions(RequiredPurpose: SavePurpose.DurableSave)));
        Assert.Same(expected, actual);
        Assert.Same(cause, actual.InnerException);
        Assert.Equal(SerializationError.UnknownType, SerializationErrors.GetError(actual));
    }

    private readonly struct RecordingPositionSerializer(List<float> reads) : IValueSerializer<SerPosition>
    {
        public void Write(ref SaveWriter writer, in SerPosition value)
        {
            writer.WriteSingle(value.X);
            writer.WriteSingle(value.Y);
        }
        public void Read(ref SaveReader reader, out SerPosition value)
        {
            value = new SerPosition { X = reader.ReadSingle(), Y = reader.ReadSingle() };
            reads.Add(value.X);
        }
    }

    private readonly struct RecordingElementSerializer(List<int> writes, List<int> reads) : IValueSerializer<SerElement>
    {
        public void Write(ref SaveWriter writer, in SerElement value)
        {
            writes.Add(value.Value);
            writer.WriteInt32(value.Value);
        }
        public void Read(ref SaveReader reader, out SerElement value)
        {
            value = new SerElement { Value = reader.ReadInt32() };
            reads.Add(value.Value);
        }
    }

    private readonly struct FailingPositionSerializer(InvalidDataException failure) : IValueSerializer<SerPosition>
    {
        public void Write(ref SaveWriter writer, in SerPosition value) => throw new NotSupportedException();
        public void Read(ref SaveReader reader, out SerPosition value) => throw failure;
    }
}
