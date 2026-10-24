using SomeEngine.Assets.Pipeline;
using SomeEngine.Resources.Geometry;
using SomeEngine.Serialization;
using SomeEngine.Serialization.Containers;
using SomeEngine.Serialization.IO;

namespace SomeEngine.Assets.Schema;

/// <summary>Cluster geometry, its authenticated page descriptions and lazily acquired payload.</summary>
[AssetBlock("SomeEngine.Assets.Schema.ClusterMeshBlock"), BinarySerializable]
[BinaryFields(nameof(VertexEncoding), nameof(PayloadKey), nameof(PayloadLength), nameof(BvhOffset),
    nameof(PageDigests), nameof(LowestDetailPageIndices), nameof(BvhLength), nameof(BvhSha256))]
public sealed partial class ClusterMeshBlock : AssetBlock
{
    public static ulong PayloadChunkKey { get; } = BinaryFieldKey.FromName("SomeEngine.Assets.Schema.Mesh.Payload");
    public partial string VertexEncoding { get; set; }
    public partial ulong PayloadKey { get; set; }
    public partial ulong PayloadLength { get; set; }
    public partial ulong BvhOffset { get; set; }
    public partial IList<MeshPayloadPageDigest> PageDigests { get; set; }
    public partial IList<uint> LowestDetailPageIndices { get; set; }
    public partial ulong BvhLength { get; set; }
    public partial ImmutableArray<byte> BvhSha256 { get; set; }

    partial void Initialize()
    {
        PayloadKey = PayloadChunkKey;
        BvhSha256 = [];
    }

    /// <summary>Assigns completed geometry pages and derives their integrity metadata.</summary>
    public void SetPayload(ReadOnlyMemory<byte> payload, ulong bvhOffset)
    {
        ReadOnlyMemory<byte> bytes = payload;
        lock (SyncRoot)
        {
            RequireIdle();
            var descriptions = MeshIntegrity.Inspect(bytes, checked((long)bvhOffset), VertexEncoding);
            ReadOnlyMemory<byte> bvh = bytes[checked((int)bvhOffset)..];
            ImmutableArray<byte> digest = MeshIntegrity.Hash(bvh);
            SetBytes(PayloadChunkKey, bytes);
            PayloadKey = PayloadChunkKey;
            PayloadLength = checked((ulong)bytes.Length);
            BvhOffset = bvhOffset;
            BvhLength = checked((ulong)bvh.Length);
            BvhSha256 = digest;
            PageDigests = descriptions;
            if (LowestDetailPageIndices.Count == 0)
                LowestDetailPageIndices = [checked((uint)descriptions.Length - 1u)];
        }
    }

    protected override void ValidateContent()
    {
        if (Chunks.TryGetValue(PayloadChunkKey, out ReadOnlyMemory<byte> bytes) && (ulong)bytes.Length != PayloadLength)
            throw new InvalidDataException("Mesh payload length differs from its declaration.");
        if (PayloadKey != PayloadChunkKey || BvhLength > int.MaxValue
            || BvhOffset >= PayloadLength || PayloadLength - BvhOffset != BvhLength
            || BvhSha256.IsDefault || BvhSha256.Length != Digest256.Size)
            throw new InvalidDataException("Mesh payload or BVH metadata is invalid.");
        long offset = MeshIntegrity.Validate(PageDigests.ToImmutableArray(), checked((long)BvhLength), VertexEncoding);
        if (checked((ulong)offset) != BvhOffset)
            throw new InvalidDataException("Mesh page descriptions do not fill the region before the BVH.");
        uint previous = uint.MaxValue;
        foreach (uint pageIndex in LowestDetailPageIndices)
        {
            if (pageIndex >= PageDigests.Count || previous != uint.MaxValue && pageIndex <= previous)
                throw new InvalidDataException("Lowest-detail mesh page indices must be unique, ordered, and in range.");
            previous = pageIndex;
        }
        if (LowestDetailPageIndices.Count == 0)
            throw new InvalidDataException("A cluster mesh requires at least one lowest-detail page.");
    }

    /// <summary>Retains future geometry page access independently of this block and its asset.</summary>
    public async ValueTask<MeshPayloadSource> OpenPayloadSourceAsync(CancellationToken cancellationToken = default)
    {
        using ClusterMeshBlock copy = Duplicate();
        copy.ValidateContent();
        return await OpenPayloadSourceAsync(copy, cancellationToken).ConfigureAwait(false);
    }

    private MeshPayloadData? _geometry;
    private Guid _geometryVersion;
    partial void CopyFrom(ClusterMeshBlock source)
    {
        _geometry = source.GetGeometry();
        _geometryVersion = source._geometryVersion;
    }
    private MeshPayloadData GetGeometry()
    {
        lock (SyncRoot)
        {
            if (_geometry is null || _geometryVersion != DataVersion
                || !PageDigests.SequenceEqual(_geometry.Digests)
                || !LowestDetailPageIndices.SequenceEqual(_geometry.LowestDetailPages))
            {
                _geometry = new(PageDigests, LowestDetailPageIndices, checked((long)BvhOffset), checked((int)BvhLength), BvhSha256.AsMemory());
                _geometryVersion = DataVersion;
            }
            return _geometry;
        }
    }
    private static ValueTask<MeshPayloadSource> OpenPayloadSourceAsync(ClusterMeshBlock copy, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        MeshPayloadData data = copy.GetGeometry();
        if (copy.Chunks.TryGetValue(PayloadChunkKey, out var bytes))
            return ValueTask.FromResult(new MeshPayloadSource(bytes, data));
        using AssetBlockSource source = copy.RetainSource() ?? throw new InvalidDataException("Cluster mesh block has no payload.");
        return ValueTask.FromResult(new MeshPayloadSource(source, data));
    }

    /// <summary>Acquires the BVH and lowest-detail geometry; finer pages are loaded explicitly.</summary>
    public async ValueTask<Mesh> CreateAsync(MeshBlock geometry, string name, CancellationToken cancellationToken = default)
    {
        using ClusterMeshBlock copy = Duplicate();
        using MeshBlock shape = geometry.Duplicate();
        copy.ValidateContent();
        using MeshPayloadSource source = await OpenPayloadSourceAsync(copy, cancellationToken).ConfigureAwait(false);
        ReadOnlyMemory<byte> bvh = await source.ReadBvhAsync(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        var ready = new ReadOnlyMemory<byte>[copy.PageDigests.Count];
        foreach (uint index in copy.LowestDetailPageIndices)
            ready[index] = await source.ReadPageAsync(checked((int)index), cancellationToken).ConfigureAwait(false);
        return Mesh.Create(name, copy.VertexEncoding, copy.PageDigests.ToImmutableArray(), bvh, ready,
            lowestDetailPageIndices: copy.LowestDetailPageIndices.ToArray(),
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
        var pages = new List<(int Index, ReadOnlyMemory<byte> Bytes)>();
        foreach (int index in pageIndices.Distinct())
        {
            if (mesh.TryGetPage(index, out _)) continue;
            ReadOnlyMemory<byte> bytes = await source.ReadPageAsync(index, cancellationToken).ConfigureAwait(false);
            pages.Add((index, bytes));
        }
        cancellationToken.ThrowIfCancellationRequested();
        mesh.AddPages(pages, revision);
    }

    protected override async ValueTask WriteChunksAsync(AssetBlockWriter writer, CancellationToken cancellationToken)
    {
        if (Chunks.ContainsKey(PayloadChunkKey))
        {
            writer.WriteChunk(PayloadChunkKey, Chunks[PayloadChunkKey], ChunkCompression.None, alignment: 4096);
            return;
        }
        using AssetBlockSource source = RetainSource() ?? throw new InvalidDataException("Cluster mesh block has no payload.");
        await writer.CopyChunkAsync(source, PayloadChunkKey, cancellationToken).ConfigureAwait(false);
    }
}
