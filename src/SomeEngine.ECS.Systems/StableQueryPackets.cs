using System.ComponentModel;
using SomeEngine.ECS.Archetypes;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;

namespace SomeEngine.ECS.Systems;

/// <summary>Stable persistent chunk identity plus one contiguous physical row interval.</summary>
public readonly struct StableQueryPacketRange : IEquatable<StableQueryPacketRange>
{
    internal StableQueryPacketRange(
        long persistentChunkId,
        int rowStart,
        int rowCount,
        int chunkRowCount)
    {
        PersistentChunkId = persistentChunkId;
        RowStart = rowStart;
        RowCount = rowCount;
        ChunkRowCount = chunkRowCount;
    }

    public long PersistentChunkId { get; }

    public int RowStart { get; }

    public int RowCount { get; }

    /// <summary>Total rows in the captured persistent chunk image.</summary>
    public int ChunkRowCount { get; }

    public int RowEnd => checked(RowStart + RowCount);

    public bool Equals(StableQueryPacketRange other) =>
        PersistentChunkId == other.PersistentChunkId &&
        RowStart == other.RowStart &&
        RowCount == other.RowCount &&
        ChunkRowCount == other.ChunkRowCount;

    public override bool Equals(object? obj) =>
        obj is StableQueryPacketRange other && Equals(other);

    public override int GetHashCode() =>
        HashCode.Combine(PersistentChunkId, RowStart, RowCount, ChunkRowCount);
}

/// <summary>
/// Mechanical evidence that packets are stable, positive, contiguous within each chunk, and
/// pairwise non-overlapping. Staging offsets are the checked prefix sum of these ranges, so packet
/// slices cannot drift from the proof. Construction is runtime-owned after topology admission.
/// </summary>
public sealed class StableQueryPartitionProof
{
    private readonly StableQueryPacketRange[] _ranges;
    private readonly int[] _rowOffsets;
    private readonly int _totalRowCount;
    private readonly int _chunkCount;

    internal StableQueryPartitionProof(
        StableQueryPacketRange[] ownedRanges,
        long structureEpoch = 0,
        long topologyRevision = 0)
    {
        ArgumentNullException.ThrowIfNull(ownedRanges);
        if (structureEpoch < 0)
            throw new ArgumentOutOfRangeException(nameof(structureEpoch));
        if (topologyRevision < 0)
            throw new ArgumentOutOfRangeException(nameof(topologyRevision));
        _ranges = ownedRanges;
        _chunkCount = Validate(_ranges);
        _rowOffsets = new int[_ranges.Length + 1];
        for (int i = 0; i < _ranges.Length; i++)
            _rowOffsets[i + 1] = checked(_rowOffsets[i] + _ranges[i].RowCount);
        _totalRowCount = _rowOffsets[^1];
        StructureEpoch = structureEpoch;
        TopologyRevision = topologyRevision;
        Fingerprint = ComputeFingerprint(_ranges, structureEpoch, topologyRevision);
    }

    public int PacketCount => _ranges.Length;

    public ulong Fingerprint { get; }

    public int TotalRowCount => _totalRowCount;

    public int ChunkCount => _chunkCount;

    /// <summary>
    /// World structural epoch held by the runtime capture owner. Zero denotes a synthetic proof
    /// used only by internal validation tests.
    /// </summary>
    public long StructureEpoch { get; }

    /// <summary>
    /// Monotonic topology-write fact version captured under topology-read admission. Zero denotes
    /// a synthetic validation-only proof.
    /// </summary>
    public long TopologyRevision { get; }

    public StableQueryPacketRange GetPacket(int index)
    {
        if ((uint)index >= (uint)_ranges.Length)
            throw new ArgumentOutOfRangeException(nameof(index));
        return _ranges[index];
    }

    internal int GetRowOffset(int packetIndex)
    {
        if ((uint)packetIndex >= (uint)_ranges.Length)
            throw new ArgumentOutOfRangeException(nameof(packetIndex));
        return _rowOffsets[packetIndex];
    }

    public bool ProvesNonOverlap(int first, int second)
    {
        StableQueryPacketRange left = GetPacket(first);
        StableQueryPacketRange right = GetPacket(second);
        if (left.PersistentChunkId != right.PersistentChunkId)
            return true;
        return left.RowEnd <= right.RowStart || right.RowEnd <= left.RowStart;
    }

    private static int Validate(ReadOnlySpan<StableQueryPacketRange> ranges)
    {
        var completedChunks = new HashSet<long>();
        long chunkId = -1;
        int expectedStart = 0;
        int chunkRowCount = 0;
        int chunkCount = 0;
        for (int i = 0; i < ranges.Length; i++)
        {
            StableQueryPacketRange range = ranges[i];
            if (range.PersistentChunkId <= 0 || range.RowStart < 0 || range.RowCount <= 0 ||
                range.ChunkRowCount <= 0 ||
                range.RowEnd > range.ChunkRowCount)
            {
                throw new InvalidOperationException("A stable query packet has an invalid identity or row range.");
            }

            bool sameChunk = range.PersistentChunkId == chunkId;
            if (!sameChunk)
            {
                if (chunkId > 0 && expectedStart != chunkRowCount)
                {
                    throw new InvalidOperationException(
                        "Stable query packets must cover every captured row through the end of each chunk.");
                }
                if (!completedChunks.Add(range.PersistentChunkId))
                {
                    throw new InvalidOperationException(
                        "A stable persistent chunk cannot reappear after another chunk in one partition.");
                }
                chunkId = range.PersistentChunkId;
                expectedStart = 0;
                chunkRowCount = range.ChunkRowCount;
                chunkCount++;
            }
            else if (range.ChunkRowCount != chunkRowCount)
            {
                throw new InvalidOperationException(
                    "Stable query packets for one persistent chunk must agree on captured row count.");
            }

            if (range.RowStart != expectedStart)
            {
                throw new InvalidOperationException(
                    "Stable query packets must form one contiguous, non-overlapping partition per chunk.");
            }
            expectedStart = checked(range.RowStart + range.RowCount);
        }
        if (chunkId > 0 && expectedStart != chunkRowCount)
        {
            throw new InvalidOperationException(
                "Stable query packets must cover every captured row through the end of each chunk.");
        }
        return chunkCount;
    }

    private static ulong ComputeFingerprint(
        ReadOnlySpan<StableQueryPacketRange> ranges,
        long structureEpoch,
        long topologyRevision)
    {
        const ulong offset = 14695981039346656037UL;
        const ulong prime = 1099511628211UL;
        ulong hash = (offset ^ (ulong)structureEpoch) * prime;
        hash = (hash ^ (ulong)topologyRevision) * prime;
        for (int i = 0; i < ranges.Length; i++)
        {
            StableQueryPacketRange range = ranges[i];
            hash = (hash ^ (ulong)range.PersistentChunkId) * prime;
            hash = (hash ^ (uint)range.RowStart) * prime;
            hash = (hash ^ (uint)range.RowCount) * prime;
            hash = (hash ^ (uint)range.ChunkRowCount) * prime;
        }
        return hash;
    }
}

internal sealed class StableQueryPacketSet
{
    private readonly QueryPacket[] _packets;

    internal StableQueryPacketSet(
        World world,
        QueryPacket[] ownedPackets,
        StableQueryPartitionProof proof,
        uint lastSystemVersion)
    {
        World = world;
        _packets = ownedPackets;
        Proof = proof;
        LastSystemVersion = lastSystemVersion;
    }

    internal World World { get; }

    internal ReadOnlySpan<QueryPacket> Packets => _packets;

    internal StableQueryPartitionProof Proof { get; }

    internal uint LastSystemVersion { get; }
}

internal readonly struct QueryPacket
{
    internal QueryPacket(
        QueryArchetypeMatch match,
        Chunk chunk,
        StableQueryPacketRange range)
    {
        Match = match;
        Chunk = chunk;
        Range = range;
    }

    internal QueryArchetypeMatch Match { get; }

    internal Chunk Chunk { get; }

    internal StableQueryPacketRange Range { get; }

}

internal static class StableQueryPacketAddress
{
    // Current ECS chunks top out at 2^18 rows. Four times that capacity leaves room for a future
    // chunk-size increase while extending the process-lifetime address space from ~4 billion to
    // ~8.8 trillion never-reused chunk identities.
    internal const long RowsPerChunkStride = 1L << 20;

    internal static long Address(in StableQueryPacketRange range)
    {
        if (range.ChunkRowCount > RowsPerChunkStride)
        {
            throw new InvalidOperationException(
                "Captured chunk row count exceeds the stable packet address stride.");
        }
        long chunkOffset = checked((range.PersistentChunkId - 1) * RowsPerChunkStride);
        return checked(chunkOffset + range.RowStart);
    }

}
