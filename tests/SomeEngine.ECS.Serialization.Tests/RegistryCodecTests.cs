using SomeEngine.ECS.Entities;
using SomeEngine.Serialization;

namespace SomeEngine.ECS.Serialization.Tests;

public sealed class RegistryCodecTests
{
    [Fact]
    public void RegistriesUseTheirOwnCodecData()
    {
        var key = new SerializationTypeKey(Guid.Parse("179ec61a-31af-433f-ab45-ae3b2df86711"), "position", 1);
        var firstReads = new List<float>();
        var secondReads = new List<float>();
        var first = new SerializationRegistry()
            .RegisterCodec<SerPosition, RecordingPositionCodec>(key, new(firstReads));
        var second = new SerializationRegistry()
            .RegisterCodec<SerPosition, RecordingPositionCodec>(key, new(secondReads));
        using var stream = new MemoryStream();
        WorldSerializer.WriteDurableComponent(stream, new SerPosition { X = 4, Y = 5 }, first);

        stream.Position = 0;
        Assert.Equal(4, WorldSerializer.ReadDurableComponent<SerPosition>(stream, first).X);
        stream.Position = 0;
        Assert.Equal(4, WorldSerializer.ReadDurableComponent<SerPosition>(stream, second).X);
        stream.Position = 0;
        Assert.Equal(4, WorldSerializer.ReadDurableComponent<SerPosition>(stream, second).X);
        Assert.Equal(new float[] { 4 }, firstReads);
        Assert.Equal(new float[] { 4, 4 }, secondReads);
    }

    [Fact]
    public void BufferCodecDataAppliesToDurableWorldReadsAndWrites()
    {
        var key = new SerializationTypeKey(Guid.Parse("6dc941a1-ff74-466c-9c9f-01c24a5bbd63"), "element", 1);
        var written = new List<int>();
        var read = new List<int>();
        var registry = new SerializationRegistry()
            .RegisterBufferCodec<SerElement, RecordingElementCodec>(key, new(written, read));
        using var source = new World();
        Entity entity = source.CreateEntity();
        source.AddBuffer<SerElement>(entity);
        source.ExecuteBufferWrite<SerElement>(entity, buffer =>
        {
            buffer.Add(new SerElement { Value = 7 });
            buffer.Add(new SerElement { Value = 11 });
        });
        using var stream = new MemoryStream();
        WorldSerializer.WriteDurableWorld(stream, source, registry);
        stream.Position = 0;
        using World loaded = WorldSerializer.ReadDurableWorld(stream, registry);
        var values = new List<int>();
        loaded.ExecuteBufferRead<SerElement>(entity, buffer =>
        {
            foreach (SerElement value in buffer.AsSpan())
                values.Add(value.Value);
        });
        Assert.Equal(new[] { 7, 11 }, values);
        Assert.Equal(values, written);
        Assert.Equal(values, read);
    }

    [Fact]
    public void CustomCodecFailuresKeepTheirCategoryAndCause()
    {
        var key = new SerializationTypeKey(Guid.Parse("0bc3c8af-3916-4c9c-8368-61676d81b27e"), "position", 1);
        var cause = new IOException();
        InvalidDataException expected = SerializationErrors.CreateDataException(
            SerializationError.UnknownType, "Codec-selected read failure.", cause);
        var writer = new SerializationRegistry().Register<SerPosition, SerPositionFullCodec>(key);
        var reader = new SerializationRegistry()
            .RegisterCodec<SerPosition, FailingPositionCodec>(key, new(expected));
        using var stream = new MemoryStream();
        WorldSerializer.WriteDurableComponent(stream, new SerPosition { X = 4, Y = 5 }, writer);
        stream.Position = 0;
        InvalidDataException actual = Assert.Throws<InvalidDataException>(() =>
            WorldSerializer.ReadDurableComponent<SerPosition>(stream, reader));
        Assert.Same(expected, actual);
        Assert.Same(cause, actual.InnerException);
        Assert.Equal(SerializationError.UnknownType, SerializationErrors.GetError(actual));
    }

    private readonly struct RecordingPositionCodec(List<float> reads) : IComponentCodec<SerPosition>
    {
        public void Write(ref DataWriter writer, in SerPosition value)
        {
            writer.WriteSingle(value.X);
            writer.WriteSingle(value.Y);
        }
        public void Read(ref DataReader reader, out SerPosition value)
        {
            value = new SerPosition { X = reader.ReadSingle(), Y = reader.ReadSingle() };
            reads.Add(value.X);
        }
    }

    private readonly struct RecordingElementCodec(List<int> writes, List<int> reads) : IComponentCodec<SerElement>
    {
        public void Write(ref DataWriter writer, in SerElement value)
        {
            writes.Add(value.Value);
            writer.WriteInt32(value.Value);
        }
        public void Read(ref DataReader reader, out SerElement value)
        {
            value = new SerElement { Value = reader.ReadInt32() };
            reads.Add(value.Value);
        }
    }

    private readonly struct FailingPositionCodec(InvalidDataException failure) : IComponentCodec<SerPosition>
    {
        public void Write(ref DataWriter writer, in SerPosition value) => throw new NotSupportedException();
        public void Read(ref DataReader reader, out SerPosition value) => throw failure;
    }
}
