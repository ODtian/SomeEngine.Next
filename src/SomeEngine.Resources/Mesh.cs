using System.Collections.Immutable;
using SomeEngine.Resources.Geometry;

namespace SomeEngine.Resources;

/// <summary>Geometry descriptions and ready immutable arrays. Data views have ordinary GC lifetimes.</summary>
[SomeEngine.Serialization.AssetUse]
public sealed class Mesh
{
    private readonly object _gate = new();
    private ReadOnlyMemory<byte>[] _pages = [];
    private ReadOnlyMemory<byte> _bvh;
    private Mesh? _capturedContent;
    public string? Name { get; private set; }
    public Bounds? Bounds { get; private set; }
    public string VertexEncoding { get; private set; } = "";
    public ImmutableArray<MeshPayloadPageDigest> PageDigests { get; private set; } = [];
    public ImmutableArray<uint> LowestDetailPageIndices { get; private set; } = [];
    public ImmutableArray<MeshRegion> Regions { get; private set; } = [];
    /// <summary>Source geometry cluster for every draw cluster in page order.</summary>
    public ImmutableArray<uint> ClusterGeometryIndices { get; private set; } = [];
    public ImmutableArray<byte> BvhSha256 { get; private set; } = [];
    public ulong BvhOffset { get; private set; }
    public ulong BvhLength => checked((ulong)_bvh.Length);
    public ulong Revision { get; private set; } = 1;
    public int PageCount => PageDigests.Length;
    public int BvhByteLength => _bvh.Length;
    public ReadOnlyMemory<byte> Bvh => _bvh;
    public int GetPageByteLength(int page) => checked((int)PageDigests[page].Length);

    public Mesh CaptureData()
    {
        lock (_gate) return _capturedContent ??= Duplicate();
    }
    public Mesh Duplicate()
    {
        lock (_gate)
            return new Mesh
            {
                Name = Name, Bounds = Bounds, VertexEncoding = VertexEncoding, PageDigests = PageDigests,
                LowestDetailPageIndices = LowestDetailPageIndices, Regions = Regions, ClusterGeometryIndices = ClusterGeometryIndices, BvhSha256 = BvhSha256,
                BvhOffset = BvhOffset, Revision = Revision, _bvh = _bvh,
                _pages = (ReadOnlyMemory<byte>[])_pages.Clone(),
            };
    }

    public static Mesh Create(string name, string vertexEncoding, ReadOnlyMemory<byte> payload,
        ulong bvhOffset, Bounds? bounds = null)
    {
        var descriptions = MeshIntegrity.Inspect(payload, checked((long)bvhOffset), vertexEncoding);
        var pages = new ReadOnlyMemory<byte>[descriptions.Length];
        for (int index = 0; index < descriptions.Length; index++)
        {
            MeshPayloadPageDigest page = descriptions[index];
            pages[index] = payload.Slice(checked((int)page.Offset), checked((int)page.Length)).ToArray();
        }
        return Create(name, vertexEncoding, descriptions, payload[checked((int)bvhOffset)..].ToArray(), pages, bounds);
    }

    public static Mesh Create(string name, string vertexEncoding, IReadOnlyList<MeshPayloadPageDigest> descriptions,
        ReadOnlyMemory<byte> bvh, IReadOnlyList<ReadOnlyMemory<byte>>? readyPages = null, Bounds? bounds = null,
        IReadOnlyList<MeshRegion>? regions = null, IReadOnlyList<uint>? lowestDetailPageIndices = null,
        IReadOnlyList<uint>? clusterGeometryIndices = null)
    {
        ImmutableArray<MeshPayloadPageDigest> pages = descriptions.ToImmutableArray();
        ImmutableArray<uint> lowest = lowestDetailPageIndices?.ToImmutableArray() ?? [checked((uint)pages.Length - 1u)];
        long offset = MeshIntegrity.Validate(pages, bvh.Length, vertexEncoding);
        uint previous = uint.MaxValue;
        foreach (uint index in lowest)
        {
            if (index >= pages.Length || previous != uint.MaxValue && index <= previous)
                throw new InvalidDataException("Lowest-detail mesh pages must be unique, ordered, and in range.");
            previous = index;
        }
        if (lowest.IsEmpty) throw new InvalidDataException("A mesh requires at least one lowest-detail page.");
        var bytes = new ReadOnlyMemory<byte>[pages.Length];
        if (readyPages is not null)
        {
            if (readyPages.Count != pages.Length) throw new InvalidDataException("Ready geometry must match the page descriptions.");
            for (int index = 0; index < bytes.Length; index++)
            {
                bytes[index] = readyPages[index];
                if (!bytes[index].IsEmpty) MeshIntegrity.ValidatePage(bytes[index], pages[index]);
            }
        }
        return new Mesh
        {
            Name = name, VertexEncoding = vertexEncoding, Bounds = bounds, PageDigests = pages,
            LowestDetailPageIndices = lowest, Regions = regions?.ToImmutableArray() ?? [],
            ClusterGeometryIndices = clusterGeometryIndices?.ToImmutableArray() ?? [],
            BvhOffset = checked((ulong)offset), BvhSha256 = MeshIntegrity.Hash(bvh), _bvh = bvh, _pages = bytes,
        };
    }

    public bool TryGetPage(int page, out ReadOnlyMemory<byte> bytes)
    {
        lock (_gate) { bytes = _pages[page]; return !bytes.IsEmpty; }
    }
    public void ReadPage(int page, Span<byte> destination)
    {
        lock (_gate)
        {
            if (_pages[page].IsEmpty) throw new InvalidOperationException("Acquire the geometry page before reading it.");
            if (destination.Length != _pages[page].Length) throw new ArgumentException("Destination must match the page length.", nameof(destination));
            _pages[page].Span.CopyTo(destination);
        }
    }
    public void ReadBvh(Span<byte> destination)
    {
        if (destination.Length != _bvh.Length) throw new ArgumentException("Destination must match the BVH length.", nameof(destination));
        _bvh.Span.CopyTo(destination);
    }
    public void AddPages(IReadOnlyList<(int Index, ReadOnlyMemory<byte> Bytes)> pages, ulong expectedRevision)
    {
        bool changed = false;
        lock (_gate)
        {
            if (Revision != expectedRevision) throw new InvalidOperationException("The mesh changed while its pages were being acquired.");
            var replacement = (ReadOnlyMemory<byte>[])_pages.Clone();
            foreach (var page in pages)
            {
                if (!replacement[page.Index].IsEmpty) continue;
                MeshIntegrity.ValidatePage(page.Bytes, PageDigests[page.Index]);
                replacement[page.Index] = page.Bytes;
                changed = true;
            }
            if (changed)
            {
                _pages = replacement;
                Revision = checked(Revision + 1);
                _capturedContent = null;
            }
        }
    }
    public void Update(Mesh replacement)
    {
        if (ReferenceEquals(this, replacement)) throw new ArgumentException("A mesh cannot replace itself.", nameof(replacement));
        Mesh data = replacement.Duplicate();
        lock (_gate)
        {
            Name = data.Name; Bounds = data.Bounds; VertexEncoding = data.VertexEncoding;
            PageDigests = data.PageDigests; LowestDetailPageIndices = data.LowestDetailPageIndices;
            Regions = data.Regions; ClusterGeometryIndices = data.ClusterGeometryIndices; BvhSha256 = data.BvhSha256; BvhOffset = data.BvhOffset;
            _pages = data._pages; _bvh = data._bvh;
            Revision = checked(Revision + 1); _capturedContent = null;
        }
    }
}
