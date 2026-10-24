using System.Buffers;
using System.IO.Compression;
using System.Security.Cryptography;
using SomeEngine.Serialization.IO;
using SomeEngine.Serialization.Streaming;

namespace SomeEngine.Serialization.Containers;

/// <summary>Header inspection that does not require a root field type.</summary>
public partial class BinaryDocument
{
    public static async ValueTask<Guid> ReadVersionAsync(Stream stream,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanRead || !stream.CanSeek)
            throw new ArgumentException("Version inspection needs a readable, seekable stream.", nameof(stream));
        long position = stream.Position;
        byte[] bytes = new byte[BinaryDocumentFormat.HeaderSize];
        try
        {
            stream.Position = 0;
            await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
            BinaryDocumentHeader header = BinaryDocumentFormat.ReadHeader(bytes);
            if (header.TotalLength != stream.Length)
                throw new InvalidDataException("The binary document length does not match its stream.");
            return header.Generation;
        }
        finally
        {
            stream.Position = position;
        }
    }

    public static async ValueTask<Guid> ReadVersionAsync(IRangeSource source,
        CancellationToken cancellationToken = default)
    {
        return (await InspectAsync(source, cancellationToken: cancellationToken).ConfigureAwait(false)).Generation;
    }
}

internal sealed class DocumentBytes(IRangeSource source, bool ownsSource)
{
    private int _references = 1;
    internal void AddReference() => Interlocked.Increment(ref _references);
    internal ValueTask ReleaseAsync() => Interlocked.Decrement(ref _references) == 0 && ownsSource
        ? source.DisposeAsync()
        : ValueTask.CompletedTask;
}

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

    internal long Length => Memory.Length;

    public ReadOnlyMemory<byte> Memory
    {
        get
        {
            _documentLifetime.ThrowIfDisposed(this);
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
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

/// <summary>Typed root decoding over the common binary document byte access.</summary>
public sealed class BinaryDocument<T> : BinaryDocument where T : IBinarySerializable<T>
{
    private T _root = default!;
    private bool _hasRoot;

    internal BinaryDocument(IRangeSource source, bool ownsSource, BinaryDocumentHeader header,
        BinaryWireTypeDescriptor[] catalog, BinaryReadLimits limits, T root, byte[] rootBytes,
        Digest256 headerHash, Digest256 catalogHash)
        : base(source, ownsSource, header, catalog, limits, rootBytes, headerHash, catalogHash)
    {
        _root = root;
        _hasRoot = true;
    }

    private BinaryDocument(BinaryDocument<T> source) : base(source) { }
    internal BinaryDocument(BinaryDocument source, T root) : base(source) { _root = root; _hasRoot = true; }

    public T Root
    {
        get
        {
            lock (_closeGate)
            {
                if (!_hasRoot)
                {
                    _root = BinarySerializer.Deserialize<T>(_rootBytes, _limits);
                    _hasRoot = true;
                }
                return _root;
            }
        }
    }

    public BinaryDocument<T> Duplicate()
    {
        lock (_closeGate)
        {
            _lifetime.ThrowIfDisposed(this);
            return new(this);
        }
    }

    public static ValueTask<BinaryDocument<T>> OpenAsync(IRangeSource source, bool ownsSource = false,
        BinaryReadLimits? limits = null, CancellationToken cancellationToken = default)
        => OpenAsync<T>(source, ownsSource, limits, cancellationToken);

    public new static ValueTask<BinaryEnvelopeMetadata> InspectAsync(IRangeSource source,
        BinaryReadLimits? limits = null, CancellationToken cancellationToken = default)
        => InspectAsync<T>(source, limits, cancellationToken);
}

/// <summary>
/// Open binary document. Opening reads only the fixed header and small root; directory entries
/// and chunk payloads are fetched lazily with explicit range reads.
/// </summary>
public partial class BinaryDocument : IAsyncDisposable
{
    private readonly IRangeSource _source;
    private readonly DocumentBytes _bytes;
    private protected readonly object _closeGate = new();
    private readonly BinaryDocumentHeader _header;
    private protected readonly BinaryReadLimits _limits;
    private readonly string _sourceGeneration;
    private readonly BinaryWireTypeDescriptor[] _typeCatalog;
    private readonly Digest256 _headerHash;
    private readonly Digest256 _catalogHash;
    private protected readonly byte[] _rootBytes;
    private protected readonly DocumentLifetime _lifetime = new();

    private protected BinaryDocument(
        IRangeSource source,
        bool ownsSource,
        BinaryDocumentHeader header,
        BinaryWireTypeDescriptor[] typeCatalog,
        BinaryReadLimits limits,
        byte[] rootBytes,
        Digest256 headerHash,
        Digest256 catalogHash)
    {
        _source = source;
        _bytes = new DocumentBytes(source, ownsSource);
        _header = header;
        _typeCatalog = typeCatalog;
        _limits = limits;
        _sourceGeneration = source.Generation;
        _headerHash = headerHash;
        _catalogHash = catalogHash;
        _rootBytes = rootBytes;
    }

    private protected BinaryDocument(BinaryDocument source)
    {
        _source = source._source;
        _bytes = source._bytes;
        _header = source._header;
        _limits = source._limits;
        _sourceGeneration = source._sourceGeneration;
        _typeCatalog = source._typeCatalog;
        _headerHash = source._headerHash;
        _catalogHash = source._catalogHash;
        _rootBytes = source._rootBytes;
        _bytes.AddReference();
    }

    /// <summary>Retains the validated byte source without retaining or decoding asset fields.</summary>
    public BinaryDocument Retain()
    {
        lock (_closeGate)
        {
            _lifetime.ThrowIfDisposed(this);
            return new BinaryDocument(this);
        }
    }

    public Guid Generation => _header.Generation;
    public uint ChunkCount => _header.ChunkCount;
    public ulong SchemaFingerprint => _header.SchemaFingerprint;
    public long TotalLength => _header.TotalLength;
    public long RootByteLength => _header.RootLength;
    public string SourceGeneration => _sourceGeneration;
    public bool RetainsResidentBacking => _source.RetainsResidentBacking;
    public bool SourceLeasesAreImmutable => _source.LeasesAreImmutable;
    public IReadOnlyList<BinaryWireTypeDescriptor> TypeCatalog => _typeCatalog;
    public BinaryWireTypeDescriptor RootType => _typeCatalog[0];

    /// <summary>
    /// Reads only the fixed header and type catalog, validates their integrity, and returns the
    /// encoded root schema metadata for this logical <typeparamref name="T"/>. Root and chunk bytes
    /// are not read.
    /// </summary>
    protected static async ValueTask<BinaryEnvelopeMetadata> InspectAsync<T>(
        IRangeSource source,
        BinaryReadLimits? limits = null,
        CancellationToken cancellationToken = default) where T : IBinarySerializable<T>
    {
        var content = await ReadHeaderAsync(source,
            new(T.TypeId, T.SchemaFingerprint, T.Compatibility, T.SchemaEpoch), limits, cancellationToken).ConfigureAwait(false);
        return DescribeRoot(content.Header, content.Types[0]);
    }

    /// <summary>Reads the root description without a business codec. Signed-package authentication still applies.</summary>
    public static async ValueTask<BinaryEnvelopeMetadata> InspectAsync(
        IRangeSource source, BinaryReadLimits? limits = null, CancellationToken cancellationToken = default)
    {
        var content = await ReadHeaderAsync(source, null, limits, cancellationToken).ConfigureAwait(false);
        return DescribeRoot(content.Header, content.Types[0]);
    }

    private static BinaryEnvelopeMetadata DescribeRoot(BinaryDocumentHeader header, BinaryWireTypeDescriptor root)
        => new(root.TypeId, header.SchemaFingerprint, header.Compatibility, header.SchemaEpoch, header.RootLength)
        { Generation = header.Generation };

    private static async ValueTask<(BinaryDocumentHeader Header, BinaryWireTypeDescriptor[] Types,
        BinaryReadLimits Limits, Digest256 HeaderHash, Digest256 CatalogHash, string SourceGeneration)> ReadHeaderAsync(
        IRangeSource source, BinaryWireTypeDescriptor? expectedType, BinaryReadLimits? limits,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        cancellationToken.ThrowIfCancellationRequested();
        if (source.Length < BinaryDocumentFormat.HeaderSize)
            throw new InvalidDataException("The binary document is shorter than its fixed header.");
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
        ValidateRootType(expectedType ?? typeCatalog[0], typeCatalog, header);
        if (!StringComparer.Ordinal.Equals(openingGeneration, source.Generation))
            throw new IOException("Range source generation changed while inspecting an binary document.");

        Digest256 headerHash = Digest256.ComputeSha256(headerLease.Memory.Span);
        Digest256 catalogHash = Digest256.ComputeSha256(catalogLease.Memory.Span);
        if (source is IBinaryDocumentReceipt receipt)
        {
            Digest256 digest = await ComputeAuthenticationDigestAsync(source, header, headerHash,
                catalogHash, openingGeneration, cancellationToken).ConfigureAwait(false);
            receipt.Validate(digest);
        }
        return (header, typeCatalog, effectiveLimits, headerHash, catalogHash, openingGeneration);
    }

    public static async ValueTask<BinaryDocument> OpenAsync(
        IRangeSource source, BinaryWireTypeDescriptor rootType,
        bool ownsSource = false,
        BinaryReadLimits? limits = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        try
        {
            var content = await ReadContentAsync(source, rootType, limits, cancellationToken).ConfigureAwait(false);
            using RangeLease root = content.Root;
            return new BinaryDocument(source, ownsSource, content.Header, content.Types, content.Limits,
                root.Memory.ToArray(), content.HeaderHash, content.CatalogHash);
        }
        catch
        {
            if (ownsSource)
                await source.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    // Both use modes authenticate the same encoded content before interpreting the root.
    // The caller either copies the root bytes or transfers the existing range into a view.
    internal static async ValueTask<(BinaryDocumentHeader Header, BinaryWireTypeDescriptor[] Types,
        BinaryReadLimits Limits, RangeLease Root, Digest256 HeaderHash, Digest256 CatalogHash,
        string SourceGeneration)> ReadContentAsync(
        IRangeSource source, BinaryWireTypeDescriptor rootType, BinaryReadLimits? limits,
        CancellationToken cancellationToken)
    {
        var content = await ReadHeaderAsync(source, rootType, limits, cancellationToken).ConfigureAwait(false);
        RangeLease? root = null;
        try
        {
            root = await source.AcquireAsync(
                content.Header.RootOffset,
                checked((int)content.Header.RootLength),
                cancellationToken).ConfigureAwait(false);
            VerifyHash(root.Memory.Span, content.Header.RootHash, "binary document root");

            if (!StringComparer.Ordinal.Equals(content.SourceGeneration, source.Generation))
                throw new IOException("Range source generation changed while opening a binary document.");

            return (content.Header, content.Types, content.Limits, root, content.HeaderHash,
                content.CatalogHash, content.SourceGeneration);
        }
        catch
        {
            root?.Dispose();
            throw;
        }
    }

    protected static async ValueTask<BinaryDocument<T>> OpenAsync<T>(IRangeSource source,
        bool ownsSource = false, BinaryReadLimits? limits = null, CancellationToken cancellationToken = default)
        where T : IBinarySerializable<T>
    {
        await using BinaryDocument document = await OpenAsync(source,
            new(T.TypeId, T.SchemaFingerprint, T.Compatibility, T.SchemaEpoch), ownsSource, limits, cancellationToken).ConfigureAwait(false);
        return new BinaryDocument<T>(document, document.ReadRoot<T>());
    }

    /// <summary>Explicitly decodes the retained root bytes without reading from the source.</summary>
    public T ReadRoot<T>() where T : IBinarySerializable<T>
    {
        _lifetime.ThrowIfDisposed(this);
        ValidateHeader<T>(_header, _source.Length, _limits);
        _ = GetRootCatalogEntry<T>(_typeCatalog, _header);
        return BinarySerializer.Deserialize<T>(_rootBytes, _limits);
    }

    /// <summary>Returns the validated encoded root for an explicit parser; use ends with this document access.</summary>
    public ReadOnlySpan<byte> GetRootBytes()
    {
        _lifetime.ThrowIfDisposed(this);
        return _rootBytes;
    }

    public BinaryDataReader CreateRootReader()
    {
        _lifetime.ThrowIfDisposed(this);
        return new BinaryDataReader(_rootBytes, _limits);
    }

    /// <summary>Decodes the retained root into caller-selected storage without source I/O.</summary>
    public void ReadRootInto<T>(T target) where T : class, IBinarySerializable<T>, IBinaryObjectReader<T>
    {
        _lifetime.ThrowIfDisposed(this);
        ValidateHeader<T>(_header, _source.Length, _limits);
        _ = GetRootCatalogEntry<T>(_typeCatalog, _header);
        BinarySerializer.DeserializeInto(_rootBytes, target, _limits);
    }

    private static void ValidateRootType(BinaryWireTypeDescriptor expected,
        BinaryWireTypeDescriptor[] catalog, BinaryDocumentHeader header)
    {
        if (header.SchemaFingerprint != expected.SchemaFingerprint || header.SchemaEpoch != expected.SchemaEpoch ||
            header.Compatibility != expected.Compatibility)
            throw new BinarySchemaMismatchException(typeof(BinaryDocument), header.SchemaFingerprint,
                expected.SchemaFingerprint, header.SchemaEpoch, expected.SchemaEpoch, expected.Compatibility);
        BinaryWireTypeDescriptor root = catalog[0];
        if (root.TypeId != expected.TypeId)
            throw SerializationErrors.CreateDataException(
                SerializationError.SchemaMismatch,
                "The file has a different root type ID; another catalog entry cannot be used as its root.");
        if (root != expected)
            throw new InvalidDataException("The root descriptor differs from its file header.");
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

    /// <summary>Explicitly copies one chunk into independent writable memory, including from a resident source.</summary>
    public async ValueTask<Memory<byte>> CopyChunkAsync(BinaryChunkRef chunk,
        CancellationToken cancellationToken = default)
    {
        BinaryChunkEntry descriptor = await FindRequiredChunkAsync(chunk, cancellationToken).ConfigureAwait(false);
        return await ReadChunkAsync(descriptor, static length => GC.AllocateUninitializedArray<byte>(length),
            cancellationToken).ConfigureAwait(false);
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
        if (descriptor.Compression != ChunkCompression.None || !_source.LeasesAreImmutable)
        {
            IMemoryOwner<byte>? decodedOwner = MemoryPool<byte>.Shared.Rent(
                Math.Max(1, checked((int)descriptor.DecodedLength)));
            try
            {
                Memory<byte> decoded = decodedOwner.Memory[..checked((int)descriptor.DecodedLength)];
                if (descriptor.Compression == ChunkCompression.None)
                    await _source.ReadExactlyAsync(descriptor.Offset, decoded, cancellationToken).ConfigureAwait(false);
                else
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

    internal async ValueTask<Digest256> CopyStoredChunkAsync(
        BinaryChunkEntry chunk, Stream destination, CancellationToken cancellationToken)
    {
        _lifetime.ThrowIfDisposed(this);
        EnsureSourceGeneration();
        if (chunk.Compression != ChunkCompression.None &&
            !await VerifyChunkContentAsync(chunk, cancellationToken).ConfigureAwait(false))
            throw SerializationErrors.CreateDataException(
                SerializationError.IntegrityMismatch,
                $"Binary chunk 0x{chunk.Key:X16} content hash mismatch.");

        byte[] buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        try
        {
            long copied = 0;
            while (copied < chunk.StoredLength)
            {
                int length = (int)Math.Min(buffer.Length, chunk.StoredLength - copied);
                await _source.ReadExactlyAsync(chunk.Offset + copied, buffer.AsMemory(0, length),
                    cancellationToken).ConfigureAwait(false);
                hash.AppendData(buffer.AsSpan(0, length));
                await destination.WriteAsync(buffer.AsMemory(0, length), cancellationToken).ConfigureAwait(false);
                copied += length;
            }
            EnsureSourceGeneration();
            Digest256 digest = Digest256.Finish(hash);
            if (chunk.Compression == ChunkCompression.None && digest != chunk.ContentHash)
                throw SerializationErrors.CreateDataException(
                SerializationError.IntegrityMismatch,
                $"Binary chunk 0x{chunk.Key:X16} content hash mismatch.");
            return digest;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
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

    internal ValueTask<Digest256> ComputeAuthenticationDigestAsync(
        CancellationToken cancellationToken = default)
    {
        _lifetime.ThrowIfDisposed(this);
        return ComputeAuthenticationDigestAsync(_source, _header, _headerHash, _catalogHash,
            _sourceGeneration, cancellationToken);
    }

    private static async ValueTask<Digest256> ComputeAuthenticationDigestAsync(
        IRangeSource source, BinaryDocumentHeader header, Digest256 headerHash, Digest256 catalogHash,
        string sourceGeneration, CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        const int rangeBytes = 64 * 1024;
        long consumed = 0;
        while (consumed < header.DirectoryLength)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int length = checked((int)Math.Min(rangeBytes, header.DirectoryLength - consumed));
            using RangeLease range = await source.AcquireAsync(
                checked(header.DirectoryOffset + consumed),
                length,
                cancellationToken).ConfigureAwait(false);
            if (!StringComparer.Ordinal.Equals(sourceGeneration, source.Generation))
                throw new IOException("Range source generation changed while authenticating a binary document.");
            hash.AppendData(range.Memory.Span);
            consumed = checked(consumed + length);
        }

        Digest256 directoryHash = Digest256.Finish(hash);
        return BinaryDocumentFormat.ComputeAuthenticationDigest(
            headerHash,
            catalogHash,
            header.RootHash,
            directoryHash);
    }

    /// <summary>Copies the original file using bounded buffers and verifies every chunk before succeeding.</summary>
    public async ValueTask CopyToAsync(Stream destination, CancellationToken cancellationToken = default)
    {
        BinaryDocumentWriter.ValidateDestination(destination);
        _lifetime.ThrowIfDisposed(this);
        EnsureSourceGeneration();
        byte[] buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            long offset = 0;
            while (offset < _header.TotalLength)
            {
                int length = (int)Math.Min(buffer.Length, _header.TotalLength - offset);
                await _source.ReadExactlyAsync(offset, buffer.AsMemory(0, length), cancellationToken).ConfigureAwait(false);
                await destination.WriteAsync(buffer.AsMemory(0, length), cancellationToken).ConfigureAwait(false);
                offset += length;
            }
            destination.SetLength(_header.TotalLength);
            await foreach (BinaryChunkEntry chunk in EnumerateChunksAsync(cancellationToken).ConfigureAwait(false))
                if (!await VerifyChunkContentAsync(chunk, cancellationToken).ConfigureAwait(false))
                    throw new InvalidDataException($"Cannot copy corrupted binary chunk 0x{chunk.Key:X16}.");
            EnsureSourceGeneration();
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }

    public ValueTask DisposeAsync()
    {
        lock (_closeGate)
        {
            if (_lifetime.IsDisposed)
                return ValueTask.CompletedTask;
            _lifetime.Dispose();
        }
        return _bytes.ReleaseAsync();
    }

    /// <summary>Enumerates and validates the on-disk chunk directory without reading payloads.</summary>
    public async IAsyncEnumerable<BinaryChunkEntry> EnumerateChunksAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        _lifetime.ThrowIfDisposed(this);
        for (uint index = 0; index < _header.ChunkCount; index++)
        {
            BinaryChunkEntry entry = await ReadEntryAsync(index, cancellationToken).ConfigureAwait(false);
            ValidateEntryBounds(entry);
            yield return await FindChunkAsync(entry.Key, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidDataException("Chunk directory changed during enumeration.");
        }
    }

    internal static void ValidateHeader<TContract>(
        in BinaryDocumentHeader header,
        long sourceLength,
        BinaryReadLimits limits)
        where TContract : IBinarySerializable<TContract>
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
            throw SerializationErrors.CreateDataException(
                SerializationError.SchemaMismatch,
                "Binary document schema fingerprint cannot be zero.");
        if (header.SchemaEpoch == 0)
            throw new InvalidDataException("Binary document schema epoch cannot be zero.");
        if (header.TotalLength != sourceLength)
            throw new InvalidDataException(
                $"Binary document declares length {header.TotalLength}, source length is {sourceLength}.");
        if (header.CatalogLength < 4 + BinaryDocumentFormat.HashSize
            || header.CatalogLength > limits.MaxTypeCatalogBytes)
            throw SerializationErrors.CreateDataException(
                SerializationError.LimitExceeded,
                $"Binary type catalog length {header.CatalogLength} exceeds configured limits.");
        if (header.RootLength < 0 || header.RootLength > limits.MaxRootBytes)
            throw SerializationErrors.CreateDataException(
                SerializationError.LimitExceeded,
                $"Binary root length {header.RootLength} exceeds configured limit {limits.MaxRootBytes}.");
        if (header.ChunkCount > limits.MaxChunkCount)
            throw SerializationErrors.CreateDataException(
                SerializationError.LimitExceeded,
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
        where TContract : IBinarySerializable<TContract>
    {
        if (catalog.IsEmpty || catalog[0].TypeId != TContract.TypeId)
            throw new InvalidDataException($"Binary document root type ID is not {TContract.TypeId}.");
        BinaryWireTypeDescriptor descriptor = catalog[0];
        if (descriptor.SchemaFingerprint != header.SchemaFingerprint
            || descriptor.Compatibility != header.Compatibility
            || descriptor.SchemaEpoch != header.SchemaEpoch)
        {
            throw new InvalidDataException("Root contract descriptor disagrees with the binary header.");
        }
        return descriptor;
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
            throw SerializationErrors.CreateDataException(
                SerializationError.LimitExceeded,
                $"Chunk 0x{entry.Key:X16} stored length is outside configured limits.");
        if (entry.DecodedLength < 0 || entry.DecodedLength > _limits.MaxDecodedChunkBytes)
            throw SerializationErrors.CreateDataException(
                SerializationError.LimitExceeded,
                $"Chunk 0x{entry.Key:X16} decoded length is outside configured limits.");
        if (entry.StoredLength > int.MaxValue || entry.DecodedLength > int.MaxValue)
        {
            throw SerializationErrors.CreateDataException(
                SerializationError.LimitExceeded,
                $"Chunk 0x{entry.Key:X16} exceeds the contiguous .NET Memory limit and must be semantically subdivided.");
        }
        if (entry.Compression == ChunkCompression.None && entry.StoredLength != entry.DecodedLength)
            throw new InvalidDataException($"Uncompressed chunk 0x{entry.Key:X16} has unequal stored and decoded lengths.");
        if (entry.StoredLength == 0 && entry.DecodedLength != 0)
            throw new InvalidDataException($"Chunk 0x{entry.Key:X16} expands from an empty payload.");
        if (entry.StoredLength > 0
            && entry.DecodedLength > checked(entry.StoredLength * (long)_limits.MaxCompressionRatio))
        {
            throw SerializationErrors.CreateDataException(
                SerializationError.LimitExceeded,
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
            throw SerializationErrors.CreateDataException(
                SerializationError.IntegrityMismatch,
                $"SHA-256 validation failed for {description}.");
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
