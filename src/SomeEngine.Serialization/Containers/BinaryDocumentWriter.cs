using System.Buffers;
using System.IO.Compression;
using System.Security.Cryptography;
using SomeEngine.Serialization.IO;

namespace SomeEngine.Serialization.Containers;

/// <summary>
/// Writes a binary document to its final seekable stream with header/directory back-patching.
/// Borrowed root/chunk memory must remain stable until <see cref="WriteAsync"/> completes.
/// </summary>
public sealed class BinaryDocumentWriter
{
    private readonly IBinaryDocumentRoot _root;
    private readonly ulong _schemaFingerprint;
    private readonly BinaryCompatibility _compatibility;
    private readonly uint _schemaEpoch;
    private readonly BinaryWireTypeDescriptor _rootDescriptor;
    private readonly Dictionary<Guid, BinaryWireTypeDescriptor> _contracts = [];
    private readonly List<ChunkData> _chunks = [];

    private BinaryDocumentWriter(
        IBinaryDocumentRoot root,
        Guid rootTypeId,
        ulong schemaFingerprint,
        BinaryCompatibility compatibility,
        uint schemaEpoch)
    {
        ArgumentNullException.ThrowIfNull(root);
        if (rootTypeId == Guid.Empty)
            throw new ArgumentOutOfRangeException(nameof(rootTypeId));
        if (schemaFingerprint == 0)
            throw new ArgumentOutOfRangeException(nameof(schemaFingerprint));
        if (!Enum.IsDefined(compatibility))
            throw new ArgumentOutOfRangeException(nameof(compatibility));
        if (schemaEpoch == 0)
            throw new ArgumentOutOfRangeException(nameof(schemaEpoch));

        _root = root;
        _schemaFingerprint = schemaFingerprint;
        _compatibility = compatibility;
        _schemaEpoch = schemaEpoch;
        _rootDescriptor = new BinaryWireTypeDescriptor(
            rootTypeId, schemaFingerprint, compatibility, schemaEpoch);
        _contracts.Add(rootTypeId, _rootDescriptor);
    }

    public static BinaryDocumentWriter Create<T>(T root)
        where T : IBinarySerializable<T>
        => new(
            new ContractDocumentRoot<T>(root),
            T.TypeId,
            T.SchemaFingerprint,
            T.Compatibility,
            T.SchemaEpoch);

    public int ChunkCount => _chunks.Count;

    /// <summary>
    /// Adds an existing chunk without reading its payload. The source must remain open until
    /// writing completes; writing verifies and streams its stored bytes with bounded memory.
    /// </summary>
    public async ValueTask AddChunkAsync(ulong key, BinaryDocument source,
        CancellationToken cancellationToken = default, ulong? sourceKey = null, uint? ordinal = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        BinaryChunkEntry chunk = await source.FindChunkAsync(sourceKey ?? key, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Binary chunk 0x{key:X16} was not found.");
        if (_chunks.Any(value => value.Key == key))
            throw new InvalidOperationException($"Duplicate binary chunk key 0x{key:X16}.");
        _chunks.Add(new ChunkData(key, chunk.TypeFingerprint, default, chunk.Compression,
            chunk.Alignment, ordinal ?? chunk.Ordinal)
        {
            SourceEntry = chunk,
            SourceDocument = source,
        });
    }

    /// <summary>The exact root contract this builder will encode.</summary>
    public BinaryWireTypeDescriptor RootDescriptor => _rootDescriptor;

    public BinaryDocumentWriter AddContract<T>()
        where T : IBinarySerializable<T>
    {
        return AddContract(new BinaryWireTypeDescriptor(
            T.TypeId, T.SchemaFingerprint, T.Compatibility, T.SchemaEpoch));
    }

    public BinaryDocumentWriter AddContract(BinaryWireTypeDescriptor descriptor)
    {
        if (descriptor.TypeId == Guid.Empty || descriptor.SchemaFingerprint == 0 || descriptor.SchemaEpoch == 0)
            throw new ArgumentException("A wire type requires a type ID, schema digest and epoch.", nameof(descriptor));
        if (_contracts.TryGetValue(descriptor.TypeId, out BinaryWireTypeDescriptor existing)
            && existing != descriptor)
        {
            throw new InvalidOperationException($"Binary type id {descriptor.TypeId} has conflicting descriptors.");
        }
        _contracts[descriptor.TypeId] = descriptor;
        return this;
    }

    public BinaryDocumentWriter AddChunk(
        ulong key,
        ReadOnlyMemory<byte> decodedBytes,
        ulong typeFingerprint = 0,
        ChunkCompression compression = ChunkCompression.None,
        int alignment = 16,
        uint ordinal = 0)
        => AddChunk(key, new ReadOnlySequence<byte>(decodedBytes), typeFingerprint, compression, alignment, ordinal);

    /// <summary>
    /// Borrows immutable segments until writing completes. Hashing and compression consume the
    /// segments directly, using the same document format as continuous chunk memory.
    /// </summary>
    public BinaryDocumentWriter AddChunk(
        ulong key,
        ReadOnlySequence<byte> decodedBytes,
        ulong typeFingerprint = 0,
        ChunkCompression compression = ChunkCompression.None,
        int alignment = 16,
        uint ordinal = 0)
    {
        if (key == 0)
            throw new ArgumentOutOfRangeException(nameof(key), "Chunk key zero is reserved.");
        if (!Enum.IsDefined(compression))
            throw new ArgumentOutOfRangeException(nameof(compression));
        if (alignment <= 0 || alignment > 1024 * 1024 || (alignment & (alignment - 1)) != 0)
            throw new ArgumentOutOfRangeException(nameof(alignment), "Alignment must be a power of two up to 1 MiB.");
        if (_chunks.Any(chunk => chunk.Key == key))
            throw new InvalidOperationException($"Duplicate binary chunk key 0x{key:X16}.");

        _chunks.Add(new ChunkData(
            key, typeFingerprint, decodedBytes, compression, alignment, ordinal));
        return this;
    }

    /// <summary>Adds an owned array without copying; the array must remain immutable while writing.</summary>
    public BinaryDocumentWriter AddChunk(
        ulong key,
        byte[] decodedBytes,
        ulong typeFingerprint = 0,
        ChunkCompression compression = ChunkCompression.None,
        int alignment = 16,
        uint ordinal = 0)
    {
        ArgumentNullException.ThrowIfNull(decodedBytes);
        return AddChunk(
            key,
            (ReadOnlyMemory<byte>)decodedBytes,
            typeFingerprint,
            compression,
            alignment,
            ordinal);
    }

    public async ValueTask WriteAsync(
        Stream destination,
        Guid? generation = null,
        CancellationToken cancellationToken = default)
    {
        ValidateDestination(destination, generation);
        cancellationToken.ThrowIfCancellationRequested();
        _ = await WriteAsyncCore(destination, generation, cancellationToken).ConfigureAwait(false);
    }

    internal ValueTask<Digest256> WriteAuthenticatedAsync(
        Stream destination,
        Guid? generation = null,
        CancellationToken cancellationToken = default)
    {
        ValidateDestination(destination, generation);
        cancellationToken.ThrowIfCancellationRequested();
        return WriteAsyncCore(destination, generation, cancellationToken);
    }

    internal static void ValidateDestination(Stream destination, Guid? generation = null)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (!destination.CanWrite || !destination.CanSeek)
            throw new ArgumentException("Binary documents require a writable, seekable destination.", nameof(destination));
        if (destination.Position != 0)
            throw new ArgumentException("Binary documents must be written at stream position zero.", nameof(destination));
        if (generation == Guid.Empty)
            throw new ArgumentOutOfRangeException(nameof(generation));

    }

    private async ValueTask<Digest256> WriteAsyncCore(Stream destination, Guid? requestedGeneration, CancellationToken cancellationToken)
    {
        destination.SetLength(0);
        WriteZeroes(destination, BinaryDocumentFormat.HeaderSize);
        byte[] catalog = BinaryDocumentFormat.WriteTypeCatalog(_rootDescriptor, _contracts.Values);
        destination.Write(catalog);
        Digest256 catalogHash = Digest256.ComputeSha256(catalog);

        long rootOffset = BinaryDocumentFormat.Align(destination.Position, 16);
        WriteZeroes(destination, rootOffset - destination.Position);
        Digest256 rootHash;
        long rootLength;
        using (var rootWriter = new HashingBufferWriter(destination))
        {
            _root.WriteTo(rootWriter);
            rootHash = rootWriter.CompleteDigest();
            rootLength = rootWriter.WrittenCount;
        }
        cancellationToken.ThrowIfCancellationRequested();

        long directoryOffset = BinaryDocumentFormat.Align(destination.Position, 16);
        WriteZeroes(destination, directoryOffset - destination.Position);
        ChunkData[] ordered = _chunks.OrderBy(static chunk => chunk.Key).ToArray();
        long directoryLength = checked((long)ordered.Length * BinaryDocumentFormat.DirectoryEntrySize);
        WriteZeroes(destination, directoryLength);
        WriteZeroes(destination, BinaryDocumentFormat.Align(destination.Position, 16) - destination.Position);

        var prepared = new WrittenChunk[ordered.Length];
        for (int i = 0; i < ordered.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ChunkData source = ordered[i];
            long offset = BinaryDocumentFormat.Align(destination.Position, source.Alignment);
            WriteZeroes(destination, offset - destination.Position);
            prepared[i] = await WriteChunkAsync(destination, source, offset, cancellationToken).ConfigureAwait(false);
        }

        long totalLength = BinaryDocumentFormat.Align(destination.Position, 16);
        WriteZeroes(destination, totalLength - destination.Position);
        long end = destination.Position;
        destination.Position = directoryOffset;
        (Digest256 directoryHash, Guid derivedGeneration) = WriteDirectory(
            destination,
            catalogHash,
            rootHash,
            prepared,
            cancellationToken);
        Guid generation = requestedGeneration ?? derivedGeneration;
        var header = new BinaryDocumentHeader(
            _compatibility,
            _schemaEpoch,
            checked((uint)prepared.Length),
            _schemaFingerprint,
            generation,
            checked((uint)catalog.Length),
            rootOffset,
            rootLength,
            directoryOffset,
            directoryLength,
            totalLength,
            rootHash);

        Span<byte> headerBytes = stackalloc byte[BinaryDocumentFormat.HeaderSize];
        BinaryDocumentFormat.WriteHeader(headerBytes, header);
        Digest256 headerHash = Digest256.ComputeSha256(headerBytes);
        Digest256 authenticationDigest = BinaryDocumentFormat.ComputeAuthenticationDigest(
            headerHash,
            catalogHash,
            rootHash,
            directoryHash);
        cancellationToken.ThrowIfCancellationRequested();
        destination.Position = 0;
        destination.Write(headerBytes);
        destination.SetLength(totalLength);
        destination.Position = end;
        return authenticationDigest;
    }

    private static async ValueTask<WrittenChunk> WriteChunkAsync(
        Stream destination,
        ChunkData source,
        long offset,
        CancellationToken cancellationToken)
    {
        if (source.SourceEntry is { } entry)
        {
            Digest256 digest = await source.SourceDocument!.CopyStoredChunkAsync(entry, destination, cancellationToken).ConfigureAwait(false);
            return new WrittenChunk(entry with { Key = source.Key, Ordinal = source.Ordinal, Offset = offset }, digest);
        }
        Digest256 contentHash;
        Digest256 storedHash;
        long storedLength;
        if (source.Compression == ChunkCompression.None)
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            WriteDecodedBytes(destination, source.DecodedBytes, hash, cancellationToken);
            contentHash = Digest256.Finish(hash);
            storedHash = contentHash;
            storedLength = source.DecodedBytes.Length;
        }
        else
        {
            using var decodedHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            using var storedWriter = new HashingWriteStream(destination);
            using (var compressor = new BrotliStream(storedWriter, CompressionLevel.Optimal, leaveOpen: true))
            {
                WriteDecodedBytes(compressor, source.DecodedBytes, decodedHash, cancellationToken);
            }
            contentHash = Digest256.Finish(decodedHash);
            storedHash = storedWriter.CompleteDigest();
            storedLength = storedWriter.BytesWritten;
            if (storedLength > 0
                && source.DecodedBytes.Length > storedLength * BinaryReadLimits.Default.MaxCompressionRatio)
            {
                destination.Position = offset;
                destination.SetLength(offset);
                throw new InvalidOperationException(
                    $"Chunk 0x{source.Key:X16} exceeds the defensive compression ratio. " +
                    "Use ChunkCompression.None; implicit re-encoding is disabled.");
            }
        }

        var descriptor = new BinaryChunkEntry(
            source.Key,
            source.TypeFingerprint,
            offset,
            storedLength,
            source.DecodedBytes.Length,
            source.Alignment,
            source.Compression,
            contentHash,
            source.Ordinal);
        return new WrittenChunk(descriptor, storedHash);
    }

    private static void WriteDecodedBytes(Stream destination, ReadOnlySequence<byte> bytes,
        IncrementalHash hash, CancellationToken cancellationToken)
    {
        foreach (ReadOnlyMemory<byte> segment in bytes)
        {
            ReadOnlySpan<byte> remaining = segment.Span;
            while (!remaining.IsEmpty)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int length = Math.Min(64 * 1024, remaining.Length);
                ReadOnlySpan<byte> block = remaining[..length];
                destination.Write(block);
                hash.AppendData(block);
                remaining = remaining[length..];
            }
        }
    }

    private static (Digest256 DirectoryHash, Guid DerivedGeneration) WriteDirectory(
        Stream destination,
        in Digest256 catalogHash,
        in Digest256 rootHash,
        ReadOnlySpan<WrittenChunk> chunks,
        CancellationToken cancellationToken)
    {
        using var directoryHasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var generationHasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> digestBytes = stackalloc byte[Digest256.Size];
        catalogHash.Write(digestBytes);
        generationHasher.AppendData(digestBytes);
        rootHash.Write(digestBytes);
        generationHasher.AppendData(digestBytes);

        Span<byte> entryBytes = stackalloc byte[BinaryDocumentFormat.DirectoryEntrySize];
        foreach (ref readonly WrittenChunk chunk in chunks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            BinaryDocumentFormat.WriteEntry(entryBytes, chunk.Descriptor);
            destination.Write(entryBytes);
            directoryHasher.AppendData(entryBytes);
            generationHasher.AppendData(entryBytes);
            chunk.StoredHash.Write(digestBytes);
            generationHasher.AppendData(digestBytes);
        }

        Digest256 directoryHash = Digest256.Finish(directoryHasher);
        Digest256 generationDigest = Digest256.Finish(generationHasher);
        Span<byte> generationBytes = stackalloc byte[Digest256.Size];
        generationDigest.Write(generationBytes);
        return (directoryHash, new Guid(generationBytes[..16], bigEndian: true));
    }

    private static void WriteZeroes(Stream destination, long count)
    {
        if (count < 0)
            throw new InvalidOperationException("Binary document layout moved backwards.");
        Span<byte> zeroes = stackalloc byte[4096];
        zeroes.Clear();
        while (count > 0)
        {
            int length = (int)Math.Min(count, zeroes.Length);
            destination.Write(zeroes[..length]);
            count -= length;
        }
    }

    private sealed record ChunkData(
        ulong Key,
        ulong TypeFingerprint,
        ReadOnlySequence<byte> DecodedBytes,
        ChunkCompression Compression,
        int Alignment,
        uint Ordinal)
    {
        public BinaryChunkEntry? SourceEntry { get; init; }
        public BinaryDocument? SourceDocument { get; init; }
    }

    private readonly record struct WrittenChunk(
        BinaryChunkEntry Descriptor,
        Digest256 StoredHash);
}
