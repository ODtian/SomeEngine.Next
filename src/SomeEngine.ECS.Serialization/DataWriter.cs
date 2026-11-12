using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using SomeEngine.ECS.Entities;
using SomeEngine.Serialization;

namespace SomeEngine.ECS.Serialization;

public ref struct DataWriter
{
    private static readonly Encoding Utf8 = new UTF8Encoding(false, true);
    private readonly IBufferWriter<byte> _output;
    private readonly ISet<ExternalReferenceKey>? _references;
    private Span<byte> _bytes;
    private int _offset;
    private long _committed;
    private long _byteLimit;
    private int _spanLength;

    public DataWriter(IBufferWriter<byte> output, ISet<ExternalReferenceKey>? references = null)
    {
        _output = output;
        _references = references;
        _bytes = output.GetSpan(4096);
        _offset = 0;
        _committed = 0;
        _byteLimit = long.MaxValue;
        _spanLength = 0;
    }
    public DataWriter(Stream output, ISet<ExternalReferenceKey>? references = null)
        : this(new StreamDataOutput(output), references) { }
    public long Position => _committed + _offset;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private Span<byte> Take(int count)
    {
        if (count > _bytes.Length - _offset) Next(count);
        Span<byte> bytes = _bytes.Slice(_offset, count);
        _offset += count;
        return bytes;
    }
    private void Next(int count)
    {
        RequireBytes(count);
        _output.Advance(_offset);
        _committed += _offset;
        _offset = 0;
        _bytes = GetBytes(Math.Max(4096, count));
    }

    private Span<byte> GetBytes(int count)
    {
        Span<byte> bytes = _output.GetSpan(count);
        if (_spanLength == 0) return bytes;
        _spanLength = bytes.Length;
        return bytes[..(int)Math.Min(bytes.Length, _byteLimit - _committed)];
    }

    private void RequireBytes(int count)
    {
        if (_byteLimit != long.MaxValue && count > _byteLimit - Position)
            throw new InvalidOperationException("Topology payload bytes exceed the configured write limit.");
    }

    internal void LimitBytes(long remaining)
    {
        long position = Position;
        _byteLimit = position + Math.Min(remaining, long.MaxValue - position);
        _spanLength = _bytes.Length;
        _bytes = _bytes[..(int)Math.Min(_bytes.Length, _byteLimit - _committed)];
    }

    internal void EndByteLimit()
    {
        _bytes = MemoryMarshal.CreateSpan(ref MemoryMarshal.GetReference(_bytes), _spanLength);
        _byteLimit = long.MaxValue;
        _spanLength = 0;
    }
    public void Flush()
    {
        _output.Advance(_offset);
        _committed += _offset;
        _offset = 0;
        _bytes = default;
    }
    public void WriteBoolean(bool value) => BinaryPrimitiveEncoding.WriteBoolean(Take(1), value);
    public void WriteByte(byte value) => BinaryPrimitiveEncoding.WriteByte(Take(1), value);
    public void WriteSByte(sbyte value) => BinaryPrimitiveEncoding.WriteSByte(Take(1), value);
    public void WriteInt16(short value) => BinaryPrimitiveEncoding.WriteInt16(Take(2), value);
    public void WriteUInt16(ushort value) => BinaryPrimitiveEncoding.WriteUInt16(Take(2), value);
    public void WriteInt32(int value) => BinaryPrimitiveEncoding.WriteInt32(Take(4), value);
    public void WriteUInt32(uint value) => BinaryPrimitiveEncoding.WriteUInt32(Take(4), value);
    public void WriteInt64(long value) => BinaryPrimitiveEncoding.WriteInt64(Take(8), value);
    public void WriteUInt64(ulong value) => BinaryPrimitiveEncoding.WriteUInt64(Take(8), value);
    public void WriteSingle(float value) => BinaryPrimitiveEncoding.WriteSingle(Take(4), value);
    public void WriteDouble(double value) => BinaryPrimitiveEncoding.WriteDouble(Take(8), value);
    public void WriteChar(char value) => BinaryPrimitiveEncoding.WriteChar(Take(2), value);
    public void WriteGuid(Guid value) => BinaryPrimitiveEncoding.WriteGuid(Take(16), value);
    public void WriteExternalReference(ExternalReferenceKey value)
    {
        WriteGuid(value.Value);
        if (value.Value != Guid.Empty) _references?.Add(value);
    }
    public void WriteEntity(Entity value)
    {
        if (BitConverter.IsLittleEndian) WriteUInt64(Unsafe.BitCast<Entity, ulong>(value));
        else { WriteInt32(value.Id); WriteInt32(value.Generation); }
    }

    internal void WriteEntities(scoped ReadOnlySpan<Entity> values)
    {
        if (BitConverter.IsLittleEndian) WriteRawBytes(MemoryMarshal.AsBytes(values));
        else foreach (Entity value in values) WriteEntity(value);
    }

    public void WriteString(string? value)
    {
        WriteBoolean(value is not null);
        if (value is not null) WriteText(value);
    }
    internal void WriteText(string value)
    {
        WriteInt32(value.Length);
        int maximum = Utf8.GetMaxByteCount(value.Length);
        if (maximum > _bytes.Length - _offset)
            Next(_byteLimit == long.MaxValue ? maximum : Utf8.GetByteCount(value));
        _offset += Utf8.GetBytes(value, _bytes[_offset..]);
    }
    internal void WriteRawBytes(scoped ReadOnlySpan<byte> bytes)
    {
        RequireBytes(bytes.Length);
        if (_output is StreamDataOutput stream && bytes.Length >= 4096)
        {
            _output.Advance(_offset);
            _committed += _offset + bytes.Length;
            _offset = 0;
            stream.Write(bytes);
            _bytes = GetBytes(4096);
        }
        else bytes.CopyTo(Take(bytes.Length));
    }
}

internal sealed class StreamDataOutput(Stream stream) : IBufferWriter<byte>
{
    private byte[] _bytes = new byte[4096];
    public void Advance(int count) { if (count != 0) stream.Write(_bytes.AsSpan(0, count)); }
    public Memory<byte> GetMemory(int sizeHint = 0)
    { if (sizeHint > _bytes.Length) _bytes = new byte[sizeHint]; return _bytes; }
    public Span<byte> GetSpan(int sizeHint = 0) => GetMemory(sizeHint).Span;
    internal void Write(ReadOnlySpan<byte> bytes) => stream.Write(bytes);
}
