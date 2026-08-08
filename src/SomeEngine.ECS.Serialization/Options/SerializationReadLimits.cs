namespace SomeEngine.ECS.Serialization;

/// <summary>
/// Central allocation and complexity limits for serialized input. Limits are enforced before
/// allocating final World storage or codec-owned values and are shared across the complete
/// top-level ECS read. These are World-domain limits, not an alternate projection of the
/// span-backed binary-document limits in SomeEngine.Serialization.
/// </summary>
public sealed record SerializationReadLimits
{
    public static SerializationReadLimits Default { get; } = new();

    public int MaxManifestEntries { get; init; } = 65_536;
    public int MaxStableNameBytes { get; init; } = 16_384;
    public int MaxEntitySlots { get; init; } = 10_000_000;
    public int MaxEntities { get; init; } = 10_000_000;
    public int MaxItemsPerEntity { get; init; } = 65_536;
    public long MaxTotalEntityItems { get; init; } = 100_000_000;
    public int MaxBufferElementsPerBuffer { get; init; } = 16_000_000;
    public long MaxTotalBufferElements { get; init; } = 100_000_000;
    public int MaxTopologyPayloads { get; init; } = 65_536;
    public int MaxTopologyEntriesPerSection { get; init; } = 16_000_000;
    public long MaxTotalTopologyEntries { get; init; } = 100_000_000;
    public int MaxPayloadBytes { get; init; } = 256 * 1024 * 1024;
    public long MaxTotalPayloadBytes { get; init; } = 2L * 1024 * 1024 * 1024;
    public int MaxStringBytes { get; init; } = 16 * 1024 * 1024;
    public long MaxTotalStringBytes { get; init; } = 256L * 1024 * 1024;
    public long MaxTotalAllocationBytes { get; init; } = 1024L * 1024 * 1024;
    public long MaxCheckpointBytes { get; init; } = 64L * 1024 * 1024 * 1024;
}

internal sealed class SerializationReadBudget
{
    // Conservative 64-bit estimates for the final World storage and the small identity/runtime
    // metadata used while filling it. The budget is a security boundary, not a profiler: modest
    // over-counting is preferable to admitting attacker-controlled final storage whose arrays fit
    // while their object, set, and table overhead does not. These estimates do not represent or
    // authorize a captured payload graph or second physical backing.
    private const int ArrayOverheadBytes = 24;
    private const int FinalEntityStorageEstimateBytesPerEntity = 72;
    private const int FinalEntityItemStorageEstimateBytesPerItem = 48;
    private const int TopologyReadMetadataEstimateBytesPerPayload = 96;

    private long _entityItems;
    private long _bufferElements;
    private long _topologyEntries;
    private long _payloadBytes;
    private long _stringBytes;
    private long _allocatedBytes;

    internal SerializationReadBudget(SerializationReadLimits? limits)
    {
        Limits = limits ?? SerializationReadLimits.Default;
    }

    internal SerializationReadLimits Limits { get; }

    internal int Count(int value, int maximum, string description)
    {
        if (value < 0)
            throw new InvalidDataException($"Negative {description} count.");
        if (value > maximum)
            throw new InvalidDataException($"{description} count {value} exceeds the configured limit {maximum}.");
        return value;
    }

    internal int ManifestCount(int value)
    {
        Count(value, Limits.MaxManifestEntries, "serialization manifest");
        ReserveArray(value, 40, "serialization manifest array");
        return value;
    }

    internal int EntitySlotCount(int value)
    {
        Count(value, Limits.MaxEntitySlots, "entity slot");
        ReserveArray(value, 16, "entity slot array");
        return value;
    }

    internal int EntityCount(int value)
    {
        Count(value, Limits.MaxEntities, "entity payload");
        ReserveArray(
            value,
            checked(IntPtr.Size + FinalEntityStorageEstimateBytesPerEntity),
            "final entity storage estimate");
        return value;
    }

    internal int TopologyPayloadCount(int value)
    {
        Count(value, Limits.MaxTopologyPayloads, "topology payload");
        ReserveArray(
            value,
            TopologyReadMetadataEstimateBytesPerPayload,
            "topology runtime metadata and stable-id set");
        ReserveAllocation(128, "topology runtime metadata and stable-id set objects");
        return value;
    }

    internal int EntityItemCount(int value)
    {
        Count(value, Limits.MaxItemsPerEntity, "entity item");
        Consume(ref _entityItems, value, Limits.MaxTotalEntityItems, "total entity item");
        ReserveArray(
            value,
            FinalEntityItemStorageEstimateBytesPerItem,
            "final entity item storage estimate");
        return value;
    }

    internal int BufferElementCount<T>(int value)
        where T : struct
    {
        Count(value, Limits.MaxBufferElementsPerBuffer, "buffer element");
        Consume(ref _bufferElements, value, Limits.MaxTotalBufferElements, "total buffer element");
        ReserveArray(value, System.Runtime.CompilerServices.Unsafe.SizeOf<T>(), "buffer element array");
        return value;
    }

    internal int TopologyEntryCount(int value, string description)
    {
        Count(value, Limits.MaxTopologyEntriesPerSection, description);
        Consume(ref _topologyEntries, value, Limits.MaxTotalTopologyEntries, "total topology entry");
        ReserveArray(value, 32, $"{description} array");
        return value;
    }

    internal int PayloadLength(int value)
    {
        Count(value, Limits.MaxPayloadBytes, "serialized payload byte");
        Consume(ref _payloadBytes, value, Limits.MaxTotalPayloadBytes, "total payload byte");
        return value;
    }

    internal int StringCharacterCount(int value, bool stableName = false)
    {
        Count(value, stableName ? Limits.MaxStableNameBytes : Limits.MaxStringBytes,
            stableName ? "stable type name character" : "string character");
        ReserveArray(value, sizeof(char), stableName ? "stable type name" : "serialized string");
        return value;
    }

    internal void StringBytesConsumed(int value, bool stableName = false)
    {
        Count(value, stableName ? Limits.MaxStableNameBytes : Limits.MaxStringBytes,
            stableName ? "stable type name byte" : "string byte");
        Consume(ref _stringBytes, value, Limits.MaxTotalStringBytes, "total string byte");
    }

    internal void ReserveAllocation(long bytes, string description)
    {
        if (bytes < 0)
            throw new InvalidDataException($"Negative {description} allocation size.");
        try
        {
            _allocatedBytes = checked(_allocatedBytes + bytes);
        }
        catch (OverflowException exception)
        {
            throw new InvalidDataException($"{description} allocation budget overflowed.", exception);
        }
        if (_allocatedBytes > Limits.MaxTotalAllocationBytes)
        {
            throw new InvalidDataException(
                $"Total allocation estimate {_allocatedBytes} exceeds the configured limit {Limits.MaxTotalAllocationBytes} while reserving {description}.");
        }
    }

    private void ReserveArray(int count, int elementSize, string description)
    {
        long bytes;
        try
        {
            bytes = checked(ArrayOverheadBytes + ((long)count * elementSize));
        }
        catch (OverflowException exception)
        {
            throw new InvalidDataException($"{description} size overflowed.", exception);
        }
        ReserveAllocation(bytes, description);
    }

    private static void Consume(ref long total, int amount, long maximum, string description)
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
            throw new InvalidDataException($"{description} count {total} exceeds the configured limit {maximum}.");
    }
}
