using System.Diagnostics;

namespace SomeEngine.Serialization.Streaming;

public readonly record struct ChunkStreamingSnapshot(
    long Requests,
    long CacheHits,
    long CacheMisses,
    long DeduplicatedWaiters,
    long LoadsCompleted,
    long LoadsFailed,
    long BytesLoaded,
    long Evictions,
    long Cancellations,
    long QueueLatencyTicks,
    long DecodeLatencyTicks,
    long PinnedBytes,
    long ResidentBytes,
    long StoredBytesRead,
    long TimeToFirstRenderableTicks)
{
    public ChunkStreamingSnapshot(
        long Requests,
        long CacheHits,
        long CacheMisses,
        long DeduplicatedWaiters,
        long LoadsCompleted,
        long LoadsFailed,
        long BytesLoaded,
        long Evictions,
        long Cancellations,
        long QueueLatencyTicks,
        long DecodeLatencyTicks,
        long PinnedBytes,
        long ResidentBytes)
        : this(
            Requests,
            CacheHits,
            CacheMisses,
            DeduplicatedWaiters,
            LoadsCompleted,
            LoadsFailed,
            BytesLoaded,
            Evictions,
            Cancellations,
            QueueLatencyTicks,
            DecodeLatencyTicks,
            PinnedBytes,
            ResidentBytes,
            StoredBytesRead: 0,
            TimeToFirstRenderableTicks: 0)
    {
    }

    public void Deconstruct(
        out long Requests,
        out long CacheHits,
        out long CacheMisses,
        out long DeduplicatedWaiters,
        out long LoadsCompleted,
        out long LoadsFailed,
        out long BytesLoaded,
        out long Evictions,
        out long Cancellations,
        out long QueueLatencyTicks,
        out long DecodeLatencyTicks,
        out long PinnedBytes,
        out long ResidentBytes)
    {
        Requests = this.Requests;
        CacheHits = this.CacheHits;
        CacheMisses = this.CacheMisses;
        DeduplicatedWaiters = this.DeduplicatedWaiters;
        LoadsCompleted = this.LoadsCompleted;
        LoadsFailed = this.LoadsFailed;
        BytesLoaded = this.BytesLoaded;
        Evictions = this.Evictions;
        Cancellations = this.Cancellations;
        QueueLatencyTicks = this.QueueLatencyTicks;
        DecodeLatencyTicks = this.DecodeLatencyTicks;
        PinnedBytes = this.PinnedBytes;
        ResidentBytes = this.ResidentBytes;
    }

    /// <summary>Decoded bytes successfully published; retained as an explicit alias for BytesLoaded.</summary>
    public long DecodedBytesLoaded => BytesLoaded;

    /// <summary>Actual stored range bytes divided by successfully published decoded bytes.</summary>
    public double ReadAmplification => DecodedBytesLoaded == 0
        ? 0
        : StoredBytesRead / (double)DecodedBytesLoaded;

    public bool HasFirstRenderable => TimeToFirstRenderableTicks != 0;
}

public sealed class ChunkStreamingMetrics
{
    private long _requests;
    private long _cacheHits;
    private long _cacheMisses;
    private long _deduplicatedWaiters;
    private long _loadsCompleted;
    private long _loadsFailed;
    private long _bytesLoaded;
    private long _evictions;
    private long _cancellations;
    private long _queueLatencyTicks;
    private long _decodeLatencyTicks;
    private long _pinnedBytes;
    private long _residentBytes;
    private long _storedBytesRead;
    private long _timeToFirstRenderableTicks;
    private readonly long _streamingStartedTimestamp = Stopwatch.GetTimestamp();

    internal void Request() => Interlocked.Increment(ref _requests);
    internal void CacheHit() => Interlocked.Increment(ref _cacheHits);
    internal void CacheMiss() => Interlocked.Increment(ref _cacheMisses);
    internal void Deduplicated() => Interlocked.Increment(ref _deduplicatedWaiters);
    internal void LoadCompleted(long bytes, long queueTicks, long decodeTicks)
    {
        Interlocked.Increment(ref _loadsCompleted);
        Interlocked.Add(ref _bytesLoaded, bytes);
        Interlocked.Add(ref _queueLatencyTicks, queueTicks);
        Interlocked.Add(ref _decodeLatencyTicks, decodeTicks);
    }
    internal void LoadFailed() => Interlocked.Increment(ref _loadsFailed);
    internal void StoredBytesRead(long bytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(bytes);
        Interlocked.Add(ref _storedBytesRead, bytes);
    }
    internal void Evicted(long bytes)
    {
        Interlocked.Increment(ref _evictions);
        Interlocked.Add(ref _residentBytes, -bytes);
    }
    internal void Canceled() => Interlocked.Increment(ref _cancellations);
    internal void ResidentAdded(long bytes) => Interlocked.Add(ref _residentBytes, bytes);
    internal void ResidentRemoved(long residentBytes, long pinnedBytes)
    {
        Interlocked.Add(ref _residentBytes, -residentBytes);
        Interlocked.Add(ref _pinnedBytes, -pinnedBytes);
    }
    internal void Pin(long bytes) => Interlocked.Add(ref _pinnedBytes, bytes);
    internal void Unpin(long bytes) => Interlocked.Add(ref _pinnedBytes, -bytes);

    /// <summary>
    /// Marks the first frame/resource state that the consumer considers renderable. The first call
    /// wins; later calls are ignored so independent subsystems cannot move the milestone.
    /// </summary>
    public bool TryMarkFirstRenderable()
    {
        long elapsed = Math.Max(1, Stopwatch.GetTimestamp() - _streamingStartedTimestamp);
        return Interlocked.CompareExchange(ref _timeToFirstRenderableTicks, elapsed, 0) == 0;
    }

    public ChunkStreamingSnapshot Snapshot() => new(
        Interlocked.Read(ref _requests),
        Interlocked.Read(ref _cacheHits),
        Interlocked.Read(ref _cacheMisses),
        Interlocked.Read(ref _deduplicatedWaiters),
        Interlocked.Read(ref _loadsCompleted),
        Interlocked.Read(ref _loadsFailed),
        Interlocked.Read(ref _bytesLoaded),
        Interlocked.Read(ref _evictions),
        Interlocked.Read(ref _cancellations),
        Interlocked.Read(ref _queueLatencyTicks),
        Interlocked.Read(ref _decodeLatencyTicks),
        Interlocked.Read(ref _pinnedBytes),
        Interlocked.Read(ref _residentBytes),
        Interlocked.Read(ref _storedBytesRead),
        Interlocked.Read(ref _timeToFirstRenderableTicks));
}
