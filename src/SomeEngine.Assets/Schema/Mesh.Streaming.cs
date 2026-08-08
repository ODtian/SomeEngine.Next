using SomeEngine.Assets.Pipeline;
using SomeEngine.Assets.Data;
using SomeEngine.Serialization;
using SomeEngine.Serialization.Containers;
using SomeEngine.Serialization.IO;
using System.Numerics;
using System.Security.Cryptography;

namespace SomeEngine.Assets.Schema;

public partial class Mesh
{
    internal static ulong PayloadChunkKey { get; }
        = BinaryFieldKey.FromName("SomeEngine.Assets.Schema.Mesh.Payload");

    internal static async ValueTask<Mesh> ReadAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        await using BinaryDocument<Mesh> document =
            await AssetProject.OpenAsync<Mesh>(path, cancellationToken)
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
        BinaryDocument<Mesh> document = await AssetProject.OpenAsync<Mesh>(
            path,
            limits: null,
            cancellationToken).ConfigureAwait(false);
        return await OpenStreamedCoreAsync(document, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Opens a streamed mesh over a caller-supplied range source.</summary>
    internal static async ValueTask<Mesh> OpenStreamedAsync(
        IRangeSource source,
        bool ownsSource = false,
        CancellationToken cancellationToken = default)
    {
        BinaryDocument<Mesh> document = await BinaryDocument<Mesh>.OpenAsync(
            source,
            ownsSource,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return await OpenStreamedCoreAsync(document, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Transfers ownership of an already-open binary document into a streamed mesh asset. This
    /// is the runtime storage entry point and does not require a file path or source reopen.
    /// </summary>
    internal static ValueTask<Mesh> OpenStreamedAsync(
        BinaryDocument<Mesh> document,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        return OpenStreamedCoreAsync(document, cancellationToken);
    }

    internal static BinaryDocumentWriter CreateWriter(Mesh asset)
    {
        Memory<byte>? payload = asset.Payload;
        if (!payload.HasValue || payload.Value.IsEmpty)
            throw new InvalidDataException("Mesh assets must provide one externalized payload.");
        PopulatePayloadIntegrity(asset, payload.Value.Span);
        asset.PayloadKey = PayloadChunkKey;
        asset.PayloadLength = checked((ulong)payload.Value.Length);

        BinaryDocumentWriter builder = BinaryDocumentWriter.Create(asset);
        builder.AddChunk(
            asset.PayloadChunk.Key,
            payload.Value,
            AssetMetadata.RawBytesTypeFingerprint,
            ChunkCompression.None,
            alignment: 4096);

        return builder;
    }

    private static async ValueTask<Mesh> MaterializeAsync(
        BinaryDocument<Mesh> document,
        CancellationToken cancellationToken)
    {
        ValidateExternalizedRoot(document.Root);
        Mesh result = document.Root;
        Memory<byte>? payload = await document.TryReadChunkAsync(
            result.PayloadChunk,
            static length => GC.AllocateUninitializedArray<byte>(length),
            cancellationToken).ConfigureAwait(false);
        if (!payload.HasValue)
            throw new InvalidDataException("Mesh document is missing its required externalized payload chunk.");
        result.Payload = payload;
        return result;
    }

    private static async ValueTask<Mesh> OpenStreamedCoreAsync(
        BinaryDocument<Mesh> document,
        CancellationToken cancellationToken)
    {
        IRangeSource? payload = null;
        try
        {
            ValidateExternalizedRoot(document.Root);
            ValidatePayloadChunk(document.Root);
            payload = await document.OpenChunkRangeSourceAsync(
                document.Root.PayloadChunk,
                cancellationToken).ConfigureAwait(false);
            if (payload.RetainsResidentBacking)
            {
                throw new NotSupportedException(
                    "Streamed meshes require a non-resident file or remote range source. A memory or " +
                    "memory-mapped source would remain as a second physical backing after page/BVH publication.");
            }
            Mesh root = document.Root;
            ulong bvhOffsetValue = root.BvhOffset;
            if (bvhOffsetValue == 0 || bvhOffsetValue >= checked((ulong)payload.Length))
            {
                throw new InvalidDataException(
                    $"Mesh BVH offset {bvhOffsetValue} must split the {payload.Length}-byte payload into non-empty page and BVH regions.");
            }

            long bvhOffset = checked((long)bvhOffsetValue);
            long bvhLength = checked(payload.Length - bvhOffset);
            int bvhNodeBytes = System.Runtime.CompilerServices.Unsafe.SizeOf<ClusterBVHNode>();
            if (bvhLength > int.MaxValue || bvhLength % bvhNodeBytes != 0)
            {
                throw new InvalidDataException(
                    $"Mesh BVH region must contain a whole number of {bvhNodeBytes}-byte nodes; length is {bvhLength}.");
            }

            if (root.BvhLength != checked((ulong)bvhLength))
            {
                throw new InvalidDataException(
                    $"Mesh root declares a {root.BvhLength}-byte BVH, but the payload layout contains {bvhLength} bytes.");
            }
            ReadOnlyMemory<byte> expectedBvhHash = RequireSha256(root.BvhSha256, "Mesh BVH");
            IList<MeshPayloadPageDigest>? rootPages = root.PageDigests;
            if (rootPages is null || rootPages.Count == 0)
                throw new InvalidDataException("Streamed mesh root contains no authenticated page descriptors.");

            long pageOffset = 0;
            for (int pageIndex = 0; pageIndex < rootPages.Count; pageIndex++)
            {
                MeshPayloadPageDigest authenticated = rootPages[pageIndex]
                    ?? throw new InvalidDataException($"Mesh page digest {pageIndex} is null.");
                if (authenticated.Offset != checked((ulong)pageOffset))
                {
                    throw new InvalidDataException(
                        $"Mesh page digest {pageIndex} starts at {authenticated.Offset}, but canonical layout requires {pageOffset}.");
                }
                Vec3 origin = authenticated.QuantOrigin
                    ?? throw new InvalidDataException($"Mesh page digest {pageIndex} has no quantization origin.");
                if (authenticated.Length < MeshPageHeader.Size ||
                    authenticated.Length > MeshPageHeader.MaxPageSize ||
                    authenticated.ClusterCount == 0 ||
                    !float.IsFinite(origin.X) ||
                    !float.IsFinite(origin.Y) ||
                    !float.IsFinite(origin.Z) ||
                    !float.IsFinite(authenticated.QuantStep) ||
                    authenticated.QuantStep <= 0)
                {
                    throw new InvalidDataException($"Mesh page digest {pageIndex} has invalid authenticated layout metadata.");
                }
                int pageLength = checked((int)authenticated.Length);
                long nextOffset = checked(pageOffset + pageLength);
                if (nextOffset > bvhOffset)
                    throw new InvalidDataException($"Mesh page digest {pageIndex} overlaps the BVH region.");
                _ = RequireSha256(authenticated.Sha256, $"Mesh page {pageIndex}");
                pageOffset = nextOffset;
            }

            if (pageOffset != bvhOffset)
                throw new InvalidDataException("Mesh payload must contain at least one complete page before its BVH.");

            var source = new MeshPayloadSource(
                document,
                payload,
                bvhOffset,
                checked((int)bvhLength),
                expectedBvhHash,
                rootPages);
            payload = null;
            root.AttachPayloadSource(source);
            return root;
        }
        catch
        {
            if (payload is not null)
                await payload.DisposeAsync().ConfigureAwait(false);
            await document.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static void ValidatePayloadChunk(Mesh root)
    {
        if (root.PayloadChunk.Key != PayloadChunkKey)
        {
            throw new InvalidDataException(
                $"Mesh root declares payload chunk key 0x{root.PayloadChunk.Key:X16}; " +
                $"expected 0x{PayloadChunkKey:X16}.");
        }
    }

    private static void ValidateExternalizedRoot(Mesh root)
    {
        if (root.Payload.HasValue)
        {
            throw new InvalidDataException(
                "Binary mesh roots must not contain inline payload bytes.");
        }
    }

    private static void PopulatePayloadIntegrity(Mesh root, ReadOnlySpan<byte> payload)
    {
        ulong bvhOffsetValue = root.BvhOffset;
        if (bvhOffsetValue == 0 || bvhOffsetValue >= checked((ulong)payload.Length))
        {
            throw new InvalidDataException(
                $"Mesh BVH offset {bvhOffsetValue} must split the {payload.Length}-byte payload into non-empty page and BVH regions.");
        }

        int bvhOffset = checked((int)bvhOffsetValue);
        int bvhLength = checked(payload.Length - bvhOffset);
        int bvhNodeBytes = System.Runtime.CompilerServices.Unsafe.SizeOf<ClusterBVHNode>();
        if (bvhLength % bvhNodeBytes != 0)
        {
            throw new InvalidDataException(
                $"Mesh BVH region must contain a whole number of {bvhNodeBytes}-byte nodes; length is {bvhLength}.");
        }

        List<MeshPayloadPageDigest> pageDigests = [];
        int pageOffset = 0;
        while (pageOffset < bvhOffset)
        {
            MeshPayloadPage page = MeshPayloadLayout.ReadPage(
                payload[pageOffset..],
                pageOffset,
                bvhOffset);
            ReadOnlySpan<byte> pageBytes = payload.Slice(pageOffset, page.Size);
            pageDigests.Add(new MeshPayloadPageDigest
            {
                Offset = checked((ulong)page.Offset),
                Length = checked((uint)page.Size),
                ClusterCount = page.ClusterCount,
                QuantOrigin = new Vec3
                {
                    X = page.QuantOrigin.X,
                    Y = page.QuantOrigin.Y,
                    Z = page.QuantOrigin.Z,
                },
                QuantStep = page.QuantStep,
                Sha256 = SHA256.HashData(pageBytes),
            });
            pageOffset = checked(pageOffset + page.Size);
        }

        if (pageOffset != bvhOffset || pageDigests.Count == 0)
            throw new InvalidDataException("Mesh payload must contain at least one complete page before its BVH.");

        root.PageDigests = pageDigests;
        root.BvhLength = checked((ulong)bvhLength);
        root.BvhSha256 = SHA256.HashData(payload[bvhOffset..]);
    }

    private static void ValidateAuthenticatedDescriptor(
        int pageIndex,
        in MeshPayloadPage actual,
        MeshPayloadPageDigest authenticated)
    {
        Vec3 origin = authenticated.QuantOrigin
            ?? throw new InvalidDataException($"Mesh page digest {pageIndex} has no quantization origin.");
        if (authenticated.Length != checked((uint)actual.Size) ||
            authenticated.ClusterCount != actual.ClusterCount ||
            !SameFloat(origin.X, actual.QuantOrigin.X) ||
            !SameFloat(origin.Y, actual.QuantOrigin.Y) ||
            !SameFloat(origin.Z, actual.QuantOrigin.Z) ||
            !SameFloat(authenticated.QuantStep, actual.QuantStep))
        {
            throw new InvalidDataException(
                $"Mesh page header {pageIndex} does not match its root-authenticated layout descriptor.");
        }
    }

    private static ReadOnlyMemory<byte> RequireSha256(Memory<byte>? value, string subject)
    {
        if (!value.HasValue || value.Value.Length != SHA256.HashSizeInBytes)
            throw new InvalidDataException($"{subject} must carry exactly one SHA-256 digest.");
        return value.Value;
    }

    private static bool SameFloat(float left, float right)
        => BitConverter.SingleToInt32Bits(left) == BitConverter.SingleToInt32Bits(right);

}
