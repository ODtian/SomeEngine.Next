using SomeEngine.ECS.Entities;
using SomeEngine.Serialization;

namespace SomeEngine.ECS.Serialization;

/// <summary>
/// Writes one ECS component payload directly to its admitted output stream. Fixed-width values
/// delegate to the shared <see cref="BinaryPrimitiveEncoding"/> contract; Entity/external-reference
/// helpers and ECS-v4 nullable framing remain component-domain policy. No encoded component backing
/// is retained by this adapter.
/// </summary>
public ref struct DataWriter
{
    private readonly BinaryWriter _writer;

    internal DataWriter(BinaryWriter writer)
    {
        _writer = writer;
    }

    public void WriteBoolean(bool value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(byte)];
        BinaryPrimitiveEncoding.WriteBoolean(bytes, value);
        _writer.Write(bytes);
    }

    public void WriteByte(byte value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(byte)];
        BinaryPrimitiveEncoding.WriteByte(bytes, value);
        _writer.Write(bytes);
    }

    public void WriteSByte(sbyte value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(sbyte)];
        BinaryPrimitiveEncoding.WriteSByte(bytes, value);
        _writer.Write(bytes);
    }

    public void WriteInt16(short value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(short)];
        BinaryPrimitiveEncoding.WriteInt16(bytes, value);
        _writer.Write(bytes);
    }

    public void WriteUInt16(ushort value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(ushort)];
        BinaryPrimitiveEncoding.WriteUInt16(bytes, value);
        _writer.Write(bytes);
    }

    public void WriteInt32(int value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(int)];
        BinaryPrimitiveEncoding.WriteInt32(bytes, value);
        _writer.Write(bytes);
    }

    public void WriteUInt32(uint value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(uint)];
        BinaryPrimitiveEncoding.WriteUInt32(bytes, value);
        _writer.Write(bytes);
    }

    public void WriteInt64(long value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(long)];
        BinaryPrimitiveEncoding.WriteInt64(bytes, value);
        _writer.Write(bytes);
    }

    public void WriteUInt64(ulong value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(ulong)];
        BinaryPrimitiveEncoding.WriteUInt64(bytes, value);
        _writer.Write(bytes);
    }

    public void WriteChar(char value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(char)];
        BinaryPrimitiveEncoding.WriteChar(bytes, value);
        _writer.Write(bytes);
    }

    public void WriteSingle(float value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(float)];
        BinaryPrimitiveEncoding.WriteSingle(bytes, value);
        _writer.Write(bytes);
    }

    public void WriteDouble(double value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(double)];
        BinaryPrimitiveEncoding.WriteDouble(bytes, value);
        _writer.Write(bytes);
    }
    public void WriteGuid(Guid value)
    {
        Span<byte> bytes = stackalloc byte[16];
        BinaryPrimitiveEncoding.WriteGuid(bytes, value);
        _writer.Write(bytes);
    }

    public void WriteString(string? value)
    {
        WriteBoolean(value is not null);
        if (value is not null)
            SerializationBinary.WriteString(_writer, value);
    }

    public void WriteEntity(Entity value)
    {
        WriteInt32(value.Index);
        WriteInt32(value.Generation);
    }

    public void WriteExternalReference(ExternalReferenceKey value) => WriteGuid(value.Value);

    internal void WriteRawBytes(ReadOnlySpan<byte> bytes) => _writer.Write(bytes);
}

