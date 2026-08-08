using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using SomeEngine.Serialization;
using SomeEngine.Serialization.Containers;
using SomeEngine.Serialization.IO;

namespace SomeEngine.Serialization.Packs;

public sealed record AssetPackEntry(
    Guid AssetId,
    string AssetType,
    ulong ChunkKey,
    ulong SchemaFingerprint)
{
    internal Digest256 DocumentAuthenticationDigest { get; init; }
}

internal sealed class AssetPackIndex : IBinaryContract<AssetPackIndex>
{
    internal const ulong Fingerprint = 0x63EA1D127C5B449BUL;
    private static readonly Guid StableTypeId = new("bdb11d99-34cb-52ef-c55c-85bf52ac89db");

    internal AssetPackIndex(int capacity = 0)
    {
        Entries = new List<AssetPackEntry>(capacity);
    }

    public List<AssetPackEntry> Entries { get; }

    public static Guid TypeId => StableTypeId;
    public static ulong SchemaFingerprint => Fingerprint;
    public static BinaryCompatibility Compatibility => BinaryCompatibility.ExactSchema;
    public static uint SchemaEpoch => 1;

    public static void Write(ref BinaryDataWriter writer, AssetPackIndex value)
    {
        writer.WriteInt32(value.Entries.Count);
        Span<byte> authenticationDigest = stackalloc byte[Digest256.Size];
        foreach (AssetPackEntry entry in value.Entries)
        {
            writer.WriteGuid(entry.AssetId);
            writer.WriteString(entry.AssetType);
            writer.WriteUInt64(entry.ChunkKey);
            writer.WriteUInt64(entry.SchemaFingerprint);
            entry.DocumentAuthenticationDigest.Write(authenticationDigest);
            writer.WriteBytes(authenticationDigest);
        }
    }

    public static AssetPackIndex Read(ref BinaryDataReader reader)
    {
        reader.EnterObject();
        try
        {
            int count = reader.ReadCollectionCount(
                "asset pack catalog",
                elementAllocationBytes: 256,
                fixedAllocationBytes: 256);
            var result = new AssetPackIndex(count);
            var assetIds = new HashSet<Guid>(count);
            var chunkKeys = new HashSet<ulong>(count);
            for (int i = 0; i < count; i++)
            {
                Guid assetId = reader.ReadGuid();
                string assetType = reader.ReadString()
                    ?? throw new InvalidDataException("Asset pack entry type cannot be null.");
                ulong chunkKey = reader.ReadUInt64();
                ulong schemaFingerprint = reader.ReadUInt64();
                Digest256 authenticationDigest = Digest256.Read(
                    reader.ReadBytes(Digest256.Size));
                if (assetId == Guid.Empty)
                    throw new InvalidDataException("Asset pack entry has an empty asset id.");
                if (string.IsNullOrWhiteSpace(assetType))
                    throw new InvalidDataException("Asset pack entry has an empty asset type.");
                if (chunkKey == 0)
                    throw new InvalidDataException("Asset pack entry has the reserved chunk key zero.");
                if (authenticationDigest.IsZero)
                    throw new InvalidDataException("Asset pack entry has no document authentication digest.");
                if (!assetIds.Add(assetId))
                    throw new InvalidDataException($"Asset pack catalog contains duplicate asset id {assetId}.");
                if (!chunkKeys.Add(chunkKey))
                    throw new InvalidDataException($"Asset pack catalog contains duplicate chunk key 0x{chunkKey:X16}.");

                result.Entries.Add(new AssetPackEntry(assetId, assetType, chunkKey, schemaFingerprint)
                {
                    DocumentAuthenticationDigest = authenticationDigest,
                });
            }

            return result;
        }
        finally
        {
            reader.ExitObject();
        }
    }
}

internal readonly record struct AssetPackFooter(
    long BodyLength,
    byte SignatureAlgorithm,
    byte[] Signature);

internal static class AssetPackFooterFormat
{
    internal const byte RsaPkcs1Sha256 = 1;
    internal const int TrailerSize = 32;
    private const uint CurrentVersion = 2;
    private const int MaximumSignatureBytes = 16 * 1024;

    internal static void Write(Stream destination, long bodyLength, ReadOnlySpan<byte> signature)
    {
        if (destination.Position != bodyLength || destination.Length != bodyLength)
            throw new InvalidOperationException("Asset pack footer must immediately follow its authenticated body.");
        if (signature.Length > MaximumSignatureBytes)
            throw new ArgumentOutOfRangeException(nameof(signature));

        byte algorithm = signature.IsEmpty ? (byte)0 : RsaPkcs1Sha256;
        destination.Write(signature);
        Span<byte> trailer = stackalloc byte[TrailerSize];
        trailer.Clear();
        "SEPACK02"u8.CopyTo(trailer);
        BinaryPrimitives.WriteUInt32LittleEndian(trailer[8..], CurrentVersion);
        trailer[12] = algorithm;
        BinaryPrimitives.WriteInt32LittleEndian(trailer[16..], signature.Length);
        BinaryPrimitives.WriteInt64LittleEndian(trailer[20..], bodyLength);
        BinaryPrimitives.WriteUInt32LittleEndian(trailer[28..], Checksum(trailer[..28]));
        destination.Write(trailer);
    }

    internal static async ValueTask<AssetPackFooter> ReadAsync(
        IRangeSource source,
        CancellationToken cancellationToken)
    {
        if (source.Length < TrailerSize)
            throw new InvalidDataException("Asset pack is missing its mandatory current footer.");
        using RangeLease trailerLease = await source.AcquireAsync(
            source.Length - TrailerSize,
            TrailerSize,
            cancellationToken).ConfigureAwait(false);
        ReadOnlySpan<byte> trailer = trailerLease.Memory.Span;
        if (!trailer[..8].SequenceEqual("SEPACK02"u8)
            || BinaryPrimitives.ReadUInt32LittleEndian(trailer[8..]) != CurrentVersion)
        {
            throw new InvalidDataException("Asset pack footer is not the current schema.");
        }
        if (trailer[13] != 0 || trailer[14] != 0 || trailer[15] != 0)
            throw new InvalidDataException("Asset pack footer reserved bytes are non-zero.");
        uint expectedChecksum = BinaryPrimitives.ReadUInt32LittleEndian(trailer[28..]);
        if (Checksum(trailer[..28]) != expectedChecksum)
            throw new InvalidDataException("Asset pack footer checksum is invalid.");

        byte algorithm = trailer[12];
        int signatureLength = BinaryPrimitives.ReadInt32LittleEndian(trailer[16..]);
        long bodyLength = BinaryPrimitives.ReadInt64LittleEndian(trailer[20..]);
        if (signatureLength < 0 || signatureLength > MaximumSignatureBytes)
            throw new InvalidDataException("Asset pack footer signature length is invalid.");
        if ((algorithm == 0) != (signatureLength == 0)
            || (algorithm != 0 && algorithm != RsaPkcs1Sha256))
        {
            throw new InvalidDataException("Asset pack footer signature algorithm is invalid.");
        }
        long expectedLength;
        try
        {
            expectedLength = checked(bodyLength + signatureLength + TrailerSize);
        }
        catch (OverflowException exception)
        {
            throw new InvalidDataException("Asset pack footer lengths overflowed.", exception);
        }
        if (bodyLength <= 0 || expectedLength != source.Length)
            throw new InvalidDataException("Asset pack footer body length is invalid.");

        byte[] signature = signatureLength == 0
            ? []
            : GC.AllocateUninitializedArray<byte>(signatureLength);
        if (signature.Length != 0)
        {
            await source.ReadExactlyAsync(
                bodyLength,
                signature,
                cancellationToken).ConfigureAwait(false);
        }
        return new AssetPackFooter(bodyLength, algorithm, signature);
    }

    private static uint Checksum(ReadOnlySpan<byte> value)
    {
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(value, hash);
        return BinaryPrimitives.ReadUInt32LittleEndian(hash);
    }
}

internal sealed class AssetPackBodyRangeSource : IRangeSource
{
    private readonly IRangeSource _source;
    private readonly bool _ownsSource;
    private int _disposed;

    internal AssetPackBodyRangeSource(IRangeSource source, long length, bool ownsSource)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (length <= 0 || length > source.Length)
            throw new ArgumentOutOfRangeException(nameof(length));

        _source = source;
        _ownsSource = ownsSource;
        Length = length;
    }

    public long Length { get; }
    public string Generation => _source.Generation;
    public bool LeasesAreImmutable => _source.LeasesAreImmutable;
    public bool RetainsResidentBacking => _source.RetainsResidentBacking;

    public ValueTask ReadExactlyAsync(
        long offset,
        Memory<byte> destination,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        RangeValidation.Validate(offset, destination.Length, Length);
        return _source.ReadExactlyAsync(offset, destination, cancellationToken);
    }

    public ValueTask<RangeLease> AcquireAsync(
        long offset,
        int length,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        RangeValidation.Validate(offset, length, Length);
        return _source.AcquireAsync(offset, length, cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0 && _ownsSource)
            await _source.DisposeAsync().ConfigureAwait(false);
    }

    private void ThrowIfDisposed()
        => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
}

public sealed class AssetPackBuilder
{
    private readonly Dictionary<Guid, PendingAsset> _assets = [];

    public int Count => _assets.Count;

    public AssetPackBuilder AddAsset(
        Guid assetId,
        string assetType,
        ReadOnlyMemory<byte> documentBytes,
        ulong schemaFingerprint,
        ChunkCompression compression = ChunkCompression.None)
    {
        if (assetId == Guid.Empty)
            throw new ArgumentOutOfRangeException(nameof(assetId));
        ArgumentException.ThrowIfNullOrWhiteSpace(assetType);
        if (_assets.ContainsKey(assetId))
            throw new InvalidOperationException($"Asset {assetId} already exists in the pack.");
        if (compression != ChunkCompression.None)
        {
            throw new ArgumentException(
                "Nested binary asset documents must remain uncompressed for random range access. " +
                "Compress semantic chunks inside the asset document instead.",
                nameof(compression));
        }

        NestedDocumentValidation validation = ValidateNestedDocument(documentBytes.Span);
        if (validation.Header.SchemaFingerprint != schemaFingerprint)
        {
            throw new ArgumentException(
                $"Pack entry fingerprint 0x{schemaFingerprint:X16} does not match nested document " +
                $"fingerprint 0x{validation.Header.SchemaFingerprint:X16}.",
                nameof(schemaFingerprint));
        }

        _assets.Add(assetId, new PendingAsset(
            assetId,
            assetType,
            documentBytes,
            schemaFingerprint,
            compression,
            validation.AuthenticationDigest));
        return this;
    }

    private static NestedDocumentValidation ValidateNestedDocument(ReadOnlySpan<byte> document)
    {
        if (document.Length < BinaryDocumentFormat.HeaderSize)
            throw new ArgumentException("Nested asset document is shorter than the binary header.", nameof(document));
        BinaryDocumentHeader header = BinaryDocumentFormat.ReadHeader(document[..BinaryDocumentFormat.HeaderSize]);
        if (header.TotalLength != document.Length)
        {
            throw new ArgumentException(
                $"Nested asset document declares length {header.TotalLength}, actual length is {document.Length}.",
                nameof(document));
        }

        BinaryReadLimits limits = BinaryReadLimits.Default;
        if (header.CatalogLength < 4 + BinaryDocumentFormat.HashSize
            || header.CatalogLength > limits.MaxTypeCatalogBytes)
        {
            throw new ArgumentException("Nested asset type catalog exceeds configured limits.", nameof(document));
        }
        int catalogEnd = checked(BinaryDocumentFormat.HeaderSize + (int)header.CatalogLength);
        if (catalogEnd > document.Length)
            throw new ArgumentException("Nested asset type catalog is truncated.", nameof(document));
        BinaryWireTypeDescriptor[] catalog = BinaryDocumentFormat.ReadTypeCatalog(
            document.Slice(BinaryDocumentFormat.HeaderSize, (int)header.CatalogLength),
            limits);
        if (!catalog.Any(descriptor =>
                descriptor.SchemaFingerprint == header.SchemaFingerprint
                && descriptor.Compatibility == header.Compatibility
                && descriptor.SchemaEpoch == header.SchemaEpoch))
        {
            throw new ArgumentException(
                "Nested asset catalog has no descriptor matching its root header.",
                nameof(document));
        }

        long expectedRootOffset = BinaryDocumentFormat.Align(catalogEnd, 16);
        long rootEnd = checked(header.RootOffset + header.RootLength);
        long directoryEnd = checked(header.DirectoryOffset + header.DirectoryLength);
        long expectedDirectoryLength = checked(
            (long)header.ChunkCount * BinaryDocumentFormat.DirectoryEntrySize);
        if (header.RootOffset != expectedRootOffset
            || header.RootLength < 0
            || header.RootLength > limits.MaxRootBytes
            || rootEnd > document.Length)
        {
            throw new ArgumentException("Nested asset root range is invalid.", nameof(document));
        }
        if (header.DirectoryLength != expectedDirectoryLength
            || header.DirectoryOffset < rootEnd
            || (header.DirectoryOffset & 15) != 0
            || directoryEnd > document.Length)
        {
            throw new ArgumentException("Nested asset directory range is invalid.", nameof(document));
        }
        if (header.ChunkCount > limits.MaxChunkCount)
            throw new ArgumentException("Nested asset chunk count exceeds configured limits.", nameof(document));

        ReadOnlySpan<byte> root = document.Slice((int)header.RootOffset, (int)header.RootLength);
        Digest256 rootHash = Digest256.ComputeSha256(root);
        if (!rootHash.FixedTimeEquals(header.RootHash))
            throw new ArgumentException("Nested asset root hash is invalid.", nameof(document));

        long payloadFloor = BinaryDocumentFormat.Align(directoryEnd, 16);
        ulong previousKey = 0;
        long previousEnd = payloadFloor;
        for (uint index = 0; index < header.ChunkCount; index++)
        {
            int entryOffset = checked((int)(header.DirectoryOffset
                + index * BinaryDocumentFormat.DirectoryEntrySize));
            BinaryChunkEntry entry = BinaryDocumentFormat.ReadEntry(
                document.Slice(entryOffset, BinaryDocumentFormat.DirectoryEntrySize));
            ValidateNestedChunk(
                entry,
                limits,
                previousKey,
                previousEnd,
                payloadFloor,
                document.Length);
            previousKey = entry.Key;
            previousEnd = entry.EndOffset;
        }
        Digest256 headerHash = Digest256.ComputeSha256(
            document[..BinaryDocumentFormat.HeaderSize]);
        Digest256 catalogHash = Digest256.ComputeSha256(
            document.Slice(BinaryDocumentFormat.HeaderSize, (int)header.CatalogLength));
        Digest256 directoryHash = Digest256.ComputeSha256(
            document.Slice((int)header.DirectoryOffset, (int)header.DirectoryLength));
        Digest256 authenticationDigest = BinaryDocumentFormat.ComputeAuthenticationDigest(
            headerHash,
            catalogHash,
            header.RootHash,
            directoryHash);
        return new NestedDocumentValidation(header, authenticationDigest);
    }

    private static void ValidateNestedChunk(
        in BinaryChunkEntry entry,
        BinaryReadLimits limits,
        ulong previousKey,
        long previousEnd,
        long payloadFloor,
        int documentLength)
    {
        if (entry.Key == 0 || entry.Key <= previousKey)
            throw new ArgumentException("Nested asset directory keys are not strictly increasing.", "document");
        if (!Enum.IsDefined(entry.Compression))
            throw new ArgumentException("Nested asset chunk compression is invalid.", "document");
        if (entry.Alignment <= 0 || entry.Alignment > 1024 * 1024
            || (entry.Alignment & (entry.Alignment - 1)) != 0
            || (entry.Offset & (entry.Alignment - 1L)) != 0)
        {
            throw new ArgumentException("Nested asset chunk alignment is invalid.", "document");
        }
        if (entry.StoredLength < 0 || entry.StoredLength > limits.MaxStoredChunkBytes
            || entry.DecodedLength < 0 || entry.DecodedLength > limits.MaxDecodedChunkBytes
            || entry.StoredLength > int.MaxValue || entry.DecodedLength > int.MaxValue)
        {
            throw new ArgumentException("Nested asset chunk lengths exceed configured limits.", "document");
        }
        if (entry.Compression == ChunkCompression.None && entry.StoredLength != entry.DecodedLength)
            throw new ArgumentException("Nested uncompressed chunk has unequal lengths.", "document");
        if (entry.StoredLength == 0 && entry.DecodedLength != 0)
            throw new ArgumentException("Nested asset chunk expands from an empty payload.", "document");
        if (entry.StoredLength > 0
            && entry.DecodedLength > checked(entry.StoredLength * (long)limits.MaxCompressionRatio))
        {
            throw new ArgumentException("Nested asset chunk exceeds the compression-ratio limit.", "document");
        }

        long end = entry.EndOffset;
        if (entry.Offset < payloadFloor
            || entry.Offset < previousEnd
            || end > documentLength)
        {
            throw new ArgumentException("Nested asset chunk range is invalid or overlaps.", "document");
        }
    }

    public async ValueTask WriteAsync(
        FileStream destination,
        Guid? generation = null,
        CancellationToken cancellationToken = default)
    {
        BinaryDocumentWriter builder = CreateDocumentBuilder();
        await builder.WriteAsync(destination, generation, cancellationToken).ConfigureAwait(false);
        long bodyLength = destination.Position;
        AssetPackFooterFormat.Write(destination, bodyLength, ReadOnlySpan<byte>.Empty);
    }

    public async ValueTask WriteSignedAsync(
        FileStream destination,
        RSA privateKey,
        Guid? generation = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(privateKey);
        PendingAsset[] ordered = OrderedAssets();
        Digest256 digest = await CreateDocumentBuilder(ordered)
            .WriteAuthenticatedAsync(destination, generation, cancellationToken)
            .ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        Span<byte> digestBytes = stackalloc byte[Digest256.Size];
        digest.Write(digestBytes);
        byte[] signature = privateKey.SignHash(
            digestBytes,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        long bodyLength = destination.Position;
        AssetPackFooterFormat.Write(destination, bodyLength, signature);
    }

    public async ValueTask PublishAsync(
        string path,
        Guid? generation = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string fullPath = Path.GetFullPath(path);
        string? directory = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrEmpty(directory))
            throw new InvalidOperationException("Asset pack path has no parent directory.");
        Directory.CreateDirectory(directory);

        string temporary = Path.Combine(
            directory,
            $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(
                temporary,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 128 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await WriteAsync(stream, generation, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporary, fullPath, overwrite: true);
        }
        catch
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
            throw;
        }
    }

    public async ValueTask PublishSignedAsync(
        string path,
        RSA privateKey,
        Guid? generation = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(privateKey);
        await PublishCoreAsync(
            path,
            (stream, token) => WriteSignedAsync(stream, privateKey, generation, token),
            cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask PublishCoreAsync(
        string path,
        Func<FileStream, CancellationToken, ValueTask> write,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string fullPath = Path.GetFullPath(path);
        string? directory = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrEmpty(directory))
            throw new InvalidOperationException("Asset pack path has no parent directory.");
        Directory.CreateDirectory(directory);

        string temporary = Path.Combine(
            directory,
            $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(
                temporary,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 128 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await write(stream, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporary, fullPath, overwrite: true);
        }
        catch
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
            throw;
        }
    }

    private BinaryDocumentWriter CreateDocumentBuilder(PendingAsset[]? assets = null)
    {
        PendingAsset[] ordered = assets ?? OrderedAssets();

        var catalog = new AssetPackIndex(ordered.Length);
        var usedKeys = new HashSet<ulong>();
        foreach (PendingAsset asset in ordered)
        {
            ulong key = AssetChunkKey(asset.AssetId);
            if (!usedKeys.Add(key))
            {
                throw new InvalidOperationException(
                    $"Asset pack chunk-key collision for asset {asset.AssetId}; use a different asset id.");
            }

            catalog.Entries.Add(new AssetPackEntry(
                asset.AssetId,
                asset.AssetType,
                key,
                asset.SchemaFingerprint)
            {
                DocumentAuthenticationDigest = asset.AuthenticationDigest,
            });
        }

        BinaryDocumentWriter builder = BinaryDocumentWriter.Create(catalog);
        for (int i = 0; i < ordered.Length; i++)
        {
            PendingAsset asset = ordered[i];
            AssetPackEntry entry = catalog.Entries[i];
            builder.AddChunk(
                entry.ChunkKey,
                asset.Document,
                asset.SchemaFingerprint,
                asset.Compression,
                alignment: 4096,
                ordinal: checked((uint)i));
        }

        return builder;
    }

    private PendingAsset[] OrderedAssets() => _assets.Values
        .OrderBy(static asset => asset.AssetId.ToString("N", CultureInfo.InvariantCulture), StringComparer.Ordinal)
        .ToArray();

    internal static ulong AssetChunkKey(Guid assetId)
    {
        Span<byte> bytes = stackalloc byte[16];
        if (!assetId.TryWriteBytes(bytes, bigEndian: true, out int written) || written != 16)
            throw new InvalidOperationException("Unable to encode asset id.");
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(bytes, hash);
        ulong key = BinaryPrimitives.ReadUInt64LittleEndian(hash);
        return key == 0 ? ulong.MaxValue : key;
    }

    private sealed record PendingAsset(
        Guid AssetId,
        string AssetType,
        ReadOnlyMemory<byte> Document,
        ulong SchemaFingerprint,
        ChunkCompression Compression,
        Digest256 AuthenticationDigest);

    private readonly record struct NestedDocumentValidation(
        BinaryDocumentHeader Header,
        Digest256 AuthenticationDigest);
}

public sealed class AssetPack : IAsyncDisposable
{
    private readonly BinaryDocument<AssetPackIndex> _document;
    private readonly IReadOnlyDictionary<Guid, AssetPackEntry> _entries;
    private readonly AssetPackFooter _footer;

    private AssetPack(BinaryDocument<AssetPackIndex> document, AssetPackFooter footer)
    {
        _document = document;
        _footer = footer;
        _entries = document.Root.Entries.ToDictionary(static entry => entry.AssetId);
    }

    public Guid Generation => _document.Generation;
    public int Count => _entries.Count;
    public IEnumerable<AssetPackEntry> Entries => _entries.Values;
    public bool HasSignature => _footer.SignatureAlgorithm != 0;

    public static async ValueTask<AssetPack> OpenAsync(
        string path,
        BinaryReadLimits? limits = null,
        CancellationToken cancellationToken = default)
    {
        FileRangeSource source = FileRangeSource.Open(path);
        return await OpenCoreAsync(
            source,
            ownsSource: true,
            limits,
            cancellationToken).ConfigureAwait(false);
    }

    public static async ValueTask<AssetPack> OpenVerifiedAsync(
        string path,
        RSA trustedPublicKey,
        BinaryReadLimits? limits = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(trustedPublicKey);
        AssetPack pack = await OpenAsync(path, limits, cancellationToken).ConfigureAwait(false);
        try
        {
            await pack.VerifySignatureAsync(trustedPublicKey, cancellationToken).ConfigureAwait(false);
            return pack;
        }
        catch
        {
            await pack.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public static async ValueTask<AssetPack> OpenAsync(
        IRangeSource source,
        bool ownsSource = false,
        BinaryReadLimits? limits = null,
        CancellationToken cancellationToken = default)
    {
        return await OpenCoreAsync(
            source,
            ownsSource,
            limits,
            cancellationToken).ConfigureAwait(false);
    }

    public static async ValueTask<AssetPack> OpenVerifiedAsync(
        IRangeSource source,
        RSA trustedPublicKey,
        bool ownsSource = false,
        BinaryReadLimits? limits = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(trustedPublicKey);
        AssetPack pack = await OpenAsync(source, ownsSource, limits, cancellationToken).ConfigureAwait(false);
        try
        {
            await pack.VerifySignatureAsync(trustedPublicKey, cancellationToken).ConfigureAwait(false);
            return pack;
        }
        catch
        {
            await pack.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Authenticates the pack catalog and every nested document's header, catalog, root hash, and
    /// directory receipt against a trusted RSA key. Semantic payload bytes remain lazy and are
    /// checked against the authenticated directory hash when their chunk is first acquired.
    /// </summary>
    public async ValueTask VerifySignatureAsync(
        RSA trustedPublicKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(trustedPublicKey);
        if (_footer.SignatureAlgorithm != AssetPackFooterFormat.RsaPkcs1Sha256
            || _footer.Signature.Length == 0)
        {
            throw new CryptographicException("Asset pack does not contain a supported required signature.");
        }

        Digest256 digest = await _document.ComputeAuthenticationDigestAsync(cancellationToken).ConfigureAwait(false);
        Span<byte> hash = stackalloc byte[Digest256.Size];
        digest.Write(hash);
        if (!trustedPublicKey.VerifyHash(
                hash,
                _footer.Signature,
                HashAlgorithmName.SHA256,
                RSASignaturePadding.Pkcs1))
        {
            throw new CryptographicException("Asset pack signature does not match the trusted public key.");
        }
    }

    private static async ValueTask<AssetPack> OpenCoreAsync(
        IRangeSource source,
        bool ownsSource,
        BinaryReadLimits? limits,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        bool sourceTransferred = false;
        try
        {
            AssetPackFooter footer = await AssetPackFooterFormat.ReadAsync(
                source,
                cancellationToken).ConfigureAwait(false);
            var bodySource = new AssetPackBodyRangeSource(source, footer.BodyLength, ownsSource);
            sourceTransferred = true;
            BinaryDocument<AssetPackIndex> document = await BinaryDocument<AssetPackIndex>.OpenAsync(
                bodySource,
                ownsSource: true,
                limits,
                cancellationToken).ConfigureAwait(false);
            return new AssetPack(document, footer);
        }
        catch
        {
            if (ownsSource && !sourceTransferred)
                await source.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public bool TryGetEntry(Guid assetId, out AssetPackEntry? entry)
        => _entries.TryGetValue(assetId, out entry);

    public async ValueTask<bool> ContentEqualsAsync(
        Guid assetId,
        string assetType,
        ReadOnlyMemory<byte> documentBytes,
        ulong schemaFingerprint,
        CancellationToken cancellationToken = default)
    {
        if (!_entries.TryGetValue(assetId, out AssetPackEntry? entry))
            return false;
        if (!StringComparer.Ordinal.Equals(entry.AssetType, assetType)
            || entry.SchemaFingerprint != schemaFingerprint)
        {
            return false;
        }
        BinaryChunkEntry descriptor = await _document.FindChunkAsync(entry.ChunkKey, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidDataException($"Pack catalog chunk 0x{entry.ChunkKey:X16} is missing.");
        Digest256 candidateHash = Digest256.ComputeSha256(documentBytes.Span);
        bool baseContentIsValid = await _document.VerifyChunkContentAsync(
            descriptor,
            cancellationToken).ConfigureAwait(false);
        return baseContentIsValid
            && descriptor.DecodedLength == documentBytes.Length
            && candidateHash.FixedTimeEquals(descriptor.ContentHash);
    }

    public async ValueTask<IRangeSource> OpenAssetSourceAsync(
        Guid assetId,
        CancellationToken cancellationToken = default)
    {
        if (!_entries.TryGetValue(assetId, out AssetPackEntry? entry))
            throw new KeyNotFoundException($"Asset {assetId} was not found in pack generation {Generation}.");
        BinaryChunkEntry descriptor = await _document.FindChunkAsync(entry.ChunkKey, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidDataException($"Pack catalog chunk 0x{entry.ChunkKey:X16} is missing.");
        if (descriptor.TypeFingerprint != entry.SchemaFingerprint)
        {
            throw new InvalidDataException(
                $"Pack chunk fingerprint 0x{descriptor.TypeFingerprint:X16} does not match catalog " +
                $"fingerprint 0x{entry.SchemaFingerprint:X16}.");
        }
        IRangeSource source = await _document.OpenChunkRangeSourceAsync(entry.ChunkKey, cancellationToken)
            .ConfigureAwait(false);
        return new PackAssetRangeSource(entry, Generation, source);
    }

    public ValueTask DisposeAsync() => _document.DisposeAsync();
}

/// <summary>
/// Builds a deterministic overlay containing only assets whose complete binary documents differ
/// from the base pack. Unchanged assets are deliberately omitted from the patch.
/// </summary>
public sealed class AssetPackPatchBuilder
{
    private readonly AssetPack _basePack;
    private readonly Dictionary<Guid, PendingUpdate> _updates = [];
    private Guid[] _changedAssetIds = [];

    public AssetPackPatchBuilder(AssetPack basePack)
    {
        _basePack = basePack ?? throw new ArgumentNullException(nameof(basePack));
    }

    public IReadOnlyList<Guid> ChangedAssetIds => _changedAssetIds;

    public AssetPackPatchBuilder AddAsset(
        Guid assetId,
        string assetType,
        ReadOnlyMemory<byte> documentBytes,
        ulong schemaFingerprint)
    {
        if (assetId == Guid.Empty)
            throw new ArgumentOutOfRangeException(nameof(assetId));
        ArgumentException.ThrowIfNullOrWhiteSpace(assetType);
        if (!_updates.TryAdd(assetId, new PendingUpdate(
                assetId,
                assetType,
                documentBytes,
                schemaFingerprint)))
        {
            throw new InvalidOperationException($"Asset {assetId} already has a patch candidate.");
        }
        return this;
    }

    public async ValueTask WriteAsync(
        FileStream destination,
        Guid? generation = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(destination);
        AssetPackBuilder builder = await CreatePatchBuilderAsync(cancellationToken).ConfigureAwait(false);
        await builder.WriteAsync(destination, generation, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask WriteSignedAsync(
        FileStream destination,
        RSA privateKey,
        Guid? generation = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(privateKey);
        AssetPackBuilder builder = await CreatePatchBuilderAsync(cancellationToken).ConfigureAwait(false);
        await builder.WriteSignedAsync(destination, privateKey, generation, cancellationToken)
            .ConfigureAwait(false);
    }

    private async ValueTask<AssetPackBuilder> CreatePatchBuilderAsync(
        CancellationToken cancellationToken)
    {
        var builder = new AssetPackBuilder();
        var changed = new List<Guid>();
        foreach (PendingUpdate update in _updates.Values.OrderBy(static value => value.AssetId))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await _basePack.ContentEqualsAsync(
                    update.AssetId,
                    update.AssetType,
                    update.Document,
                    update.SchemaFingerprint,
                    cancellationToken)
                    .ConfigureAwait(false))
            {
                continue;
            }

            builder.AddAsset(
                update.AssetId,
                update.AssetType,
                update.Document,
                update.SchemaFingerprint);
            changed.Add(update.AssetId);
        }

        _changedAssetIds = changed.ToArray();
        return builder;
    }

    private sealed record PendingUpdate(
        Guid AssetId,
        string AssetType,
        ReadOnlyMemory<byte> Document,
        ulong SchemaFingerprint);
}

internal sealed class PackAssetRangeSource : IRangeSource, IBinaryDocumentReceipt
{
    private IRangeSource? _source;
    private readonly string _sourceGeneration;
    private readonly string _generation;
    private int _disposed;

    internal PackAssetRangeSource(AssetPackEntry entry, Guid packGeneration, IRangeSource source)
    {
        Entry = entry;
        _source = source;
        _sourceGeneration = source.Generation;
        _generation = $"{_sourceGeneration}:pack:{packGeneration:N}:{entry.AssetId:N}";
    }

    public AssetPackEntry Entry { get; }
    public long Length
    {
        get
        {
            IRangeSource source = GetSource();
            return source.Length;
        }
    }
    public string Generation
    {
        get
        {
            _ = GetSource();
            return _generation;
        }
    }
    public bool LeasesAreImmutable => GetSource().LeasesAreImmutable;
    public bool RetainsResidentBacking => GetSource().RetainsResidentBacking;

    void IBinaryDocumentReceipt.Validate(Digest256 documentDigest)
    {
        if (!Entry.DocumentAuthenticationDigest.FixedTimeEquals(documentDigest))
        {
            throw new CryptographicException(
                "Opened asset document metadata does not match its authenticated pack receipt.");
        }
    }

    public async ValueTask ReadExactlyAsync(
        long offset,
        Memory<byte> destination,
        CancellationToken cancellationToken = default)
    {
        IRangeSource source = GetSource();
        EnsureSourceGeneration(source);
        await source.ReadExactlyAsync(offset, destination, cancellationToken).ConfigureAwait(false);
        EnsureSourceGeneration(source);
    }

    public async ValueTask<RangeLease> AcquireAsync(
        long offset,
        int length,
        CancellationToken cancellationToken = default)
    {
        IRangeSource source = GetSource();
        EnsureSourceGeneration(source);
        RangeLease lease = await source.AcquireAsync(offset, length, cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureSourceGeneration(source);
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
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return ValueTask.CompletedTask;
        IRangeSource? source = Interlocked.Exchange(ref _source, null);
        return source is null ? ValueTask.CompletedTask : source.DisposeAsync();
    }

    private IRangeSource GetSource()
    {
        IRangeSource? source = Volatile.Read(ref _source);
        ObjectDisposedException.ThrowIf(source is null || Volatile.Read(ref _disposed) != 0, this);
        return source;
    }

    private void EnsureSourceGeneration(IRangeSource source)
    {
        if (!StringComparer.Ordinal.Equals(_sourceGeneration, source.Generation))
            throw new IOException("Pack asset backing source generation changed while the range was open.");
    }
}

public sealed class AssetPackOverlay : IAsyncDisposable
{
    private readonly AssetPack[] _packs;

    /// <param name="packsHighestPriorityFirst">Hotfix/DLC packs before base packs.</param>
    public AssetPackOverlay(IEnumerable<AssetPack> packsHighestPriorityFirst)
    {
        ArgumentNullException.ThrowIfNull(packsHighestPriorityFirst);
        _packs = packsHighestPriorityFirst.ToArray();
        if (_packs.Length == 0)
            throw new ArgumentException("At least one asset pack is required.", nameof(packsHighestPriorityFirst));
    }

    public bool TryResolve(Guid assetId, out AssetPack? pack, out AssetPackEntry? entry)
    {
        foreach (AssetPack candidate in _packs)
        {
            if (candidate.TryGetEntry(assetId, out entry))
            {
                pack = candidate;
                return true;
            }
        }

        pack = null;
        entry = null;
        return false;
    }

    public ValueTask<IRangeSource> OpenAssetSourceAsync(
        Guid assetId,
        CancellationToken cancellationToken = default)
    {
        if (!TryResolve(assetId, out AssetPack? pack, out _))
            throw new KeyNotFoundException($"Asset {assetId} was not found in any pack overlay.");
        return pack!.OpenAssetSourceAsync(assetId, cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (AssetPack pack in _packs)
            await pack.DisposeAsync().ConfigureAwait(false);
    }
}
