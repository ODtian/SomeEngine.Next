using System.Buffers;
using System.Text;

namespace SomeEngine.Serialization;

public sealed record BinaryReadLimits
{
    public static BinaryReadLimits Default { get; } = new();

    public int MaxObjectDepth { get; init; } = 128;
    public int MaxCollectionCount { get; init; } = 16_000_000;
    public int MaxStringBytes { get; init; } = 16 * 1024 * 1024;
    public long MaxTotalStringBytes { get; init; } = 256L * 1024 * 1024;
    public int MaxBytePayloadBytes { get; init; } = 1024 * 1024 * 1024;
    public long MaxTotalDecodedBytes { get; init; } = 8L * 1024 * 1024 * 1024;
    public long MaxAllocationBytes { get; init; } = 2L * 1024 * 1024 * 1024;
    public int MaxRootBytes { get; init; } = 64 * 1024 * 1024;
    public int MaxTypeCatalogEntries { get; init; } = 65_536;
    public int MaxTypeCatalogBytes { get; init; } = 4 * 1024 * 1024;
    public int MaxChunkCount { get; init; } = 4_000_000;
    public long MaxStoredChunkBytes { get; init; } = 2L * 1024 * 1024 * 1024;
    public long MaxDecodedChunkBytes { get; init; } = 2L * 1024 * 1024 * 1024;
    public int MaxCompressionRatio { get; init; } = 256;
}

internal sealed class BinaryReadBudget
{
    private long _stringBytes;
    private long _decodedBytes;
    private long _allocationBytes;
    private int _depth;

    internal BinaryReadBudget(BinaryReadLimits? limits)
    {
        Limits = limits ?? BinaryReadLimits.Default;
    }

    internal BinaryReadLimits Limits { get; }

    internal void EnterObject()
    {
        _depth = checked(_depth + 1);
        if (_depth > Limits.MaxObjectDepth)
            throw SerializationErrors.CreateDataException(
                SerializationError.LimitExceeded,
                $"Binary object depth exceeds configured limit {Limits.MaxObjectDepth}.");
    }

    internal void ExitObject()
    {
        if (_depth <= 0)
            throw new InvalidOperationException("Binary object-depth accounting is unbalanced.");
        _depth--;
    }

    internal int CollectionCount(
        int value,
        string description,
        int elementAllocationBytes,
        int fixedAllocationBytes)
    {
        if (value < 0)
            throw new InvalidDataException($"Negative {description} count.");
        if (value > Limits.MaxCollectionCount)
            throw SerializationErrors.CreateDataException(
                SerializationError.LimitExceeded,
                $"{description} count {value} exceeds {Limits.MaxCollectionCount}.");

        if (elementAllocationBytes < 0)
            throw new ArgumentOutOfRangeException(nameof(elementAllocationBytes));
        if (fixedAllocationBytes < 0)
            throw new ArgumentOutOfRangeException(nameof(fixedAllocationBytes));
        ReserveAllocation(
            checked((long)fixedAllocationBytes + (long)value * elementAllocationBytes),
            description);
        return value;
    }

    internal int StringCodeUnitCount(int value)
    {
        if (value < 0)
            throw new InvalidDataException("Negative UTF-16 string code-unit count.");
        if (value > Limits.MaxStringBytes)
            throw SerializationErrors.CreateDataException(
                SerializationError.LimitExceeded,
                $"UTF-16 string code-unit count {value} exceeds {Limits.MaxStringBytes}.");
        ReserveAllocation(checked(24L + (long)value * sizeof(char)), "UTF-16 string");
        return value;
    }

    internal void StringBytes(int value)
    {
        if (value > Limits.MaxStringBytes)
            throw SerializationErrors.CreateDataException(
                SerializationError.LimitExceeded,
                $"UTF-8 string length {value} exceeds {Limits.MaxStringBytes}.");
        Consume(ref _stringBytes, value, Limits.MaxTotalStringBytes, "UTF-8 string bytes");
    }

    internal int BytePayloadLength(int value, bool allocate)
    {
        if (value < 0)
            throw new InvalidDataException("Negative byte payload length.");
        if (value > Limits.MaxBytePayloadBytes)
            throw SerializationErrors.CreateDataException(
                SerializationError.LimitExceeded,
                $"Byte payload length {value} exceeds {Limits.MaxBytePayloadBytes}.");
        Consume(ref _decodedBytes, value, Limits.MaxTotalDecodedBytes, "decoded bytes");
        if (allocate)
            ReserveAllocation(checked(24L + value), "byte payload");
        return value;
    }

    internal void ReserveAllocation(long bytes, string description)
    {
        if (bytes < 0)
            throw new InvalidDataException($"Negative allocation estimate for {description}.");
        Consume(ref _allocationBytes, bytes, Limits.MaxAllocationBytes, "allocation bytes");
    }

    private static void Consume(ref long total, long amount, long maximum, string description)
    {
        try
        {
            total = checked(total + amount);
        }
        catch (OverflowException exception)
        {
            throw SerializationErrors.CreateDataException(
                SerializationError.LimitExceeded,
                $"{description} budget overflowed.", exception);
        }

        if (total > maximum)
            throw SerializationErrors.CreateDataException(
                SerializationError.LimitExceeded,
                $"Total {description} {total} exceeds configured limit {maximum}.");
    }
}

/// <summary>Bounds-checked little-endian reader over immutable continuous or segmented input.</summary>
public ref struct BinaryDataReader
{
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    private readonly ReadOnlySpan<byte> _source;
    private readonly BinaryReadBudget _budget;
    private SequenceReader<byte> _sequence;
    private readonly bool _segmented;
    private readonly int _length;
    private int _position;

    public BinaryDataReader(ReadOnlySpan<byte> source, BinaryReadLimits? limits = null)
        : this(source, new BinaryReadBudget(limits))
    {
    }

    private BinaryDataReader(ReadOnlySpan<byte> source, BinaryReadBudget budget)
    {
        _source = source;
        _budget = budget;
        _sequence = default;
        _segmented = false;
        _length = source.Length;
        _position = 0;
    }

    public BinaryDataReader(ReadOnlySequence<byte> source, BinaryReadLimits? limits = null)
        : this(source, new BinaryReadBudget(limits))
    {
    }

    private BinaryDataReader(ReadOnlySequence<byte> source, BinaryReadBudget budget)
    {
        // A single encoded value retains the existing Int32 addressing limit, independently
        // of document offsets and the total size of a source containing many values.
        ArgumentOutOfRangeException.ThrowIfGreaterThan(source.Length, int.MaxValue);
        _budget = budget;
        _length = (int)source.Length;
        _segmented = !source.IsSingleSegment;
        _source = source.IsSingleSegment ? source.FirstSpan : default;
        _sequence = _segmented ? new SequenceReader<byte>(source) : default;
        _position = 0;
    }

    public int Position => _segmented ? (int)_sequence.Consumed : _position;
    public int Remaining => _length - Position;
    public bool End => Remaining == 0;

    public bool ReadBoolean()
    {
        int position = Position;
        try
        {
            return BinaryPrimitiveEncoding.ReadBoolean(ReadBytes(sizeof(byte)));
        }
        catch (InvalidDataException exception) when (exception.Message.StartsWith("Invalid Boolean value", StringComparison.Ordinal))
        {
            throw new InvalidDataException($"{exception.Message.TrimEnd('.')} at byte {position}.", exception);
        }
    }

    public byte ReadByte() => BinaryPrimitiveEncoding.ReadByte(ReadBytes(sizeof(byte)));
    public sbyte ReadSByte() => BinaryPrimitiveEncoding.ReadSByte(ReadBytes(sizeof(sbyte)));
    public short ReadInt16()
    {
        if (!_segmented) return BinaryPrimitiveEncoding.ReadInt16(ReadBytes(sizeof(short)));
        RequireBytes(sizeof(short));
        _sequence.TryReadLittleEndian(out short value);
        return value;
    }
    public ushort ReadUInt16() => unchecked((ushort)ReadInt16());
    public int ReadInt32()
    {
        if (!_segmented) return BinaryPrimitiveEncoding.ReadInt32(ReadBytes(sizeof(int)));
        RequireBytes(sizeof(int));
        _sequence.TryReadLittleEndian(out int value);
        return value;
    }
    public uint ReadUInt32() => unchecked((uint)ReadInt32());
    public long ReadInt64()
    {
        if (!_segmented) return BinaryPrimitiveEncoding.ReadInt64(ReadBytes(sizeof(long)));
        RequireBytes(sizeof(long));
        _sequence.TryReadLittleEndian(out long value);
        return value;
    }
    public ulong ReadUInt64() => unchecked((ulong)ReadInt64());
    public float ReadSingle() => BitConverter.Int32BitsToSingle(ReadInt32());
    public double ReadDouble() => BitConverter.Int64BitsToDouble(ReadInt64());
    public char ReadChar() => (char)ReadUInt16();

    public Guid ReadGuid()
    {
        if (!_segmented || _sequence.UnreadSpan.Length >= 16)
            return BinaryPrimitiveEncoding.ReadGuid(ReadBytes(16));
        Span<byte> bytes = stackalloc byte[16];
        ReadInto(bytes);
        return BinaryPrimitiveEncoding.ReadGuid(bytes);
    }

    public unsafe string? ReadString()
    {
        int codeUnitCount = ReadInt32();
        if (codeUnitCount == -1)
            return null;
        _budget.StringCodeUnitCount(codeUnitCount);
        if (codeUnitCount == 0)
            return string.Empty;

        string value = new('\0', codeUnitCount);
        int bytesUsed = 0;
        int charsUsed = 0;
        SequenceReader<byte> input = _sequence;
        Span<byte> utf8 = stackalloc byte[4];
        fixed (char* destination = value)
        {
            while (charsUsed < codeUnitCount)
            {
                ReadOnlySpan<byte> next = _segmented ? input.UnreadSpan : _source[checked(_position + bytesUsed)..];
                OperationStatus status = Rune.DecodeFromUtf8(next,
                    out Rune rune,
                    out int consumed);
                if (_segmented && status == OperationStatus.NeedMoreData && input.Remaining > next.Length)
                {
                    int count = (int)Math.Min(4, input.Remaining);
                    input.TryCopyTo(utf8[..count]);
                    status = Rune.DecodeFromUtf8(utf8[..count], out rune, out consumed);
                }
                if (status == OperationStatus.NeedMoreData)
                    throw new InvalidDataException("Binary string ended inside a UTF-8 sequence.");
                if (status != OperationStatus.Done)
                    throw new InvalidDataException("Binary string contains malformed UTF-8.");

                int runeChars = rune.Utf16SequenceLength;
                if (runeChars > codeUnitCount - charsUsed)
                {
                    throw new InvalidDataException(
                        "Binary string exceeds its declared UTF-16 code-unit count.");
                }

                int encoded = rune.EncodeToUtf16(
                    new Span<char>(destination + charsUsed, codeUnitCount - charsUsed));
                if (encoded != runeChars)
                    throw new InvalidOperationException("Rune UTF-16 length changed while decoding.");
                bytesUsed = checked(bytesUsed + consumed);
                charsUsed += runeChars;
                if (_segmented) input.Advance(consumed);
            }
        }

        _budget.StringBytes(bytesUsed);
        if (_segmented) _sequence = input;
        else _position = checked(_position + bytesUsed);
        return value;
    }

    public Memory<byte>? ReadMemory()
    {
        int length = ReadInt32();
        if (length == -1)
            return null;
        _budget.BytePayloadLength(length, allocate: true);
        RequireBytes(length);
        byte[] result = GC.AllocateUninitializedArray<byte>(length);
        ReadInto(result);
        return result;
    }

    public byte[] ReadByteArray()
    {
        int length = ReadInt32();
        _budget.BytePayloadLength(length, allocate: true);
        RequireBytes(length);
        byte[] result = GC.AllocateUninitializedArray<byte>(length);
        ReadInto(result);
        return result;
    }

    public ReadOnlySpan<byte> ReadLengthPrefixedSpan()
    {
        int length = ReadInt32();
        _budget.BytePayloadLength(length, allocate: false);
        return ReadBytes(length);
    }

    /// <summary>
    /// Borrows a contiguous range. If the requested range spans segments, copies only that
    /// range to a GC array and charges the allocation limit. Ordinary scalar reads never take this copy path.
    /// </summary>
    public ReadOnlySpan<byte> ReadBytes(int length)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        RequireBytes(length);
        if (_segmented)
        {
            ReadOnlySpan<byte> bytes = _sequence.UnreadSpan;
            if (length <= bytes.Length)
            {
                _sequence.Advance(length);
                return bytes[..length];
            }
            _budget.ReserveAllocation(24L + length, "contiguous byte range");
            byte[] copy = GC.AllocateUninitializedArray<byte>(length);
            ReadInto(copy);
            return copy;
        }

        ReadOnlySpan<byte> result = _source.Slice(_position, length);
        _position += length;
        return result;
    }

    private void ReadInto(scoped Span<byte> destination)
    {
        RequireBytes(destination.Length);
        if (_segmented)
        {
            _sequence.TryCopyTo(destination);
            _sequence.Advance(destination.Length);
        }
        else
        {
            _source.Slice(_position, destination.Length).CopyTo(destination);
            _position += destination.Length;
        }
    }

    private void RequireBytes(int length)
    {
        if (length > Remaining)
            throw new InvalidDataException($"Truncated binary payload at byte {Position}: requested {length} bytes, {Remaining} remain.");
    }

    /// <summary>
    /// Reads a collection count using a conservative allocation estimate suitable for unknown
    /// collection implementations. Generated codecs use the explicit overload below.
    /// </summary>
    public int ReadCollectionCount(string description = "collection")
        => _budget.CollectionCount(
            ReadInt32(),
            description,
            elementAllocationBytes: 32,
            fixedAllocationBytes: 64);

    public int ReadCollectionCount(
        string description,
        int elementAllocationBytes,
        int fixedAllocationBytes)
        => _budget.CollectionCount(
            ReadInt32(),
            description,
            elementAllocationBytes,
            fixedAllocationBytes);

    /// <summary>Charges a generated or custom materializer before it allocates an object.</summary>
    public void ReserveAllocation(long bytes, string description)
        => _budget.ReserveAllocation(bytes, description);

    public int ReadPayloadLength(string description = "field payload")
    {
        int value = ReadInt32();
        if (value < 0)
            throw new InvalidDataException($"Negative {description} length.");
        if (value > Remaining)
            throw new InvalidDataException($"{description} length {value} exceeds remaining input {Remaining}.");
        return value;
    }

    public BinaryDataReader ReadSubReader(int length)
    {
        if (!_segmented) return new(ReadBytes(length), _budget);
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        RequireBytes(length);
        ReadOnlySequence<byte> bytes = _sequence.Sequence.Slice(_sequence.Position, length);
        _sequence.Advance(length);
        return new(bytes, _budget);
    }

    public void EnterObject() => _budget.EnterObject();
    public void ExitObject() => _budget.ExitObject();

    public void EnsureFullyConsumed(string? description = null)
    {
        if (End)
            return;

        throw new InvalidDataException(
            $"Binary {description ?? "payload"} has {Remaining} unexpected trailing bytes.");
    }
}
