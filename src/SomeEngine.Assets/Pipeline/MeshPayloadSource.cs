using System.Runtime.InteropServices;
using SomeEngine.Assets.Schema;
using SomeEngine.Resources;
using SomeEngine.Resources.Geometry;
using SomeEngine.Serialization;
using SomeEngine.Serialization.IO;

namespace SomeEngine.Assets.Pipeline;

/// <summary>Explicit geometry reads. Only file access is disposable; returned arrays remain valid independently.</summary>
public sealed class MeshPayloadSource : IDisposable
{
    private AssetBlockSource? _source;
    private ReadOnlyMemory<byte> _bytes;
    private Mesh? _mesh;
    private readonly MeshPayloadData _data;
    private bool _disposed;

    internal MeshPayloadSource(AssetBlockSource source, MeshPayloadData data)
    { _source = source.Retain(); _data = data; }
    internal MeshPayloadSource(ReadOnlyMemory<byte> bytes, MeshPayloadData data)
    { _bytes = bytes; _data = data; }
    private MeshPayloadSource(Mesh mesh)
    {
        _mesh = mesh.Duplicate();
        for (int index = 0; index < _mesh.PageCount; index++)
            if (!_mesh.TryGetPage(index, out _)) throw new InvalidOperationException("Missing geometry requires an explicit asset source.");
        _data = new(_mesh.PageDigests, _mesh.LowestDetailPageIndices, checked((long)_mesh.BvhOffset), _mesh.BvhByteLength, _mesh.BvhSha256.AsMemory());
    }
    private MeshPayloadSource(MeshPayloadSource source)
    {
        lock (source)
        {
            ObjectDisposedException.ThrowIf(source._disposed, source);
            _source = source._source?.Retain(); _bytes = source._bytes; _mesh = source._mesh; _data = source._data;
        }
    }
    public static MeshPayloadSource FromMesh(Mesh mesh) => new(mesh);
    public MeshPayloadSource Retain() => new(this);
    public IReadOnlyList<MeshPayloadPage> Pages => _data.Pages;
    public ReadOnlyMemory<uint> LowestDetailPageIndices => _data.LowestDetailPages;
    public int BvhLength => _data.Bvh.Length;
    public long Length => _data.Bvh.Offset + BvhLength;
    public bool Matches(Mesh mesh) => MeshIntegrity.SamePages(mesh.PageDigests, _data.Digests)
        && mesh.LowestDetailPageIndices.AsSpan().SequenceEqual(LowestDetailPageIndices.Span)
        && mesh.BvhSha256.AsSpan().SequenceEqual(_data.Bvh.Sha256.Span);

    public ValueTask<ReadOnlyMemory<byte>> ReadPageAsync(int index, CancellationToken cancellationToken = default)
        => ReadAsync(_data.GetPageRange(index), cancellationToken);
    public ValueTask<ReadOnlyMemory<byte>> ReadBvhAsync(CancellationToken cancellationToken = default)
        => ReadAsync(_data.Bvh, cancellationToken);
    public async ValueTask ReadPageIntoAsync(int index, Memory<byte> destination, CancellationToken cancellationToken = default)
    {
        using MeshPayloadSource source = Retain();
        await source.ReadIntoAsync(_data.GetPageRange(index), destination, cancellationToken).ConfigureAwait(false);
    }
    public async ValueTask ReadBvhIntoAsync(Memory<byte> destination, CancellationToken cancellationToken = default)
    {
        using MeshPayloadSource source = Retain();
        await source.ReadIntoAsync(_data.Bvh, destination, cancellationToken).ConfigureAwait(false);
    }
    internal async ValueTask ReadIntoAsync(MeshByteRange range, Memory<byte> destination, CancellationToken cancellationToken)
    {
        if (destination.Length != range.Length) throw new ArgumentException("The destination must match the geometry range.", nameof(destination));
        cancellationToken.ThrowIfCancellationRequested();
        if (GetCache().TryGet(GetByteKey(range), out byte[]? ready)) ready!.CopyTo(destination);
        else if (_mesh is not null)
        {
            if (range.PageIndex < 0) _mesh.ReadBvh(destination.Span);
            else _mesh.ReadPage(range.PageIndex, destination.Span);
        }
        else if (!_bytes.IsEmpty) _bytes.Slice(checked((int)range.Offset), range.Length).CopyTo(destination);
        else
        {
            await using IRangeSource input = await _source!.OpenChunkRangeSourceAsync(ClusterMeshBlock.PayloadChunkKey, cancellationToken).ConfigureAwait(false);
            if (input.Length != Length) throw new InvalidDataException("Mesh payload length differs from its declaration.");
            await input.ReadExactlyAsync(range.Offset, destination, cancellationToken).ConfigureAwait(false);
        }
        if (!Digest256.ComputeSha256(destination.Span).FixedTimeEquals(range.Sha256.Span))
            throw new InvalidDataException("Geometry bytes differ from their authenticated digest.");
    }
    internal bool TryGetArray(MeshByteRange range, out byte[]? bytes)
    {
        ReadOnlyMemory<byte> data = default;
        if (_mesh is not null)
        {
            if (range.PageIndex < 0) data = _mesh.Bvh;
            else _mesh.TryGetPage(range.PageIndex, out data);
        }
        if (!data.IsEmpty && MemoryMarshal.TryGetArray(data, out ArraySegment<byte> segment)
            && segment.Offset == 0 && segment.Count == segment.Array!.Length)
        { bytes = segment.Array; return true; }
        bytes = null; return false;
    }

    private AssetByteCache GetCache() => _source?.ByteCache ?? AssetByteCache.Shared;
    private AssetByteKey GetByteKey(MeshByteRange range) => _source is not null
        ? _source.GetByteKey(ClusterMeshBlock.PayloadChunkKey, range.Offset, range.Length)
        : new(_data.Version, ClusterMeshBlock.PayloadChunkKey, range.Offset, range.Length);

    private ValueTask<ReadOnlyMemory<byte>> ReadAsync(MeshByteRange range, CancellationToken cancellationToken)
    {
        lock (this)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            cancellationToken.ThrowIfCancellationRequested();
            if (TryGetArray(range, out byte[]? bytes)) return ValueTask.FromResult<ReadOnlyMemory<byte>>(bytes!);
            return GetCache().ReadAsync(GetByteKey(range), (Source: this, Range: range), static async input =>
            {
                using MeshPayloadSource read = input.Source.Retain();
                byte[] result = new byte[input.Range.Length];
                await read.ReadIntoAsync(input.Range, result, default).ConfigureAwait(false);
                return result;
            }, cancellationToken);
        }
    }
    public void Dispose()
    {
        lock (this)
        {
            if (_disposed) return;
            _disposed = true;
            _source?.Dispose(); _source = null; _bytes = default; _mesh = null;
        }
    }
}

/// <summary>The geometry and BVH ranges of one block version, shared by its duplicates.</summary>
internal sealed class MeshPayloadData
{
    internal Guid Version { get; } = Guid.NewGuid();
    internal MeshPayloadPageDigest[] Digests { get; }
    internal MeshPayloadPage[] Pages { get; }
    internal uint[] LowestDetailPages { get; }
    internal MeshByteRange Bvh { get; }
    internal MeshPayloadData(IEnumerable<MeshPayloadPageDigest> descriptions, IEnumerable<uint> lowestDetail,
        long bvhOffset, int bvhLength, ReadOnlyMemory<byte> bvhSha256)
    {
        Digests = descriptions.ToArray(); LowestDetailPages = lowestDetail.ToArray();
        Pages = Digests.Select(page => new MeshPayloadPage(checked((long)page.Offset), checked((int)page.Length),
            page.ClusterCount, page.VertexEncodingHash, page.Sha256.AsMemory())).ToArray();
        Bvh = new(-1, bvhOffset, bvhLength, bvhSha256);
    }

    internal MeshByteRange GetPageRange(int index)
    {
        MeshPayloadPage page = Pages[index];
        return new(index, page.Offset, page.Size, page.Sha256);
    }
}

internal readonly struct MeshByteRange(int pageIndex, long offset, int length, ReadOnlyMemory<byte> sha256)
{
    internal int PageIndex { get; } = pageIndex;
    internal long Offset { get; } = offset;
    internal int Length { get; } = length;
    internal ReadOnlyMemory<byte> Sha256 { get; } = sha256;
}
