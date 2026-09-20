using SomeEngine.Serialization.Containers;

namespace SomeEngine.Serialization.Streaming;

/// <summary>
/// Descriptor-caching single-range loader. Each request reads only its selected chunk directly
/// into the lease that will be published; stored-range coalescing is intentionally unsupported
/// because a merged range plus per-chunk owners would retain duplicate payload backings.
/// </summary>
internal sealed class DocumentChunkLoader
{
    private readonly object _gate = new();
    private readonly BinaryDocument _document;
    private readonly ChunkStreamingMetrics _metrics;
    private readonly PageArena? _pages;
    private readonly Dictionary<ulong, BinaryChunkEntry> _descriptors = [];

    internal DocumentChunkLoader(
        BinaryDocument document,
        ChunkStreamingMetrics metrics,
        PageArena? pages = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(metrics);
        _document = pages is null ? document : document.Retain();
        _metrics = metrics;
        _pages = pages;
    }

    internal async ValueTask<ChunkLoadEstimate> EstimateAsync(
        ulong key,
        CancellationToken cancellationToken)
    {
        BinaryChunkEntry descriptor = await _document.FindChunkAsync(key, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Chunk 0x{key:X16} was not found.");
        lock (_gate)
            _descriptors[key] = descriptor;
        return new ChunkLoadEstimate(descriptor.StoredLength, descriptor.DecodedLength)
        { BufferBytes = _pages?.BufferCapacity(descriptor.DecodedLength) };
    }

    internal ValueTask<ChunkLease> LoadAsync(ulong key, CancellationToken cancellationToken)
        => LoadCoreAsync(key, null, cancellationToken);

    internal ValueTask<ChunkLease> LoadPagesAsync(ulong key, ResidencyReservation reservation,
        CancellationToken cancellationToken) => LoadCoreAsync(key, reservation, cancellationToken);

    internal ValueTask DisposeAsync() => _pages is null ? ValueTask.CompletedTask : _document.DisposeAsync();

    private async ValueTask<ChunkLease> LoadCoreAsync(
        ulong key,
        ResidencyReservation? reservation,
        CancellationToken cancellationToken)
    {
        BinaryChunkEntry descriptor;
        lock (_gate)
            _descriptors.TryGetValue(key, out descriptor);
        if (descriptor.Key == 0)
        {
            descriptor = await _document.FindChunkAsync(key, cancellationToken).ConfigureAwait(false)
                ?? throw new KeyNotFoundException($"Chunk 0x{key:X16} was not found.");
        }

        try
        {
            ChunkLease lease = reservation is null
                ? await _document.AcquireChunkAsync(descriptor, cancellationToken).ConfigureAwait(false)
                : await _document.AcquirePagesAsync(descriptor, _pages!, reservation, cancellationToken).ConfigureAwait(false);
            _metrics.StoredBytesRead(descriptor.StoredLength);
            return lease;
        }
        finally
        {
            lock (_gate)
                _descriptors.Remove(key);
        }
    }
}
