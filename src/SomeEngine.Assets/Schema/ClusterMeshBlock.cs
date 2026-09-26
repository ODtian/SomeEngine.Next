using SomeEngine.Assets.Pipeline;
using SomeEngine.Resources.Geometry;
using SomeEngine.Serialization;
using SomeEngine.Serialization.Containers;
using SomeEngine.Serialization.IO;

namespace SomeEngine.Assets.Schema;

/// <summary>Cluster geometry, its authenticated page descriptions and lazily acquired payload.</summary>
[AssetBlock("SomeEngine.Assets.Schema.ClusterMeshBlock"), BinarySerializable]
[BinaryFields(nameof(VertexEncoding), nameof(PayloadKey), nameof(PayloadLength), nameof(BvhOffset),
    nameof(PageDigests), nameof(BvhLength), nameof(BvhSha256))]
public sealed partial class ClusterMeshBlock : AssetPageBlock
{
    public static ulong PayloadChunkKey { get; } = BinaryFieldKey.FromName("SomeEngine.Assets.Schema.Mesh.Payload");
    public partial string VertexEncoding { get; set; }
    public partial ulong PayloadKey { get; set; }
    public partial ulong PayloadLength { get; set; }
    public partial ulong BvhOffset { get; set; }
    public partial IList<MeshPayloadPageDigest> PageDigests { get; set; }
    public partial ulong BvhLength { get; set; }
    public partial ImmutableArray<byte> BvhSha256 { get; set; }

    partial void Initialize()
    {
        PayloadKey = PayloadChunkKey;
        BvhSha256 = [];
    }

    /// <summary>Assigns completed geometry pages and derives their integrity metadata.</summary>
    public void SetPayload(PageBuffer payload, ulong bvhOffset)
    {
        using PageBuffer bytes = payload.Duplicate();
        lock (Access.SyncRoot)
        {
            RequireIdle();
            var descriptions = MeshIntegrity.Inspect(bytes, checked((long)bvhOffset), VertexEncoding);
            using PageRange<byte> bvh = bytes.Retain(checked((long)bvhOffset), bytes.Length - checked((long)bvhOffset));
            ImmutableArray<byte> digest = MeshIntegrity.Hash(bvh);
            Chunks.Set(PayloadChunkKey, bytes);
            PayloadKey = PayloadChunkKey;
            PayloadLength = checked((ulong)bytes.Length);
            BvhOffset = bvhOffset;
            BvhLength = checked((ulong)bvh.Sequence.Length);
            BvhSha256 = digest;
            PageDigests = descriptions;
        }
    }

    protected override void ValidateContent()
    {
        if (Chunks.Contains(PayloadChunkKey))
        {
            using PageBuffer bytes = Chunks.DuplicateBuffer(PayloadChunkKey);
            SetPayload(bytes, BvhOffset);
        }
        if (PayloadKey != PayloadChunkKey || BvhLength > int.MaxValue
            || BvhOffset >= PayloadLength || PayloadLength - BvhOffset != BvhLength
            || BvhSha256.IsDefault || BvhSha256.Length != Digest256.Size)
            throw new InvalidDataException("Mesh payload or BVH metadata is invalid.");
        long offset = MeshIntegrity.Validate(PageDigests.ToImmutableArray(), checked((long)BvhLength), VertexEncoding);
        if (checked((ulong)offset) != BvhOffset)
            throw new InvalidDataException("Mesh page descriptions do not fill the region before the BVH.");
    }

    /// <summary>Retains future geometry page access independently of this block and its asset.</summary>
    public async ValueTask<MeshPayloadSource> OpenPayloadSourceAsync(CancellationToken cancellationToken = default)
    {
        using ClusterMeshBlock copy = Duplicate();
        copy.ValidateContent();
        return await OpenPayloadSourceAsync(copy, cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<MeshPayloadSource> OpenPayloadSourceAsync(ClusterMeshBlock copy, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ImmutableArray<MeshPayloadPageDigest> descriptions = copy.PageDigests.ToImmutableArray();
        if (copy.Chunks.Contains(PayloadChunkKey))
        {
            PageBuffer bytes = copy.Chunks.DuplicateBuffer(PayloadChunkKey);
            try { return new(bytes, checked((long)copy.BvhOffset), checked((int)copy.BvhLength), copy.BvhSha256.AsMemory(), descriptions); }
            catch { bytes.Dispose(); throw; }
        }
        AssetBlockSource blockSource = copy.RetainSource() ?? throw new InvalidDataException("Cluster mesh block has no payload.");
        IRangeSource? source = null;
        try
        {
            source = await blockSource.OpenChunkRangeSourceAsync(PayloadChunkKey, cancellationToken).ConfigureAwait(false);
            if (checked((ulong)source.Length) != copy.PayloadLength)
                throw new InvalidDataException("Mesh payload length differs from the block declaration.");
            return new(blockSource, source, checked((long)copy.BvhOffset), checked((int)copy.BvhLength), copy.BvhSha256.AsMemory(), descriptions);
        }
        catch
        {
            try { if (source is not null) await source.DisposeAsync().ConfigureAwait(false); }
            finally { blockSource.Dispose(); }
            throw;
        }
    }

    /// <summary>Acquires the BVH; geometry pages remain available through explicit page reads.</summary>
    public async ValueTask<Mesh> CreateAsync(MeshBlock geometry, string name, CancellationToken cancellationToken = default)
    {
        using ClusterMeshBlock copy = Duplicate();
        using MeshBlock shape = geometry.Duplicate();
        copy.ValidateContent();
        using MeshPayloadSource source = await OpenPayloadSourceAsync(copy, cancellationToken).ConfigureAwait(false);
        using PageBuffer bvh = await source.AcquireBvhAsync(copy.Pages, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return Mesh.Create(name, copy.VertexEncoding, copy.PageDigests.ToImmutableArray(), bvh,
            bounds: shape.Bounds, regions: shape.Regions.ToImmutableArray());
    }

    public async ValueTask LoadPagesAsync(Mesh mesh, IEnumerable<int> pageIndices, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        ArgumentNullException.ThrowIfNull(pageIndices);
        using ClusterMeshBlock copy = Duplicate();
        copy.ValidateContent();
        ulong revision = mesh.Revision;
        if (mesh.VertexEncoding != copy.VertexEncoding || !MeshIntegrity.SamePages(mesh.PageDigests, copy.PageDigests.ToImmutableArray())
            || !mesh.BvhSha256.AsSpan().SequenceEqual(copy.BvhSha256.AsSpan()))
            throw new InvalidOperationException("Mesh content differs; update the mesh before acquiring more pages.");
        using MeshPayloadSource source = await OpenPayloadSourceAsync(copy, cancellationToken).ConfigureAwait(false);
        using var pages = new PageBufferMap(copy.Pages);
        foreach (int index in pageIndices.Distinct())
        {
            if (mesh.TryRetainPage(index, out PageRange<byte>? ready)) { ready.Dispose(); continue; }
            using PageBuffer bytes = await source.AcquirePageAsync(index, copy.Pages, cancellationToken).ConfigureAwait(false);
            pages.Set(checked((ulong)index), bytes);
        }
        cancellationToken.ThrowIfCancellationRequested();
        mesh.AddPages(pages, revision);
    }

    protected override async ValueTask WriteChunksAsync(AssetBlockWriter writer, CancellationToken cancellationToken)
    {
        if (Chunks.Contains(PayloadChunkKey))
        {
            writer.WriteChunk(PayloadChunkKey, Chunks, ChunkCompression.None, alignment: 4096);
            return;
        }
        using AssetBlockSource source = RetainSource() ?? throw new InvalidDataException("Cluster mesh block has no payload.");
        await writer.CopyChunkAsync(source, PayloadChunkKey, cancellationToken).ConfigureAwait(false);
    }
}
