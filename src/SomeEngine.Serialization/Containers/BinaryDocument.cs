using System.Buffers;
using System.IO.Compression;
using System.Security.Cryptography;
using SomeEngine.Serialization.IO;

namespace SomeEngine.Serialization.Containers;

internal sealed class DocumentLifetime
{
    private int _disposed;

    internal bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    internal void ThrowIfDisposed(object owner)
        => ObjectDisposedException.ThrowIf(IsDisposed, owner);

    internal void Dispose() => Interlocked.Exchange(ref _disposed, 1);
}

public sealed class ChunkLease : IDisposable
{
    private readonly DocumentLifetime _documentLifetime;
    private RangeLease? _range;
    private int _disposed;

    internal ChunkLease(
        BinaryChunkEntry descriptor,
        RangeLease range,
        DocumentLifetime documentLifetime)
    {
        Descriptor = descriptor;
        _range = range;
        _documentLifetime = documentLifetime;
    }

    public BinaryChunkEntry Descriptor { get; }

    public ReadOnlyMemory<byte> Memory
    {
        get
        {
            _documentLifetime.ThrowIfDisposed(this);
            RangeLease? range = Volatile.Read(ref _range);
            ObjectDisposedException.ThrowIf(range is null || Volatile.Read(ref _disposed) != 0, this);
            return range.Memory;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        Interlocked.Exchange(ref _range, null)?.Dispose();
    }
}

/// <summary>
/// Open binary document. Opening reads only the fixed header and small root; directory entries
/// and chunk payloads are fetched lazily with explicit range reads.
/// </summary>
public sealed class BinaryDocument<T> : IAsyncDisposable
    where T : IBinaryContract<T>
{
    private readonly IRangeSource _source;
    private readonly bool _ownsSource;
    private readonly BinaryDocumentHeader _header;
    private readonly BinaryReadLimits _limits;
    private readonly string _sourceGeneration;
    private readonly BinaryWireTypeDescriptor[] _typeCatalog;
    private readonly Digest256 _headerHash;
    private readonly Digest256 _catalogHash;
    private readonly DocumentLifetime _lifetime = new();

    private BinaryDocument(
        IRangeSource source,
        bool ownsSource,
        BinaryDocumentHeader header,
        BinaryWireTypeDescriptor[] typeCatalog,
        BinaryReadLimits limits,
        T root,
        Digest256 headerHash,
        Digest256 catalogHash)
    {
        _source = source;
        _ownsSource = ownsSource;
        _header = header;
        _typeCatalog = typeCatalog;
        _limits = limits;
        _sourceGeneration = source.Generation;
        _headerHash = headerHash;
        _catalogHash = catalogHash;
        Root = root;
    }

    public T Root { get; }
    public Guid Generation => _header.Generation;
    public uint ChunkCount => _header.ChunkCount;
    public ulong SchemaFingerprint => _header.SchemaFingerprint;
    public long TotalLength => _header.TotalLength;
    public string SourceGeneration => _sourceGeneration;
    public bool RetainsResidentBacking => _source.RetainsResidentBacking;
    public bool SourceLeasesAreImmutable => _source.LeasesAreImmutable;
    public IReadOnlyList<BinaryWireTypeDescriptor> TypeCatalog => _typeCatalog;

    /// <summary>
    /// Reads only the fixed header and type catalog, validates their integrity, and returns the
    /// encoded root schema metadata for this logical <typeparamref name="T"/>. Root and chunk bytes
    /// are not read.
    /// </summary>
    public static async ValueTask<BinaryEnvelopeMetadata> InspectAsync(
        IRangeSource source,
        BinaryReadLimits? limits = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        BinaryReadLimits effectiveLimits = limits ?? BinaryReadLimits.Default;
        string openingGeneration = source.Generation;

        using RangeLease headerLease = await source.AcquireAsync(
            0,
            BinaryDocumentFormat.HeaderSize,
            cancellationToken).ConfigureAwait(false);
        BinaryDocumentHeader header = BinaryDocumentFormat.ReadHeader(headerLease.Memory.Span);
        ValidateEnvelopeHeader(header, source.Length, effectiveLimits);

        using RangeLease catalogLease = await source.AcquireAsync(
            BinaryDocumentFormat.HeaderSize,
            checked((int)header.CatalogLength),
            cancellationToken).ConfigureAwait(false);
        BinaryWireTypeDescriptor[] typeCatalog = BinaryDocumentFormat.ReadTypeCatalog(
            catalogLease.Memory.Span,
            effectiveLimits);
        BinaryWireTypeDescriptor rootDescriptor = GetRootCatalogEntry<T>(typeCatalog, header);
        if (!StringComparer.Ordinal.Equals(openingGeneration, source.Generation))
            throw new IOException("Range source generation changed while inspecting an binary document.");

        return new BinaryEnvelopeMetadata(
            rootDescriptor.TypeId,
            header.SchemaFingerprint,
            header.Compatibility,
            header.SchemaEpoch,
            header.RootLength);
    }

    public static async ValueTask<BinaryDocument<T>> OpenAsync(
        IRangeSource source,
        bool ownsSource = false,
        BinaryReadLimits? limits = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        BinaryReadLimits effectiveLimits = limits ?? BinaryReadLimits.Default;
        string openingGeneration = source.Generation;

        try
        {
            using RangeLease headerLease = await source.AcquireAsync(
                0,
                BinaryDocumentFormat.HeaderSize,
                cancellationToken).ConfigureAwait(false);
            BinaryDocumentHeader header = BinaryDocumentFormat.ReadHeader(headerLease.Memory.Span);
            ValidateHeader<T>(header, source.Length, effectiveLimits);

            using RangeLease catalogLease = await source.AcquireAsync(
                BinaryDocumentFormat.HeaderSize,
                checked((int)header.CatalogLength),
                cancellationToken).ConfigureAwait(false);
            BinaryWireTypeDescriptor[] typeCatalog = BinaryDocumentFormat.ReadTypeCatalog(
                catalogLease.Memory.Span,
                effectiveLimits);
            _ = GetRootCatalogEntry<T>(typeCatalog, header);

            using RangeLease rootLease = await source.AcquireAsync(
                header.RootOffset,
                checked((int)header.RootLength),
                cancellationToken).ConfigureAwait(false);
            VerifyHash(rootLease.Memory.Span, header.RootHash, "binary document root");
            T root = BinaryContractSerializer.Deserialize<T>(rootLease.Memory.Span, effectiveLimits);

            if (!StringComparer.Ordinal.Equals(openingGeneration, source.Generation))
                throw new IOException("Range source generation changed while opening a binary document.");

            var document = new BinaryDocument<T>(
                source,
                ownsSource,
                header,
                typeCatalog,
                effectiveLimits,
                root,
                Digest256.ComputeSha256(headerLease.Memory.Span),
                Digest256.ComputeSha256(catalogLease.Memory.Span));
            if (source is IBinaryDocumentReceipt receipt)
            {
                Digest256 digest = await document.ComputeAuthenticationDigestAsync(cancellationToken)
                    .ConfigureAwait(false);
                receipt.Validate(digest);
            }
            return document;
        }
        catch
        {
            if (ownsSource)
                await source.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async ValueTask<ChunkLease> AcquireChunkAsync(
        ulong key,
        CancellationToken cancellationToken = default)
    {
        ChunkLease? lease = await TryAcquireChunkAsync(key, cancellationToken).ConfigureAwait(false);
        return lease ?? throw new KeyNotFoundException($"Binary chunk 0x{key:X16} was not found.");
    }

    /// <summary>
    /// Acquires a root-authenticated logical chunk and proves that its directory length still
    /// matches the exact-schema root before publishing the lease.
    /// </summary>
    public async ValueTask<ChunkLease> AcquireChunkAsync(
        BinaryChunkRef chunk,
        CancellationToken cancellationToken = default)
    {
        BinaryChunkEntry descriptor = await FindRequiredChunkAsync(chunk, cancellationToken)
            .ConfigureAwait(false);
        return await AcquireChunkAsync(descriptor, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Allocates the caller's final chunk destination exactly once and reads or decompresses
    /// directly into it. No decoded lease or intermediate decoded payload is created.
    /// </summary>
    public async ValueTask<Memory<byte>?> TryReadChunkAsync(
        ulong key,
        Func<int, Memory<byte>> destinationFactory,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(destinationFactory);
        BinaryChunkEntry? found = await FindChunkAsync(key, cancellationToken).ConfigureAwait(false);
        if (!found.HasValue)
            return null;

        return await ReadChunkAsync(found.Value, destinationFactory, cancellationToken)
            .ConfigureAwait(false);
    }

    private async ValueTask<Memory<byte>> ReadChunkAsync(
        BinaryChunkEntry descriptor,
        Func<int, Memory<byte>> destinationFactory,
        CancellationToken cancellationToken)
    {
        _lifetime.ThrowIfDisposed(this);
        if (_source.RetainsResidentBacking)
        {
            throw new NotSupportedException(
                "Owned chunk materialization from a whole-memory or memory-mapped document would " +
                "retain two physical payload backings. Consume a chunk lease/range, or open a file/range source.");
        }
        EnsureSourceGeneration();
        int decodedLength = checked((int)descriptor.DecodedLength);
        Memory<byte> destination = destinationFactory(decodedLength);
        if (destination.Length != decodedLength)
        {
            throw new InvalidOperationException(
                $"Chunk destination factory returned {destination.Length} bytes; expected exactly {decodedLength}.");
        }

        if (descriptor.Compression == ChunkCompression.None)
        {
            await _source.ReadExactlyAsync(
                descriptor.Offset,
                destination,
                cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await DecodeBrotliIntoAsync(descriptor, destination, cancellationToken).ConfigureAwait(false);
        }

        EnsureSourceGeneration();
        VerifyHash(destination.Span, descriptor.ContentHash, $"decoded chunk 0x{descriptor.Key:X16}");
        return destination;
    }

    /// <summary>Reads a root-authenticated logical chunk directly into the caller's final backing.</summary>
    public async ValueTask<Memory<byte>?> TryReadChunkAsync(
        BinaryChunkRef chunk,
        Func<int, Memory<byte>> destinationFactory,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(destinationFactory);
        BinaryChunkEntry descriptor = await FindRequiredChunkAsync(chunk, cancellationToken)
            .ConfigureAwait(false);
        return await ReadChunkAsync(descriptor, destinationFactory, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Opens an uncompressed chunk as a bounded child range source without materializing the chunk.
    /// This is used by packs whose chunks are themselves binary documents.
    /// </summary>
    public async ValueTask<IRangeSource> OpenChunkRangeSourceAsync(
        ulong key,
        CancellationToken cancellationToken = default)
    {
        _lifetime.ThrowIfDisposed(this);
        BinaryChunkEntry? found = await FindChunkAsync(key, cancellationToken).ConfigureAwait(false);
        if (!found.HasValue)
            throw new KeyNotFoundException($"Binary chunk 0x{key:X16} was not found.");
        return OpenChunkRangeSource(found.Value);
    }

    private IRangeSource OpenChunkRangeSource(BinaryChunkEntry descriptor)
    {
        if (descriptor.Compression != ChunkCompression.None)
        {
            throw new InvalidOperationException(
                $"Chunk 0x{descriptor.Key:X16} is compressed and cannot provide nested random access. " +
                "Store nested binary documents uncompressed and compress their semantic chunks instead.");
        }

        return new DocumentChunkRangeSource(_source, descriptor, _header.Generation, _lifetime);
    }

    /// <summary>Opens a root-authenticated uncompressed logical chunk as a bounded range source.</summary>
    public async ValueTask<IRangeSource> OpenChunkRangeSourceAsync(
        BinaryChunkRef chunk,
        CancellationToken cancellationToken = default)
    {
        BinaryChunkEntry descriptor = await FindRequiredChunkAsync(chunk, cancellationToken)
            .ConfigureAwait(false);
        return OpenChunkRangeSource(descriptor);
    }

    public async ValueTask<ChunkLease?> TryAcquireChunkAsync(
        ulong key,
        CancellationToken cancellationToken = default)
    {
        _lifetime.ThrowIfDisposed(this);
        EnsureSourceGeneration();
        BinaryChunkEntry? found = await FindChunkAsync(key, cancellationToken).ConfigureAwait(false);
        if (!found.HasValue)
            return null;

        return await AcquireChunkAsync(found.Value, cancellationToken).ConfigureAwait(false);
    }

    internal async ValueTask<ChunkLease> AcquireChunkAsync(
        BinaryChunkEntry descriptor,
        CancellationToken cancellationToken = default)
    {
        _lifetime.ThrowIfDisposed(this);
        EnsureSourceGeneration();
        ValidateEntryBounds(descriptor);
        if (_source.RetainsResidentBacking
            && (descriptor.Compression != ChunkCompression.None || !_source.LeasesAreImmutable))
        {
            throw new NotSupportedException(
                $"Chunk 0x{descriptor.Key:X16} cannot be decoded or snapshotted while its source retains " +
                "a resident physical backing. Only immutable uncompressed borrowed leases are allowed.");
        }

        if (descriptor.Compression != ChunkCompression.None)
        {
            IMemoryOwner<byte>? decodedOwner = MemoryPool<byte>.Shared.Rent(
                Math.Max(1, checked((int)descriptor.DecodedLength)));
            try
            {
                Memory<byte> decoded = decodedOwner.Memory[..checked((int)descriptor.DecodedLength)];
                await DecodeBrotliIntoAsync(descriptor, decoded, cancellationToken).ConfigureAwait(false);

                EnsureSourceGeneration();
                VerifyHash(decoded.Span, descriptor.ContentHash, $"decoded chunk 0x{descriptor.Key:X16}");
                RangeLease decodedLease = RangeLease.Own(decodedOwner, decoded.Length);
                decodedOwner = null;
                return new ChunkLease(descriptor, decodedLease, _lifetime);
            }
            finally
            {
                decodedOwner?.Dispose();
            }
        }

        if (!_source.LeasesAreImmutable)
        {
            throw new NotSupportedException(
                $"Chunk 0x{descriptor.Key:X16} cannot be published from a source whose leases are mutable; " +
                "snapshotting would create a second physical backing.");
        }

        RangeLease stored = await _source.AcquireAsync(
            descriptor.Offset,
            checked((int)descriptor.StoredLength),
            cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureSourceGeneration();
            VerifyHash(stored.Memory.Span, descriptor.ContentHash, $"chunk 0x{descriptor.Key:X16}");
            return new ChunkLease(descriptor, stored, _lifetime);
        }
        catch
        {
            stored.Dispose();
            throw;
        }
    }

    private async ValueTask DecodeBrotliIntoAsync(
        BinaryChunkEntry descriptor,
        Memory<byte> destination,
        CancellationToken cancellationToken)
    {
        await using var stored = new RangeSourceReadStream(
            _source,
            descriptor.Offset,
            descriptor.StoredLength);
        using var decoder = new BrotliStream(stored, CompressionMode.Decompress, leaveOpen: true);
        try
        {
            int written = 0;
            while (written < destination.Length)
            {
                int read = await decoder.ReadAsync(destination[written..], cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    throw new InvalidDataException(
                        $"Chunk 0x{descriptor.Key:X16} Brotli payload ended before its declared decoded length " +
                        $"{descriptor.DecodedLength}.");
                }
                written = checked(written + read);
            }

            byte[] overflowProbe = new byte[1];
            if (await decoder.ReadAsync(overflowProbe, cancellationToken).ConfigureAwait(false) != 0)
            {
                throw new InvalidDataException(
                    $"Chunk 0x{descriptor.Key:X16} Brotli payload expands beyond its declared length.");
            }
        }
        catch (InvalidOperationException exception)
        {
            throw new InvalidDataException(
                $"Chunk 0x{descriptor.Key:X16} contains invalid Brotli data.",
                exception);
        }
    }

    /// <summary>
    /// Verifies the complete decoded content of a chunk against its directory hash without
    /// publishing a lease. Uncompressed chunks are hashed in bounded ranges, so callers such as
    /// offline patch builders do not need to allocate the complete nested document.
    /// </summary>
    public async ValueTask<bool> VerifyChunkContentAsync(
        ulong key,
        CancellationToken cancellationToken = default)
    {
        _lifetime.ThrowIfDisposed(this);
        EnsureSourceGeneration();
        BinaryChunkEntry? found = await FindChunkAsync(key, cancellationToken).ConfigureAwait(false);
        if (!found.HasValue)
            return false;

        return await VerifyChunkContentAsync(found.Value, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Verifies a descriptor already read and validated by <see cref="FindChunkAsync"/>. This
    /// avoids a second directory lookup for pack admission and patch comparison.
    /// </summary>
    internal async ValueTask<bool> VerifyChunkContentAsync(
        BinaryChunkEntry descriptor,
        CancellationToken cancellationToken = default)
    {
        _lifetime.ThrowIfDisposed(this);
        EnsureSourceGeneration();
        ValidateEntryBounds(descriptor);

        if (descriptor.Compression != ChunkCompression.None)
        {
            try
            {
                await using var stored = new RangeSourceReadStream(
                    _source,
                    descriptor.Offset,
                    descriptor.StoredLength);
                using var decoder = new BrotliStream(stored, CompressionMode.Decompress, leaveOpen: true);
                using var decodedHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                byte[] buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
                try
                {
                    long decoded = 0;
                    int read;
                    while ((read = await decoder.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) != 0)
                    {
                        decoded = checked(decoded + read);
                        if (decoded > descriptor.DecodedLength)
                            return false;
                        decodedHash.AppendData(buffer.AsSpan(0, read));
                    }
                    if (decoded != descriptor.DecodedLength)
                        return false;
                    Span<byte> actual = stackalloc byte[BinaryDocumentFormat.HashSize];
                    return decodedHash.TryGetHashAndReset(actual, out int written)
                        && written == actual.Length
                        && descriptor.ContentHash.FixedTimeEquals(actual);
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }
            }
            catch (InvalidDataException)
            {
                return false;
            }
        }

        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        const int rangeBytes = 64 * 1024;
        long consumed = 0;
        while (consumed < descriptor.StoredLength)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int length = checked((int)Math.Min(rangeBytes, descriptor.StoredLength - consumed));
            using RangeLease range = await _source.AcquireAsync(
                checked(descriptor.Offset + consumed),
                length,
                cancellationToken).ConfigureAwait(false);
            EnsureSourceGeneration();
            hash.AppendData(range.Memory.Span);
            consumed = checked(consumed + length);
        }

        Span<byte> digest = stackalloc byte[BinaryDocumentFormat.HashSize];
        return hash.TryGetHashAndReset(digest, out int digestLength)
            && digestLength == digest.Length
            && descriptor.ContentHash.FixedTimeEquals(digest);
    }

    public async ValueTask<BinaryChunkEntry?> FindChunkAsync(
        ulong key,
        CancellationToken cancellationToken = default)
    {
        _lifetime.ThrowIfDisposed(this);
        long low = 0;
        long high = _header.ChunkCount - 1L;

        while (low <= high)
        {
            cancellationToken.ThrowIfCancellationRequested();
            long middle = low + ((high - low) >> 1);
            BinaryChunkEntry entry = await ReadEntryAsync(middle, cancellationToken).ConfigureAwait(false);
            ValidateEntryBounds(entry);

            if (entry.Key == key)
            {
                await ValidateEntryNeighborsAsync(middle, entry, cancellationToken).ConfigureAwait(false);
                return entry;
            }

            if (entry.Key < key)
                low = middle + 1;
            else
                high = middle - 1;
        }

        return null;
    }

    private async ValueTask<BinaryChunkEntry> FindRequiredChunkAsync(
        BinaryChunkRef chunk,
        CancellationToken cancellationToken)
    {
        ValidateChunkRef(chunk);
        BinaryChunkEntry? found = await FindChunkAsync(chunk.Key, cancellationToken)
            .ConfigureAwait(false);
        if (!found.HasValue)
            throw new KeyNotFoundException($"Binary chunk 0x{chunk.Key:X16} was not found.");

        BinaryChunkEntry descriptor = found.Value;
        if (descriptor.DecodedLength != chunk.DecodedLength)
        {
            throw new InvalidDataException(
                $"Binary chunk 0x{chunk.Key:X16} directory length disagrees with the exact-schema root: " +
                $"{descriptor.DecodedLength} != {chunk.DecodedLength}.");
        }

        return descriptor;
    }

    internal async ValueTask<Digest256> ComputeAuthenticationDigestAsync(
        CancellationToken cancellationToken = default)
    {
        _lifetime.ThrowIfDisposed(this);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        const int rangeBytes = 64 * 1024;
        long consumed = 0;
        while (consumed < _header.DirectoryLength)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int length = checked((int)Math.Min(rangeBytes, _header.DirectoryLength - consumed));
            using RangeLease range = await _source.AcquireAsync(
                checked(_header.DirectoryOffset + consumed),
                length,
                cancellationToken).ConfigureAwait(false);
            EnsureSourceGeneration();
            hash.AppendData(range.Memory.Span);
            consumed = checked(consumed + length);
        }

        Digest256 directoryHash = Digest256.Finish(hash);
        return BinaryDocumentFormat.ComputeAuthenticationDigest(
            _headerHash,
            _catalogHash,
            _header.RootHash,
            directoryHash);
    }

    public async ValueTask DisposeAsync()
    {
        if (_lifetime.IsDisposed)
            return;
        _lifetime.Dispose();
        if (_ownsSource)
            await _source.DisposeAsync().ConfigureAwait(false);
    }

    internal static void ValidateHeader<TContract>(
        in BinaryDocumentHeader header,
        long sourceLength,
        BinaryReadLimits limits)
        where TContract : IBinaryContract<TContract>
    {
        ValidateEnvelopeHeader(header, sourceLength, limits);
        if (header.Compatibility != TContract.Compatibility)
        {
            throw new InvalidDataException(
                $"Binary document compatibility mode {header.Compatibility} does not match " +
                $"reader mode {TContract.Compatibility} for '{typeof(TContract).FullName}'.");
        }

        BinaryContract<TContract>.ThrowIfIncompatible(header.SchemaFingerprint, header.SchemaEpoch);
    }

    private static void ValidateEnvelopeHeader(
        in BinaryDocumentHeader header,
        long sourceLength,
        BinaryReadLimits limits)
    {
        if (header.SchemaFingerprint == 0)
            throw new InvalidDataException("Binary document schema fingerprint cannot be zero.");
        if (header.SchemaEpoch == 0)
            throw new InvalidDataException("Binary document schema epoch cannot be zero.");
        if (header.TotalLength != sourceLength)
            throw new InvalidDataException(
                $"Binary document declares length {header.TotalLength}, source length is {sourceLength}.");
        if (header.CatalogLength < 4 + BinaryDocumentFormat.HashSize
            || header.CatalogLength > limits.MaxTypeCatalogBytes)
            throw new InvalidDataException(
                $"Binary type catalog length {header.CatalogLength} exceeds configured limits.");
        if (header.RootLength < 0 || header.RootLength > limits.MaxRootBytes)
            throw new InvalidDataException(
                $"Binary root length {header.RootLength} exceeds configured limit {limits.MaxRootBytes}.");
        if (header.ChunkCount > limits.MaxChunkCount)
            throw new InvalidDataException(
                $"Binary chunk count {header.ChunkCount} exceeds configured limit {limits.MaxChunkCount}.");

        long expectedDirectoryLength;
        long rootEnd;
        long directoryEnd;
        try
        {
            expectedDirectoryLength = checked((long)header.ChunkCount * BinaryDocumentFormat.DirectoryEntrySize);
            long minimumRootOffset = BinaryDocumentFormat.Align(
                checked(BinaryDocumentFormat.HeaderSize + header.CatalogLength),
                16);
            if (header.RootOffset != minimumRootOffset)
                throw new InvalidDataException($"Binary root offset {header.RootOffset} is invalid.");
            rootEnd = checked(header.RootOffset + header.RootLength);
            directoryEnd = checked(header.DirectoryOffset + header.DirectoryLength);
        }
        catch (OverflowException exception)
        {
            throw new InvalidDataException("Binary document metadata offsets overflowed.", exception);
        }

        if (header.DirectoryLength != expectedDirectoryLength)
            throw new InvalidDataException(
                $"Binary directory length {header.DirectoryLength} does not match chunk count {header.ChunkCount}.");
        if (header.DirectoryOffset < rootEnd || (header.DirectoryOffset & 15) != 0)
            throw new InvalidDataException("Binary directory overlaps the root or is not 16-byte aligned.");
        if (directoryEnd > header.TotalLength)
            throw new InvalidDataException("Binary directory exceeds the declared document length.");
    }

    internal static BinaryWireTypeDescriptor GetRootCatalogEntry<TContract>(
        ReadOnlySpan<BinaryWireTypeDescriptor> catalog,
        in BinaryDocumentHeader header)
        where TContract : IBinaryContract<TContract>
    {
        foreach (ref readonly BinaryWireTypeDescriptor descriptor in catalog)
        {
            if (descriptor.TypeId != TContract.TypeId)
                continue;
            if (descriptor.SchemaFingerprint != header.SchemaFingerprint
                || descriptor.Compatibility != header.Compatibility
                || descriptor.SchemaEpoch != header.SchemaEpoch)
            {
                throw new InvalidDataException("Root contract descriptor disagrees with the binary header.");
            }
            return descriptor;
        }

        throw new InvalidDataException(
            $"Binary type catalog does not contain root type id {TContract.TypeId}.");
    }

    private async ValueTask<BinaryChunkEntry> ReadEntryAsync(
        long index,
        CancellationToken cancellationToken)
    {
        if ((ulong)index >= _header.ChunkCount)
            throw new ArgumentOutOfRangeException(nameof(index));

        long offset = checked(
            _header.DirectoryOffset + index * BinaryDocumentFormat.DirectoryEntrySize);
        using RangeLease lease = await _source.AcquireAsync(
            offset,
            BinaryDocumentFormat.DirectoryEntrySize,
            cancellationToken).ConfigureAwait(false);
        EnsureSourceGeneration();
        return BinaryDocumentFormat.ReadEntry(lease.Memory.Span);
    }

    private void ValidateEntryBounds(in BinaryChunkEntry entry)
    {
        if (entry.Key == 0)
            throw new InvalidDataException("Binary chunk key zero is reserved.");
        if (entry.Alignment <= 0 || entry.Alignment > 1024 * 1024
            || (entry.Alignment & (entry.Alignment - 1)) != 0)
            throw new InvalidDataException($"Chunk 0x{entry.Key:X16} has invalid alignment {entry.Alignment}.");
        if ((entry.Offset & (entry.Alignment - 1L)) != 0)
            throw new InvalidDataException($"Chunk 0x{entry.Key:X16} offset is not aligned.");
        if (entry.StoredLength < 0 || entry.StoredLength > _limits.MaxStoredChunkBytes)
            throw new InvalidDataException($"Chunk 0x{entry.Key:X16} stored length is outside configured limits.");
        if (entry.DecodedLength < 0 || entry.DecodedLength > _limits.MaxDecodedChunkBytes)
            throw new InvalidDataException($"Chunk 0x{entry.Key:X16} decoded length is outside configured limits.");
        if (entry.StoredLength > int.MaxValue || entry.DecodedLength > int.MaxValue)
        {
            throw new InvalidDataException(
                $"Chunk 0x{entry.Key:X16} exceeds the contiguous .NET Memory limit and must be semantically subdivided.");
        }
        if (entry.Compression == ChunkCompression.None && entry.StoredLength != entry.DecodedLength)
            throw new InvalidDataException($"Uncompressed chunk 0x{entry.Key:X16} has unequal stored and decoded lengths.");
        if (entry.StoredLength == 0 && entry.DecodedLength != 0)
            throw new InvalidDataException($"Chunk 0x{entry.Key:X16} expands from an empty payload.");
        if (entry.StoredLength > 0
            && entry.DecodedLength > checked(entry.StoredLength * (long)_limits.MaxCompressionRatio))
        {
            throw new InvalidDataException(
                $"Chunk 0x{entry.Key:X16} exceeds maximum compression ratio {_limits.MaxCompressionRatio}:1.");
        }

        long payloadFloor = BinaryDocumentFormat.Align(
            checked(_header.DirectoryOffset + _header.DirectoryLength),
            16);
        if (entry.Offset < payloadFloor || entry.EndOffset > _header.TotalLength)
            throw new InvalidDataException($"Chunk 0x{entry.Key:X16} range lies outside the binary payload region.");
    }

    private async ValueTask ValidateEntryNeighborsAsync(
        long index,
        BinaryChunkEntry entry,
        CancellationToken cancellationToken)
    {
        if (index > 0)
        {
            BinaryChunkEntry previous = await ReadEntryAsync(index - 1, cancellationToken).ConfigureAwait(false);
            ValidateEntryBounds(previous);
            if (previous.Key >= entry.Key)
                throw new InvalidDataException("Binary directory keys are not strictly increasing.");
            if (previous.EndOffset > entry.Offset)
                throw new InvalidDataException(
                    $"Binary chunks 0x{previous.Key:X16} and 0x{entry.Key:X16} overlap.");
        }

        if (index + 1 < _header.ChunkCount)
        {
            BinaryChunkEntry next = await ReadEntryAsync(index + 1, cancellationToken).ConfigureAwait(false);
            ValidateEntryBounds(next);
            if (next.Key <= entry.Key)
                throw new InvalidDataException("Binary directory keys are not strictly increasing.");
            if (entry.EndOffset > next.Offset)
                throw new InvalidDataException(
                    $"Binary chunks 0x{entry.Key:X16} and 0x{next.Key:X16} overlap.");
        }
    }

    private void EnsureSourceGeneration()
    {
        if (!StringComparer.Ordinal.Equals(_sourceGeneration, _source.Generation))
            throw new IOException("Range source generation changed while the binary document was open.");
    }

    private static void ValidateChunkRef(in BinaryChunkRef chunk)
    {
        if (!chunk.IsValid)
            throw new ArgumentException("Binary chunk reference is invalid.", nameof(chunk));
    }

    internal static void VerifyHash(
        ReadOnlySpan<byte> bytes,
        in Digest256 expected,
        string description)
    {
        Digest256 actual = Digest256.ComputeSha256(bytes);
        if (!actual.FixedTimeEquals(expected))
            throw new InvalidDataException($"SHA-256 validation failed for {description}.");
    }
}

/// <summary>Bounded sequential view used to stream a stored chunk into a decoder.</summary>
internal sealed class RangeSourceReadStream(
    IRangeSource source,
    long offset,
    long length) : Stream
{
    private long _position;

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => length;
    public override long Position
    {
        get => _position;
        set => throw new NotSupportedException();
    }

    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        int count = checked((int)Math.Min(buffer.Length, length - _position));
        if (count == 0)
            return 0;
        await source.ReadExactlyAsync(
            checked(offset + _position),
            buffer[..count],
            cancellationToken).ConfigureAwait(false);
        _position = checked(_position + count);
        return count;
    }

    public override int Read(byte[] buffer, int bufferOffset, int count)
        => ReadAsync(buffer.AsMemory(bufferOffset, count)).AsTask().GetAwaiter().GetResult();

    public override void Flush()
    {
    }

    public override long Seek(long streamOffset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int bufferOffset, int count) => throw new NotSupportedException();
}

internal sealed class DocumentChunkRangeSource : IRangeSource
{
    private readonly IRangeSource _parent;
    private readonly BinaryChunkEntry _descriptor;
    private readonly DocumentLifetime _documentLifetime;
    private readonly string _parentGeneration;
    private readonly Guid _documentGeneration;
    private int _disposed;

    internal DocumentChunkRangeSource(
        IRangeSource parent,
        BinaryChunkEntry descriptor,
        Guid documentGeneration,
        DocumentLifetime documentLifetime)
    {
        _parent = parent;
        _descriptor = descriptor;
        _documentLifetime = documentLifetime;
        _parentGeneration = parent.Generation;
        _documentGeneration = documentGeneration;
    }

    public long Length => _descriptor.DecodedLength;
    public string Generation => $"{_parent.Generation}:document:{_documentGeneration:N}:chunk:{_descriptor.Key:X16}";
    public bool LeasesAreImmutable => _parent.LeasesAreImmutable;
    public bool RetainsResidentBacking => _parent.RetainsResidentBacking;

    public async ValueTask ReadExactlyAsync(
        long offset,
        Memory<byte> destination,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        EnsureParentGeneration();
        IO.RangeValidation.Validate(offset, destination.Length, Length);
        await _parent.ReadExactlyAsync(
            checked(_descriptor.Offset + offset),
            destination,
            cancellationToken).ConfigureAwait(false);
        EnsureParentGeneration();
    }

    public async ValueTask<RangeLease> AcquireAsync(
        long offset,
        int length,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        EnsureParentGeneration();
        IO.RangeValidation.Validate(offset, length, Length);
        RangeLease lease = await _parent.AcquireAsync(
            checked(_descriptor.Offset + offset),
            length,
            cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureParentGeneration();
            return lease;
        }
        catch
        {
            lease.Dispose();
            throw;
        }
    }

    public ValueTask DisposeAsync()
    {
        Interlocked.Exchange(ref _disposed, 1);
        return ValueTask.CompletedTask;
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        _documentLifetime.ThrowIfDisposed(this);
    }

    private void EnsureParentGeneration()
    {
        if (!StringComparer.Ordinal.Equals(_parentGeneration, _parent.Generation))
            throw new IOException("Parent range source generation changed while a child chunk source was open.");
    }
}
