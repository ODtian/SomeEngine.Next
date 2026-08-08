using SomeEngine.Serialization.Containers;

namespace SomeEngine.Serialization.Streaming;

/// <summary>
/// Descriptor-caching single-range loader. Each request reads only its selected chunk directly
/// into the lease that will be published; stored-range coalescing is intentionally unsupported
/// because a merged range plus per-chunk owners would retain duplicate payload backings.
/// </summary>
internal sealed class DocumentChunkLoader<T>
    where T : IBinaryContract<T>
{
    private readonly object _gate = new();
    private readonly BinaryDocument<T> _document;
    private readonly ChunkStreamingMetrics _metrics;
    private readonly Dictionary<ulong, BinaryChunkEntry> _descriptors = [];

    internal DocumentChunkLoader(
        BinaryDocument<T> document,
        ChunkStreamingMetrics metrics)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(metrics);
        if (document.RetainsResidentBacking && !document.SourceLeasesAreImmutable)
        {
            throw new NotSupportedException(
                "A resident document source must expose immutable borrowed leases; unknown resident " +
                "wrappers fail closed before a streamed runtime cache is created.");
        }
        _document = document;
        _metrics = metrics;
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
        return new ChunkLoadEstimate(descriptor.StoredLength, descriptor.DecodedLength);
    }

    internal async ValueTask<ChunkLease> LoadAsync(
        ulong key,
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
            ChunkLease lease = await _document.AcquireChunkAsync(descriptor, cancellationToken)
                .ConfigureAwait(false);
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
