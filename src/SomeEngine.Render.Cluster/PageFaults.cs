using System.Runtime.InteropServices;

namespace SomeEngine.Render.Cluster;

[StructLayout(LayoutKind.Sequential)]
internal readonly record struct ClusterPageRequest(uint LeafNodeIndex, uint FirstPageId, uint PageCount, uint Priority);

internal readonly ref struct PageFaultRead
{
    public PageFaultRead(
        ClusterEpochId epochId,
        uint reportedCount,
        ReadOnlySpan<ClusterPageRequest> requests)
    {
        if (!epochId.IsValid)
            throw new ArgumentException("A page-fault read requires a valid Cluster epoch id.", nameof(epochId));
        if (reportedCount < checked((uint)requests.Length))
            throw new ArgumentOutOfRangeException(nameof(reportedCount), "Reported fault count cannot be smaller than the stored page count.");
        EpochId = epochId;
        ReportedCount = reportedCount;
        Requests = requests;
    }

    public ClusterEpochId EpochId { get; }
    public uint ReportedCount { get; }
    public ReadOnlySpan<ClusterPageRequest> Requests { get; }
    public uint StoredCount => checked((uint)Requests.Length);
    public uint DroppedCount => ReportedCount - StoredCount;
    public bool WasTruncated => DroppedCount != 0;
}

/// <summary>
/// Borrows complete GPU page requests from the readback span. Each request carries
/// its selected leaf, page range and the strongest view/category priority.
/// </summary>
internal sealed class PageFaults
{
    private readonly ClusterEpochId _epochId;
    private readonly int _capacity;

    public PageFaults(ClusterEpochId epochId, int capacity)
    {
        if (!epochId.IsValid)
            throw new ArgumentException("A page-fault decoder requires a valid Cluster epoch id.", nameof(epochId));
        if (capacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(capacity));
        _epochId = epochId;
        _capacity = capacity;
    }

    public PageFaultRead Read(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length % sizeof(uint) != 0)
            throw new InvalidDataException("GPU page-fault data must contain whole 32-bit words.");

        ReadOnlySpan<uint> words = MemoryMarshal.Cast<byte, uint>(bytes);
        if (words.Length == 0)
            return new PageFaultRead(_epochId, 0, []);

        uint reportedCount = words[0];
        uint storageLimit = checked((uint)_capacity);
        uint availableCount = checked((uint)(words.Length - 1) / 4u);
        uint count = Math.Min(reportedCount, Math.Min(storageLimit, availableCount));
        if (count == 0)
            return new PageFaultRead(_epochId, reportedCount, []);

        return new PageFaultRead(
            _epochId,
            reportedCount,
            MemoryMarshal.Cast<uint, ClusterPageRequest>(words.Slice(1, checked((int)count * 4))));
    }
}
