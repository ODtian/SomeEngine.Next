using System.Buffers;
using System.Text;
using SomeEngine.Serialization.IO;

namespace SomeEngine.Serialization;

/// <summary>
/// Compile-time view contract implemented by generated codecs. The associated view type remains
/// strongly typed without reflection or runtime type discovery.
/// </summary>
public interface IBinaryViewContract<TSelf, TView>
    where TSelf : IBinaryContract<TSelf>, IBinaryViewContract<TSelf, TView>
    where TView : struct
{
    static abstract void ValidateCanonical(ReadOnlySpan<byte> source, BinaryReadLimits? limits = null);

    static abstract TView CreateView(
        BinaryContractViewOwner owner,
        BinaryReadLimits? limits = null);
}

/// <summary>
/// Opt-in validation hook for hand-written contracts nested inside generated contracts. The hook
/// must consume exactly one value from the supplied reader and must not materialize an object.
/// </summary>
public interface IBinaryCustomViewContract<TSelf>
    where TSelf : IBinaryContract<TSelf>, IBinaryCustomViewContract<TSelf>
{
    static abstract void ValidateView(ref BinaryViewReader reader);
}

/// <summary>
/// Explicit lifetime state for a long-lived generated binary view. It retains the original range
/// lease or memory owner; it never snapshots the bytes into a façade array.
/// </summary>
public sealed class BinaryContractViewOwner : IDisposable
{
    private RangeLease? _lease;
    private int _disposed;

    private BinaryContractViewOwner(RangeLease lease)
    {
        _lease = lease;
    }

    public int Length => Memory.Length;

    public ReadOnlyMemory<byte> Memory
    {
        get
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            RangeLease? lease = Volatile.Read(ref _lease);
            ObjectDisposedException.ThrowIf(lease is null, this);
            return lease.Memory;
        }
    }

    public ReadOnlySpan<byte> Span => Memory.Span;

    /// <summary>Creates an owner over caller-owned immutable memory without copying it.</summary>
    public static BinaryContractViewOwner Borrow(ReadOnlyMemory<byte> memory)
        => new(RangeLease.Borrow(memory));

    /// <summary>Takes ownership of an existing memory owner without copying it.</summary>
    public static BinaryContractViewOwner Own(IMemoryOwner<byte> owner, int length)
        => new(RangeLease.Own(owner, length));

    internal static BinaryContractViewOwner Take(RangeLease lease)
    {
        ArgumentNullException.ThrowIfNull(lease);
        return new BinaryContractViewOwner(lease);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        Interlocked.Exchange(ref _lease, null)?.Dispose();
    }
}

/// <summary>
/// Allocation-free bounded reader used by generated validators and lazy field accessors. Unlike
/// <see cref="BinaryDataReader"/>, strings and blobs remain encoded spans and no object graph is
/// published while validation is in progress.
/// </summary>
public ref struct BinaryViewReader
{
    private readonly ReadOnlySpan<byte> _source;
    private readonly BinaryReadLimits _limits;
    private readonly int _baseDepth;
    private int _position;
    private int _depth;
    private long _stringBytes;
    private long _decodedBytes;
    private long _allocationBytes;

    public BinaryViewReader(ReadOnlySpan<byte> source, BinaryReadLimits? limits = null)
        : this(source, limits ?? BinaryReadLimits.Default, baseDepth: 0)
    {
    }

    private BinaryViewReader(ReadOnlySpan<byte> source, BinaryReadLimits limits, int baseDepth)
    {
        _source = source;
        _limits = limits;
        _baseDepth = baseDepth;
        _position = 0;
        _depth = baseDepth;
        _stringBytes = 0;
        _decodedBytes = 0;
        _allocationBytes = 0;
    }

    public int Position => _position;
    public int Remaining => _source.Length - _position;
    public bool End => _position == _source.Length;

    public bool ReadBoolean()
    {
        int position = _position;
        try
        {
            return BinaryPrimitiveEncoding.ReadBoolean(ReadBytes(sizeof(byte)));
        }
        catch (InvalidDataException exception) when (
            exception.Message.StartsWith("Invalid Boolean value", StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"{exception.Message.TrimEnd('.')} at byte {position}.",
                exception);
        }
    }

    public byte ReadByte() => BinaryPrimitiveEncoding.ReadByte(ReadBytes(sizeof(byte)));
    public sbyte ReadSByte() => BinaryPrimitiveEncoding.ReadSByte(ReadBytes(sizeof(sbyte)));
    public short ReadInt16() => BinaryPrimitiveEncoding.ReadInt16(ReadBytes(sizeof(short)));
    public ushort ReadUInt16() => BinaryPrimitiveEncoding.ReadUInt16(ReadBytes(sizeof(ushort)));
    public int ReadInt32() => BinaryPrimitiveEncoding.ReadInt32(ReadBytes(sizeof(int)));
    public uint ReadUInt32() => BinaryPrimitiveEncoding.ReadUInt32(ReadBytes(sizeof(uint)));
    public long ReadInt64() => BinaryPrimitiveEncoding.ReadInt64(ReadBytes(sizeof(long)));
    public ulong ReadUInt64() => BinaryPrimitiveEncoding.ReadUInt64(ReadBytes(sizeof(ulong)));
    public float ReadSingle() => BinaryPrimitiveEncoding.ReadSingle(ReadBytes(sizeof(float)));
    public double ReadDouble() => BinaryPrimitiveEncoding.ReadDouble(ReadBytes(sizeof(double)));
    public char ReadChar() => BinaryPrimitiveEncoding.ReadChar(ReadBytes(sizeof(char)));
    public Guid ReadGuid() => BinaryPrimitiveEncoding.ReadGuid(ReadBytes(16));

    public ReadOnlySpan<byte> ReadStringBytes(out bool isNull)
    {
        int codeUnitCount = ReadInt32();
        if (codeUnitCount == -1)
        {
            isNull = true;
            return default;
        }
        if (codeUnitCount < 0)
            throw new InvalidDataException("Negative UTF-16 string code-unit count.");
        if (codeUnitCount > _limits.MaxStringBytes)
        {
            throw new InvalidDataException(
                $"UTF-16 string code-unit count {codeUnitCount} exceeds {_limits.MaxStringBytes}.");
        }
        int byteLength = Utf8ByteLengthForCodeUnits(_source[_position..], codeUnitCount);
        if (byteLength > _limits.MaxStringBytes)
            throw new InvalidDataException($"UTF-8 string length {byteLength} exceeds {_limits.MaxStringBytes}.");
        Consume(ref _stringBytes, byteLength, _limits.MaxTotalStringBytes, "UTF-8 string bytes");
        ReadOnlySpan<byte> bytes = ReadBytes(byteLength);
        isNull = false;
        return bytes;
    }

    private static int Utf8ByteLengthForCodeUnits(ReadOnlySpan<byte> source, int codeUnitCount)
    {
        if (codeUnitCount == 0)
            return 0;

        int totalBytes = 0;
        int remainingCodeUnits = codeUnitCount;
        while (remainingCodeUnits > 0)
        {
            OperationStatus status = Rune.DecodeFromUtf8(
                source[totalBytes..],
                out Rune rune,
                out int bytesUsed);
            if (status == OperationStatus.NeedMoreData)
                throw new InvalidDataException("Binary string ended inside a UTF-8 sequence.");
            if (status != OperationStatus.Done)
                throw new InvalidDataException("Binary string contains malformed UTF-8.");
            if (rune.Utf16SequenceLength > remainingCodeUnits)
                throw new InvalidDataException("Binary string exceeds its declared UTF-16 code-unit count.");
            totalBytes = checked(totalBytes + bytesUsed);
            remainingCodeUnits -= rune.Utf16SequenceLength;
        }
        return totalBytes;
    }

    public ReadOnlySpan<byte> ReadMemoryBytes(out bool isNull)
    {
        int length = ReadInt32();
        if (length == -1)
        {
            isNull = true;
            return default;
        }
        ValidateBytePayloadLength(length);
        isNull = false;
        return ReadBytes(length);
    }

    public ReadOnlySpan<byte> ReadByteArrayBytes()
    {
        int length = ReadInt32();
        ValidateBytePayloadLength(length);
        return ReadBytes(length);
    }

    public int ReadCollectionCount(string description = "collection")
    {
        int count = ReadInt32();
        if (count < 0)
            throw new InvalidDataException($"Negative {description} count.");
        if (count > _limits.MaxCollectionCount)
            throw new InvalidDataException($"{description} count {count} exceeds {_limits.MaxCollectionCount}.");
        return count;
    }

    public int ReadCollectionCount(
        string description,
        int elementAllocationBytes,
        int fixedAllocationBytes)
    {
        int count = ReadCollectionCount(description);
        if (elementAllocationBytes < 0)
            throw new ArgumentOutOfRangeException(nameof(elementAllocationBytes));
        if (fixedAllocationBytes < 0)
            throw new ArgumentOutOfRangeException(nameof(fixedAllocationBytes));
        ReserveAllocation(
            checked((long)fixedAllocationBytes + (long)count * elementAllocationBytes),
            description);
        return count;
    }

    public void ReserveAllocation(long bytes, string description)
    {
        if (bytes < 0)
            throw new InvalidDataException($"Negative allocation estimate for {description}.");
        Consume(ref _allocationBytes, bytes, _limits.MaxAllocationBytes, "allocation bytes");
    }

    public int ReadPayloadLength(string description = "field payload")
    {
        int value = ReadInt32();
        if (value < 0)
            throw new InvalidDataException($"Negative {description} length.");
        if (value > Remaining)
            throw new InvalidDataException(
                $"{description} length {value} exceeds remaining input {Remaining}.");
        return value;
    }

    public ReadOnlySpan<byte> ReadBytes(int length)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        if (length > Remaining)
        {
            throw new InvalidDataException(
                $"Truncated binary payload at byte {_position}: requested {length} bytes, {Remaining} remain.");
        }

        ReadOnlySpan<byte> result = _source.Slice(_position, length);
        _position += length;
        return result;
    }

    public ReadOnlySpan<byte> Slice(int offset, int length)
    {
        if ((uint)offset > (uint)_source.Length || (uint)length > (uint)(_source.Length - offset))
            throw new ArgumentOutOfRangeException(nameof(offset));
        return _source.Slice(offset, length);
    }

    public BinaryViewReader ReadSubReader(int length)
        => new(ReadBytes(length), _limits, _depth);

    public void MergeValidatedSubReader(ref BinaryViewReader child)
    {
        child.EnsureFullyConsumed("field payload");
        if (child._depth != child._baseDepth)
            throw new InvalidOperationException("Binary view child-reader depth accounting is unbalanced.");
        Consume(ref _stringBytes, child._stringBytes, _limits.MaxTotalStringBytes, "UTF-8 string bytes");
        Consume(ref _decodedBytes, child._decodedBytes, _limits.MaxTotalDecodedBytes, "decoded bytes");
        Consume(ref _allocationBytes, child._allocationBytes, _limits.MaxAllocationBytes, "allocation bytes");
    }

    public void EnterObject()
    {
        _depth = checked(_depth + 1);
        if (_depth > _limits.MaxObjectDepth)
            throw new InvalidDataException(
                $"Binary object depth exceeds configured limit {_limits.MaxObjectDepth}.");
    }

    public void ExitObject()
    {
        if (_depth <= _baseDepth)
            throw new InvalidOperationException("Binary view object-depth accounting is unbalanced.");
        _depth--;
    }

    public void EnsureFullyConsumed(string? description = null)
    {
        if (!End)
        {
            throw new InvalidDataException(
                $"Binary {description ?? "payload"} has {Remaining} unexpected trailing bytes.");
        }
        if (_depth != _baseDepth)
            throw new InvalidOperationException("Binary view object-depth accounting is unbalanced.");
    }

    /// <summary>Compares already validated UTF-8 using the same UTF-16 ordinal order as StringComparer.Ordinal.</summary>
    public static int CompareUtf8Ordinal(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right)
    {
        Span<char> leftChars = stackalloc char[2];
        Span<char> rightChars = stackalloc char[2];
        while (!left.IsEmpty && !right.IsEmpty)
        {
            OperationStatus leftStatus = Rune.DecodeFromUtf8(left, out Rune leftRune, out int leftBytes);
            OperationStatus rightStatus = Rune.DecodeFromUtf8(right, out Rune rightRune, out int rightBytes);
            if (leftStatus != OperationStatus.Done || rightStatus != OperationStatus.Done)
                throw new InvalidDataException("Binary string contains malformed UTF-8.");

            int leftLength = leftRune.EncodeToUtf16(leftChars);
            int rightLength = rightRune.EncodeToUtf16(rightChars);
            int shared = Math.Min(leftLength, rightLength);
            for (int index = 0; index < shared; index++)
            {
                int comparison = leftChars[index].CompareTo(rightChars[index]);
                if (comparison != 0)
                    return comparison;
            }
            if (leftLength != rightLength)
                return leftLength.CompareTo(rightLength);

            left = left[leftBytes..];
            right = right[rightBytes..];
        }
        return left.IsEmpty ? (right.IsEmpty ? 0 : -1) : 1;
    }

    private void ValidateBytePayloadLength(int length)
    {
        if (length < 0)
            throw new InvalidDataException("Negative byte payload length.");
        if (length > _limits.MaxBytePayloadBytes)
            throw new InvalidDataException(
                $"Byte payload length {length} exceeds {_limits.MaxBytePayloadBytes}.");
        Consume(ref _decodedBytes, length, _limits.MaxTotalDecodedBytes, "decoded bytes");
    }

    private static void Consume(ref long total, long amount, long maximum, string description)
    {
        try
        {
            total = checked(total + amount);
        }
        catch (OverflowException exception)
        {
            throw new InvalidDataException($"{description} budget overflowed.", exception);
        }
        if (total > maximum)
            throw new InvalidDataException(
                $"Total {description} {total} exceeds configured limit {maximum}.");
    }
}
