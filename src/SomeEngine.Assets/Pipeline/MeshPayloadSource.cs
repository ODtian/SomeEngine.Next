using SomeEngine.Resources;
using SomeEngine.Resources.Geometry;

namespace SomeEngine.Assets.Pipeline;

/// <summary>Retains immutable renderer pages assembled from geometry and attribute Blocks.</summary>
public sealed class MeshPayloadSource : IDisposable
{
    private Mesh? _mesh;
    private readonly MeshPayloadPage[] _pages;
    private readonly uint[] _lowestDetail;
    private MeshPayloadSource(Mesh mesh)
    {
        _mesh = mesh.Duplicate();
        for (int index = 0; index < _mesh.PageCount; index++)
            if (!_mesh.TryGetPage(index, out _)) throw new InvalidOperationException("A page source requires complete page data.");
        _pages = _mesh.PageDigests.Select(page => new MeshPayloadPage((long)page.Offset, (int)page.Length,
            page.ClusterCount, page.VertexEncodingHash, page.Sha256.AsMemory())).ToArray();
        _lowestDetail = _mesh.LowestDetailPageIndices.ToArray();
    }
    private MeshPayloadSource(MeshPayloadSource source)
    {
        lock (source)
        {
            _mesh = source.RequireData(); _pages = source._pages; _lowestDetail = source._lowestDetail;
        }
    }
    public static MeshPayloadSource FromMesh(Mesh mesh) => new(mesh);
    public MeshPayloadSource Retain() => new(this);
    public IReadOnlyList<MeshPayloadPage> Pages => _pages;
    public ReadOnlyMemory<uint> LowestDetailPageIndices => _lowestDetail;
    public int BvhLength { get { lock (this) return RequireData().BvhByteLength; } }
    public long Length { get { lock (this) { Mesh mesh = RequireData(); return (long)mesh.BvhOffset + mesh.BvhByteLength; } } }
    public bool Matches(Mesh mesh)
    {
        lock (this)
        {
            Mesh data = RequireData();
            return MeshIntegrity.SamePages(mesh.PageDigests, data.PageDigests)
                && mesh.LowestDetailPageIndices.AsSpan().SequenceEqual(data.LowestDetailPageIndices.AsSpan())
                && mesh.BvhSha256.AsSpan().SequenceEqual(data.BvhSha256.AsSpan());
        }
    }
    public ValueTask<ReadOnlyMemory<byte>> ReadPageAsync(int index, CancellationToken cancellationToken = default)
    {
        lock (this)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RequireData().TryGetPage(index, out ReadOnlyMemory<byte> bytes);
            return ValueTask.FromResult(bytes);
        }
    }
    public ValueTask<ReadOnlyMemory<byte>> ReadBvhAsync(CancellationToken cancellationToken = default)
    {
        lock (this) { cancellationToken.ThrowIfCancellationRequested(); return ValueTask.FromResult(RequireData().Bvh); }
    }
    public ValueTask ReadPageIntoAsync(int index, Memory<byte> destination, CancellationToken cancellationToken = default)
    {
        lock (this) { cancellationToken.ThrowIfCancellationRequested(); RequireData().ReadPage(index, destination.Span); return ValueTask.CompletedTask; }
    }
    public ValueTask ReadBvhIntoAsync(Memory<byte> destination, CancellationToken cancellationToken = default)
    {
        lock (this) { cancellationToken.ThrowIfCancellationRequested(); RequireData().ReadBvh(destination.Span); return ValueTask.CompletedTask; }
    }
    private Mesh RequireData() => _mesh ?? throw new ObjectDisposedException(nameof(MeshPayloadSource));
    public void Dispose() { lock (this) _mesh = null; }
}
