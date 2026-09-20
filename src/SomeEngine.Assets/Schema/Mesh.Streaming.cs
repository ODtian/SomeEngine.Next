using SomeEngine.Assets.Pipeline;
using SomeEngine.Assets.Data;
using SomeEngine.Serialization;
using SomeEngine.Serialization.Containers;
using SomeEngine.Serialization.IO;
using System.Security.Cryptography;

namespace SomeEngine.Assets.Schema;

internal partial class MeshData
{
    private static ValueTask<BinaryDocument> OpenDocumentAsync(string path, CancellationToken cancellationToken)
        => BinaryDocument.OpenAsync(SomeEngine.Serialization.IO.FileRangeSource.Open(path), Mesh.Asset.WireType,
            ownsSource: true, cancellationToken: cancellationToken);

    internal static ulong PayloadChunkKey { get; }
        = BinaryFieldKey.FromName("SomeEngine.Assets.Schema.Mesh.Payload");

    internal static async ValueTask<MeshData> ReadAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        await using BinaryDocument document =
            await OpenDocumentAsync(path, cancellationToken)
                .ConfigureAwait(false);
        return await MaterializeAsync(document, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Opens only the mesh root, page headers, and BVH. Page bodies remain in the payload range
    /// source until explicitly acquired by the runtime streamer.
    /// </summary>
    internal static async ValueTask<Mesh> OpenStreamedAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        BinaryDocument document = await OpenDocumentAsync(path, cancellationToken).ConfigureAwait(false);
        return await OpenStreamedCoreAsync(document, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Opens a streamed mesh over a caller-supplied range source.</summary>
    internal static async ValueTask<Mesh> OpenStreamedAsync(
        IRangeSource source,
        bool ownsSource = false,
        CancellationToken cancellationToken = default)
    {
        BinaryDocument document = await BinaryDocument.OpenAsync(
            source, Mesh.Asset.WireType,
            ownsSource,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return await OpenStreamedCoreAsync(document, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Transfers ownership of an already-open binary document into a streamed mesh asset. This
    /// is the runtime storage entry point and does not require a file path or source reopen.
    /// </summary>
    internal static ValueTask<Mesh> OpenStreamedAsync(
        BinaryDocument document,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        return OpenStreamedCoreAsync(document, cancellationToken);
    }

    internal static ValueTask<Mesh> OpenStreamedAsync(BinaryDocument document,
        MeshData fields, CancellationToken cancellationToken)
        => OpenStreamedCoreAsync(document, cancellationToken, fields);

    internal static BinaryDocumentWriter CreateWriter(MeshData asset)
    {
        asset.ValidateMaterialReferences(asset.Name ?? "");
        ReadOnlyMemory<byte>? payload = asset.Payload;
        if (!payload.HasValue || payload.Value.IsEmpty)
            throw new InvalidDataException("MeshData assets must provide one externalized payload.");
        PopulatePayloadIntegrity(asset, payload.Value.Span);
        asset.PayloadKey = PayloadChunkKey;
        asset.PayloadLength = checked((ulong)payload.Value.Length);

        BinaryDocumentWriter builder = BinaryDocumentWriter.Create(asset);
        builder.AddChunk(
            asset.PayloadChunk.Key,
            payload.Value,
            global::SomeEngine.Assets.Asset<Mesh>.ByteSchema,
            ChunkCompression.None,
            alignment: 4096);

        return builder;
    }

    internal static async ValueTask<BinaryDocumentWriter> CreateWriterAsync(MeshData asset,
        BinaryDocument? source, CancellationToken cancellationToken)
    {
        if (asset.Payload.HasValue || source is null) return CreateWriter(asset);
        ValidateExternalizedRoot(asset);
        ValidatePayloadChunk(asset);
        BinaryDocumentWriter writer = BinaryDocumentWriter.Create(asset);
        await writer.AddChunkAsync(asset.PayloadChunk.Key, source, cancellationToken).ConfigureAwait(false);
        return writer;
    }

    private static async ValueTask<MeshData> MaterializeAsync(
        BinaryDocument document,
        CancellationToken cancellationToken)
    {
        MeshData result = document.ReadRoot<MeshData>();
        ValidateExternalizedRoot(result);
        Memory<byte>? payload = await document.TryReadChunkAsync(
            result.PayloadChunk,
            static length => GC.AllocateUninitializedArray<byte>(length),
            cancellationToken).ConfigureAwait(false);
        if (!payload.HasValue)
            throw new InvalidDataException("MeshData document is missing its required externalized payload chunk.");
        result.Payload = payload;
        return result;
    }

    private static async ValueTask<Mesh> OpenStreamedCoreAsync(
        BinaryDocument document,
        CancellationToken cancellationToken,
        MeshData? fields = null)
    {
        IRangeSource? payload = null;
        MeshData? root = fields;
        try
        {
            root ??= document.ReadRoot<MeshData>();
            ValidateExternalizedRoot(root);
            ValidatePayloadChunk(root);
            payload = await document.OpenChunkRangeSourceAsync(
                root.PayloadChunk,
                cancellationToken).ConfigureAwait(false);
            if (payload.RetainsResidentBacking)
            {
                throw new NotSupportedException(
                    "Streamed meshes require a non-resident file or remote range source. A memory or " +
                    "memory-mapped source would remain as a second physical backing after page/BVH publication.");
            }
            ulong bvhOffsetValue = root.BvhOffset;
            if (bvhOffsetValue == 0 || bvhOffsetValue >= checked((ulong)payload.Length))
            {
                throw new InvalidDataException(
                    $"MeshData BVH offset {bvhOffsetValue} must split the {payload.Length}-byte payload into non-empty page and BVH regions.");
            }

            long bvhOffset = checked((long)bvhOffsetValue);
            long bvhLength = checked(payload.Length - bvhOffset);
            int bvhNodeBytes = System.Runtime.CompilerServices.Unsafe.SizeOf<ClusterBVHNode>();
            if (bvhLength > int.MaxValue || bvhLength % bvhNodeBytes != 0)
            {
                throw new InvalidDataException(
                    $"MeshData BVH region must contain a whole number of {bvhNodeBytes}-byte nodes; length is {bvhLength}.");
            }

            if (root.BvhLength != checked((ulong)bvhLength))
            {
                throw new InvalidDataException(
                    $"MeshData root declares a {root.BvhLength}-byte BVH, but the payload layout contains {bvhLength} bytes.");
            }
            ReadOnlyMemory<byte> expectedBvhHash = RequireSha256(root.BvhSha256, "MeshData BVH");
            IList<MeshPayloadPageDigest>? rootPages = root.PageDigests;
            if (rootPages is null || rootPages.Count == 0)
                throw new InvalidDataException("Streamed mesh root contains no authenticated page descriptors.");
            ulong vertexEncodingHash = VertexEncodingCompatibility.Hash(
                root.VertexEncoding ?? throw new InvalidDataException(
                    "Streamed mesh root has no vertex encoding compatibility."));

            long pageOffset = 0;
            for (int pageIndex = 0; pageIndex < rootPages.Count; pageIndex++)
            {
                MeshPayloadPageDigest authenticated = rootPages[pageIndex]
                    ?? throw new InvalidDataException($"MeshData page digest {pageIndex} is null.");
                if (authenticated.Offset != checked((ulong)pageOffset))
                {
                    throw new InvalidDataException(
                        $"MeshData page digest {pageIndex} starts at {authenticated.Offset}, but canonical layout requires {pageOffset}.");
                }
                if (authenticated.Length < MeshPageHeader.Size ||
                    authenticated.Length > MeshPageHeader.MaxPageSize ||
                    authenticated.ClusterCount == 0 ||
                    authenticated.VertexEncodingHash != vertexEncodingHash)
                {
                    throw new InvalidDataException($"MeshData page digest {pageIndex} has invalid authenticated layout metadata.");
                }
                int pageLength = checked((int)authenticated.Length);
                long nextOffset = checked(pageOffset + pageLength);
                if (nextOffset > bvhOffset)
                    throw new InvalidDataException($"MeshData page digest {pageIndex} overlaps the BVH region.");
                _ = RequireSha256(authenticated.Sha256, $"MeshData page {pageIndex}");
                pageOffset = nextOffset;
            }

            if (pageOffset != bvhOffset)
                throw new InvalidDataException("MeshData payload must contain at least one complete page before its BVH.");

            var source = new MeshPayloadSource(
                document,
                payload,
                bvhOffset,
                checked((int)bvhLength),
                expectedBvhHash,
                rootPages);
            payload = null;
            Mesh result = Mesh.FromFields(root);
            result.AttachPayloadSource(source);
            return result;
        }
        catch
        {
            root?.Dispose();
            if (payload is not null)
                await payload.DisposeAsync().ConfigureAwait(false);
            await document.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static void ValidatePayloadChunk(MeshData root)
    {
        if (root.PayloadChunk.Key != PayloadChunkKey)
        {
            throw new InvalidDataException(
                $"MeshData root declares payload chunk key 0x{root.PayloadChunk.Key:X16}; " +
                $"expected 0x{PayloadChunkKey:X16}.");
        }
    }

    private static void ValidateExternalizedRoot(MeshData root)
    {
        root.ValidateMaterialReferences(root.Name ?? "");
        if (root.Payload.HasValue)
        {
            throw new InvalidDataException(
                "Binary mesh roots must not contain inline payload bytes.");
        }
    }

    internal static void PopulatePayloadIntegrity(MeshData root, ReadOnlySpan<byte> payload)
    {
        ulong bvhOffsetValue = root.BvhOffset;
        if (bvhOffsetValue == 0 || bvhOffsetValue >= checked((ulong)payload.Length))
        {
            throw new InvalidDataException(
                $"MeshData BVH offset {bvhOffsetValue} must split the {payload.Length}-byte payload into non-empty page and BVH regions.");
        }

        int bvhOffset = checked((int)bvhOffsetValue);
        int bvhLength = checked(payload.Length - bvhOffset);
        int bvhNodeBytes = System.Runtime.CompilerServices.Unsafe.SizeOf<ClusterBVHNode>();
        if (bvhLength % bvhNodeBytes != 0)
        {
            throw new InvalidDataException(
                $"MeshData BVH region must contain a whole number of {bvhNodeBytes}-byte nodes; length is {bvhLength}.");
        }

        List<MeshPayloadPageDigest> pageDigests = [];
        ulong vertexEncodingHash = VertexEncodingCompatibility.Hash(
            root.VertexEncoding ?? throw new InvalidDataException(
                "MeshData root has no vertex encoding compatibility."));
        int pageOffset = 0;
        while (pageOffset < bvhOffset)
        {
            MeshPayloadPage page = MeshPayloadLayout.ReadPage(
                payload[pageOffset..],
                pageOffset,
                bvhOffset);
            if (page.VertexEncodingHash != vertexEncodingHash)
            {
                throw new InvalidDataException(
                    $"MeshData page at byte {pageOffset} uses vertex encoding hash " +
                    $"0x{page.VertexEncodingHash:X16}, but the root requires 0x{vertexEncodingHash:X16}.");
            }
            ReadOnlySpan<byte> pageBytes = payload.Slice(pageOffset, page.Size);
            pageDigests.Add(new MeshPayloadPageDigest
            {
                Offset = checked((ulong)page.Offset),
                Length = checked((uint)page.Size),
                ClusterCount = page.ClusterCount,
                VertexEncodingHash = page.VertexEncodingHash,
                Sha256 = SHA256.HashData(pageBytes),
            });
            pageOffset = checked(pageOffset + page.Size);
        }

        if (pageOffset != bvhOffset || pageDigests.Count == 0)
            throw new InvalidDataException("MeshData payload must contain at least one complete page before its BVH.");

        root.PageDigests = pageDigests.ToImmutableList();
        root.BvhLength = checked((ulong)bvhLength);
        root.BvhSha256 = SHA256.HashData(payload[bvhOffset..]);
    }

    private static void ValidateAuthenticatedDescriptor(
        int pageIndex,
        in MeshPayloadPage actual,
        MeshPayloadPageDigest authenticated)
    {
        if (authenticated.Length != checked((uint)actual.Size) ||
            authenticated.ClusterCount != actual.ClusterCount ||
            authenticated.VertexEncodingHash != actual.VertexEncodingHash)
        {
            throw new InvalidDataException(
                $"MeshData page header {pageIndex} does not match its root-authenticated layout descriptor.");
        }
    }

    private static ReadOnlyMemory<byte> RequireSha256(ReadOnlyMemory<byte>? value, string subject)
    {
        if (!value.HasValue || value.Value.Length != SHA256.HashSizeInBytes)
            throw new InvalidDataException($"{subject} must carry exactly one SHA-256 digest.");
        return value.Value;
    }

}
