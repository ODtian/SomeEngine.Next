using SomeEngine.ECS.Entities;

namespace SomeEngine.ECS.Serialization;

public ref struct DataWriter
{
    private readonly BinaryWriter _writer;

    internal DataWriter(BinaryWriter writer)
    {
        _writer = writer;
    }

    public void WriteBoolean(bool value) => _writer.Write(value);
    public void WriteByte(byte value) => _writer.Write(value);
    public void WriteInt32(int value) => _writer.Write(value);
    public void WriteUInt32(uint value) => _writer.Write(value);
    public void WriteInt64(long value) => _writer.Write(value);
    public void WriteSingle(float value) => _writer.Write(value);
    public void WriteDouble(double value) => _writer.Write(value);
    public void WriteGuid(Guid value)
    {
        Span<byte> bytes = stackalloc byte[16];
        value.TryWriteBytes(bytes);
        _writer.Write(bytes);
    }

    public void WriteString(string? value)
    {
        _writer.Write(value is not null);
        if (value is not null)
            _writer.Write(value);
    }

    public void WriteEntity(Entity value)
    {
        _writer.Write(value.Index);
        _writer.Write(value.Generation);
    }

    public void WriteExternalReference(ExternalReferenceKey value) => WriteGuid(value.Value);

    internal void WriteRawBytes(ReadOnlySpan<byte> bytes) => _writer.Write(bytes);
}

