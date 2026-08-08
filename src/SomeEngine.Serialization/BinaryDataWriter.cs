using System.Buffers;
using System.Text;

namespace SomeEngine.Serialization;

/// <summary>
/// Optional fast path for binary writers that can consume an existing byte range directly.
/// Implementations must consume the supplied span before returning.
/// </summary>
internal interface IStreamingBinarySink : IBufferWriter<byte>
{
    void WriteDirect(ReadOnlySpan<byte> value);

    void WriteZeroesDirect(int count);
}

/// <summary>Little-endian writer over caller-owned final storage or a non-retaining streaming sink.</summary>
public ref struct BinaryDataWriter
{
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    private Span<byte> _destination;
    private readonly IStreamingBinarySink? _output;
    private int _written;

    public BinaryDataWriter(Span<byte> destination)
    {
        _destination = destination;
        _output = null;
        _written = 0;
    }

    internal BinaryDataWriter(IStreamingBinarySink output)
    {
        ArgumentNullException.ThrowIfNull(output);
        _destination = default;
        _output = output;
        _written = 0;
    }

    public int WrittenCount => _written;

    public int Remaining => _output is null
        ? _destination.Length - _written
        : int.MaxValue;

    public void WriteBoolean(bool value) => WriteByte(value ? (byte)1 : (byte)0);

    public void WriteByte(byte value)
    {
        if (_output is not null)
        {
            Span<byte> destination = _output.GetSpan(sizeof(byte));
            BinaryPrimitiveEncoding.WriteByte(destination, value);
            _output.Advance(sizeof(byte));
        }
        else
        {
            EnsureFixedCapacity(sizeof(byte));
            BinaryPrimitiveEncoding.WriteByte(_destination[_written..], value);
        }
        AdvanceCount(sizeof(byte));
    }

    public void WriteSByte(sbyte value) => WriteByte(unchecked((byte)value));

    public void WriteInt16(short value)
    {
        if (_output is not null)
        {
            Span<byte> destination = _output.GetSpan(sizeof(short));
            BinaryPrimitiveEncoding.WriteInt16(destination, value);
            _output.Advance(sizeof(short));
        }
        else
        {
            EnsureFixedCapacity(sizeof(short));
            BinaryPrimitiveEncoding.WriteInt16(_destination[_written..], value);
        }
        AdvanceCount(sizeof(short));
    }

    public void WriteUInt16(ushort value)
    {
        if (_output is not null)
        {
            Span<byte> destination = _output.GetSpan(sizeof(ushort));
            BinaryPrimitiveEncoding.WriteUInt16(destination, value);
            _output.Advance(sizeof(ushort));
        }
        else
        {
            EnsureFixedCapacity(sizeof(ushort));
            BinaryPrimitiveEncoding.WriteUInt16(_destination[_written..], value);
        }
        AdvanceCount(sizeof(ushort));
    }

    public void WriteInt32(int value)
    {
        if (_output is not null)
        {
            Span<byte> destination = _output.GetSpan(sizeof(int));
            BinaryPrimitiveEncoding.WriteInt32(destination, value);
            _output.Advance(sizeof(int));
        }
        else
        {
            EnsureFixedCapacity(sizeof(int));
            BinaryPrimitiveEncoding.WriteInt32(_destination[_written..], value);
        }
        AdvanceCount(sizeof(int));
    }

    public void WriteUInt32(uint value)
    {
        if (_output is not null)
        {
            Span<byte> destination = _output.GetSpan(sizeof(uint));
            BinaryPrimitiveEncoding.WriteUInt32(destination, value);
            _output.Advance(sizeof(uint));
        }
        else
        {
            EnsureFixedCapacity(sizeof(uint));
            BinaryPrimitiveEncoding.WriteUInt32(_destination[_written..], value);
        }
        AdvanceCount(sizeof(uint));
    }

    public void WriteInt64(long value)
    {
        if (_output is not null)
        {
            Span<byte> destination = _output.GetSpan(sizeof(long));
            BinaryPrimitiveEncoding.WriteInt64(destination, value);
            _output.Advance(sizeof(long));
        }
        else
        {
            EnsureFixedCapacity(sizeof(long));
            BinaryPrimitiveEncoding.WriteInt64(_destination[_written..], value);
        }
        AdvanceCount(sizeof(long));
    }

    public void WriteUInt64(ulong value)
    {
        if (_output is not null)
        {
            Span<byte> destination = _output.GetSpan(sizeof(ulong));
            BinaryPrimitiveEncoding.WriteUInt64(destination, value);
            _output.Advance(sizeof(ulong));
        }
        else
        {
            EnsureFixedCapacity(sizeof(ulong));
            BinaryPrimitiveEncoding.WriteUInt64(_destination[_written..], value);
        }
        AdvanceCount(sizeof(ulong));
    }

    public void WriteSingle(float value) => WriteInt32(BitConverter.SingleToInt32Bits(value));

    public void WriteDouble(double value) => WriteInt64(BitConverter.DoubleToInt64Bits(value));

    public void WriteChar(char value) => WriteUInt16(value);

    public void WriteGuid(Guid value)
    {
        const int size = 16;
        if (_output is not null)
        {
            Span<byte> destination = _output.GetSpan(size);
            BinaryPrimitiveEncoding.WriteGuid(destination, value);
            _output.Advance(size);
        }
        else
        {
            EnsureFixedCapacity(size);
            BinaryPrimitiveEncoding.WriteGuid(_destination[_written..], value);
        }
        AdvanceCount(size);
    }

    public void WriteString(string? value)
    {
        if (value is null)
        {
            WriteInt32(-1);
            return;
        }

        if (_output is not null)
        {
            WriteInt32(value.Length);
            if (value.Length == 0)
                return;

            Encoder encoder = StrictUtf8.GetEncoder();
            ReadOnlySpan<char> remaining = value.AsSpan();
            Span<byte> buffer = stackalloc byte[1024];
            while (!remaining.IsEmpty)
            {
                encoder.Convert(
                    remaining,
                    buffer,
                    flush: false,
                    out int charsUsed,
                    out int bytesUsed,
                    out _);
                if (charsUsed == 0)
                    throw new InvalidOperationException("UTF-8 encoder made no progress.");
                _output.WriteDirect(buffer[..bytesUsed]);
                AdvanceCount(bytesUsed);
                remaining = remaining[charsUsed..];
            }
            encoder.Convert(
                ReadOnlySpan<char>.Empty,
                buffer,
                flush: true,
                out _,
                out int finalBytes,
                out bool completed);
            if (!completed)
                throw new InvalidOperationException("UTF-8 encoder did not complete after consuming the string.");
            if (finalBytes != 0)
            {
                _output.WriteDirect(buffer[..finalBytes]);
                AdvanceCount(finalBytes);
            }
            return;
        }

        EnsureFixedCapacity(sizeof(int));
        int payloadOffset = checked(_written + sizeof(int));
        int fixedEncoded;
        try
        {
            fixedEncoded = StrictUtf8.GetBytes(value, _destination[payloadOffset..]);
        }
        catch (ArgumentException exception) when (exception is not EncoderFallbackException)
        {
            throw new BinaryDestinationTooSmallException(
                checked(_destination.Length + 1),
                _destination.Length,
                exception);
        }
        BinaryPrimitiveEncoding.WriteInt32(_destination[_written..], value.Length);
        AdvanceCount(checked(sizeof(int) + fixedEncoded));
    }

    public void WriteMemory(ReadOnlyMemory<byte>? value)
    {
        if (!value.HasValue)
        {
            WriteInt32(-1);
            return;
        }

        WriteLengthPrefixedBytes(value.Value.Span);
    }

    public void WriteLengthPrefixedBytes(scoped ReadOnlySpan<byte> value)
    {
        WriteInt32(value.Length);
        WriteBytes(value);
    }

    public void WriteBytes(scoped ReadOnlySpan<byte> value)
    {
        if (value.IsEmpty)
            return;

        if (_output is not null)
        {
            _output.WriteDirect(value);
            AdvanceCount(value.Length);
            return;
        }

        EnsureFixedCapacity(value.Length);
        value.CopyTo(_destination[_written..]);
        AdvanceCount(value.Length);
    }

    public void WriteZeroes(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (count == 0)
            return;

        if (_output is not null)
        {
            _output.WriteZeroesDirect(count);
            AdvanceCount(count);
            return;
        }

        EnsureFixedCapacity(count);
        _destination.Slice(_written, count).Clear();
        AdvanceCount(count);
    }

    private void EnsureFixedCapacity(int count)
    {
        if (count > _destination.Length - _written)
            throw new BinaryDestinationTooSmallException(checked(_written + count), _destination.Length);
    }

    private void AdvanceCount(int count) => _written = checked(_written + count);
}

internal sealed class BinaryDestinationTooSmallException : ArgumentException
{
    public BinaryDestinationTooSmallException(
        int requiredCapacity,
        int suppliedCapacity,
        Exception? innerException = null)
        : base(
            $"Destination is too small. Required at least {requiredCapacity} bytes, " +
            $"but only {suppliedCapacity} bytes were supplied.",
            "_destination",
            innerException)
    {
        RequiredCapacity = requiredCapacity;
    }

    public int RequiredCapacity { get; }
}
