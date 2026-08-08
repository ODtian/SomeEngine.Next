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
    private readonly List<PendingChunk> _chunks = [];

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
        where T : IBinaryContract<T>
        => new(
            new ContractDocumentRoot<T>(root),
            T.TypeId,
            T.SchemaFingerprint,
            T.Compatibility,
            T.SchemaEpoch);

    public int ChunkCount => _chunks.Count;

    /// <summary>The exact root contract this builder will encode.</summary>
    public BinaryWireTypeDescriptor RootDescriptor => _rootDescriptor;

    public BinaryDocumentWriter AddContract<T>()
        where T : IBinaryContract<T>
    {
        var descriptor = new BinaryWireTypeDescriptor(
            T.TypeId, T.SchemaFingerprint, T.Compatibility, T.SchemaEpoch);
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
    {
        if (key == 0)
            throw new ArgumentOutOfRangeException(nameof(key), "Chunk key zero is reserved.");
        if (!Enum.IsDefined(compression))
            throw new ArgumentOutOfRangeException(nameof(compression));
        if (alignment <= 0 || alignment > 1024 * 1024 || (alignment & (alignment - 1)) != 0)
            throw new ArgumentOutOfRangeException(nameof(alignment), "Alignment must be a power of two up to 1 MiB.");
        if (_chunks.Any(chunk => chunk.Key == key))
            throw new InvalidOperationException($"Duplicate binary chunk key 0x{key:X16}.");

        _chunks.Add(new PendingChunk(
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

    public ValueTask WriteAsync(
        FileStream destination,
        Guid? generation = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (!destination.CanWrite || !destination.CanSeek)
            throw new ArgumentException("Binary documents require a writable, seekable destination.", nameof(destination));
        if (destination.Position != 0)
            throw new ArgumentException("Binary documents must be written at stream position zero.", nameof(destination));
        if (generation == Guid.Empty)
            throw new ArgumentOutOfRangeException(nameof(generation));

        cancellationToken.ThrowIfCancellationRequested();
        _ = Write(destination, generation, cancellationToken);
        return ValueTask.CompletedTask;
    }

    internal ValueTask<Digest256> WriteAuthenticatedAsync(
        FileStream destination,
        Guid? generation = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (!destination.CanWrite || !destination.CanSeek)
            throw new ArgumentException("Binary documents require a writable, seekable destination.", nameof(destination));
        if (destination.Position != 0)
            throw new ArgumentException("Binary documents must be written at stream position zero.", nameof(destination));
        if (generation == Guid.Empty)
            throw new ArgumentOutOfRangeException(nameof(generation));

        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Write(destination, generation, cancellationToken));
    }

    private Digest256 Write(FileStream destination, Guid? requestedGeneration, CancellationToken cancellationToken)
    {
        destination.SetLength(0);
        WriteZeroes(destination, BinaryDocumentFormat.HeaderSize);
        byte[] catalog = BinaryDocumentFormat.WriteTypeCatalog(_contracts.Values);
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

        long directoryOffset = BinaryDocumentFormat.Align(destination.Position, 16);
        WriteZeroes(destination, directoryOffset - destination.Position);
        PendingChunk[] ordered = _chunks.OrderBy(static chunk => chunk.Key).ToArray();
        long directoryLength = checked((long)ordered.Length * BinaryDocumentFormat.DirectoryEntrySize);
        WriteZeroes(destination, directoryLength);
        WriteZeroes(destination, BinaryDocumentFormat.Align(destination.Position, 16) - destination.Position);

        var prepared = new PreparedChunk[ordered.Length];
        for (int i = 0; i < ordered.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            PendingChunk source = ordered[i];
            long offset = BinaryDocumentFormat.Align(destination.Position, source.Alignment);
            WriteZeroes(destination, offset - destination.Position);
            prepared[i] = WriteChunk(destination, source, offset, cancellationToken);
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
        destination.Position = 0;
        destination.Write(headerBytes);
        destination.SetLength(totalLength);
        destination.Position = end;
        return authenticationDigest;
    }

    private static PreparedChunk WriteChunk(
        Stream destination,
        PendingChunk source,
        long offset,
        CancellationToken cancellationToken)
    {
        Digest256 contentHash;
        Digest256 storedHash;
        long storedLength;
        if (source.Compression == ChunkCompression.None)
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            ReadOnlyMemory<byte> remaining = source.DecodedBytes;
            while (!remaining.IsEmpty)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int length = Math.Min(64 * 1024, remaining.Length);
                ReadOnlySpan<byte> block = remaining.Span[..length];
                destination.Write(block);
                hash.AppendData(block);
                remaining = remaining[length..];
            }
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
                ReadOnlyMemory<byte> remaining = source.DecodedBytes;
                while (!remaining.IsEmpty)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    int length = Math.Min(64 * 1024, remaining.Length);
                    ReadOnlySpan<byte> block = remaining.Span[..length];
                    decodedHash.AppendData(block);
                    compressor.Write(block);
                    remaining = remaining[length..];
                }
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
        return new PreparedChunk(descriptor, storedHash);
    }

    private static (Digest256 DirectoryHash, Guid DerivedGeneration) WriteDirectory(
        Stream destination,
        in Digest256 catalogHash,
        in Digest256 rootHash,
        ReadOnlySpan<PreparedChunk> chunks,
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
        foreach (ref readonly PreparedChunk chunk in chunks)
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

    private sealed record PendingChunk(
        ulong Key,
        ulong TypeFingerprint,
        ReadOnlyMemory<byte> DecodedBytes,
        ChunkCompression Compression,
        int Alignment,
        uint Ordinal);

    private readonly record struct PreparedChunk(
        BinaryChunkEntry Descriptor,
        Digest256 StoredHash);
}
