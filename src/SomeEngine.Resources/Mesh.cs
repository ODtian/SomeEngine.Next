using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using SomeEngine.Resources.Geometry;
using SomeEngine.Serialization.Containers;

namespace SomeEngine.Resources;

/// <summary>Geometry descriptions and ready pages. Missing pages require an explicit asset-side acquisition.</summary>
[SomeEngine.Serialization.AssetUse]
public sealed class Mesh : IDisposable, IAsyncDisposable
{
    private const ulong BvhKey = ulong.MaxValue;
    private readonly object _gate = new();
    private PageBufferMap? _buffers;
    private Mesh() { }
    private readonly List<Mesh> _capturedContents = [];
    private Mesh? _capturedContent;
    public event Action<Mesh>? Changed;

    /// <summary>Shares ready pages at the extraction boundary, independent of later updates.</summary>
    public Mesh CaptureData()
    {
        lock (_gate)
        {
            RequireOpen();
            if (_capturedContent is not null && _capturedContent.Revision == Revision) return _capturedContent;
            Mesh content = Duplicate();
            _capturedContents.Add(content);
            return _capturedContent = content;
        }
    }
    public string? Name { get; private set; }
    public Bounds? Bounds { get; private set; }
    public string VertexEncoding { get; private set; } = "";
    public ImmutableArray<MeshPayloadPageDigest> PageDigests { get; private set; } = [];
    public ImmutableArray<uint> LowestDetailPageIndices { get; private set; } = [];
    public ImmutableArray<MeshRegion> Regions { get; private set; } = [];
    public ImmutableArray<byte> BvhSha256 { get; private set; } = [];
    public ulong BvhOffset { get; private set; }
    public ulong BvhLength { get; private set; }
    public ulong Revision { get; private set; } = 1;
    public int PageCount => PageDigests.Length;
    public int BvhByteLength => checked((int)BvhLength);
    public bool IsDisposed => Volatile.Read(ref _buffers) is null;
    public PageArena Pages => (_buffers ?? throw new ObjectDisposedException(nameof(Mesh))).Arena;
    public int GetPageByteLength(int page) => checked((int)PageDigests[page].Length);

    /// <summary>Shares the current ready pages; subsequent additions and updates are independent.</summary>
    public Mesh Duplicate()
    {
        lock (_gate)
        {
            RequireOpen();
            return new Mesh
            {
                Name = Name, Bounds = Bounds, VertexEncoding = VertexEncoding, PageDigests = PageDigests,
                LowestDetailPageIndices = LowestDetailPageIndices,
                Regions = Regions, BvhSha256 = BvhSha256, BvhOffset = BvhOffset, BvhLength = BvhLength,
                Revision = Revision, _buffers = _buffers!.Duplicate(),
            };
        }
    }

    internal PageBuffer DuplicatePage(int page)
    {
        lock (_gate)
        {
            RequireOpen();
            if ((uint)page >= (uint)PageCount) throw new ArgumentOutOfRangeException(nameof(page));
            return _buffers!.DuplicateBuffer((ulong)page);
        }
    }

    internal PageBuffer DuplicateBvh()
    {
        lock (_gate) { RequireOpen(); return _buffers!.DuplicateBuffer(BvhKey); }
    }

    public static Mesh Create(string name, string vertexEncoding, Memory<byte> payload, ulong bvhOffset, Bounds? bounds = null)
    {
        using var bytes = new PageBuffer(PageArena.Shared, payload.Length);
        bytes.Write(0, payload.Span);
        return Create(name, vertexEncoding, bytes, bvhOffset, bounds);
    }
    public static Mesh Create(string name, string vertexEncoding, PageBuffer payload, ulong bvhOffset, Bounds? bounds = null)
    {
        using PageBuffer content = payload.Duplicate();
        var descriptions = MeshIntegrity.Inspect(content, checked((long)bvhOffset), vertexEncoding);
        using var pages = new PageBufferMap(content.Arena);
        for (int index = 0; index < descriptions.Length; index++)
        {
            MeshPayloadPageDigest page = descriptions[index];
            using PageBuffer bytes = content.Slice(checked((long)page.Offset), page.Length);
            pages.Set(checked((ulong)index), bytes);
        }
        using PageBuffer bvh = content.Slice(checked((long)bvhOffset), content.Length - checked((long)bvhOffset));
        return Create(name, vertexEncoding, descriptions, bvh, pages, bounds);
    }
    public static Mesh Create(string name, string vertexEncoding, IReadOnlyList<MeshPayloadPageDigest> descriptions,
        PageBuffer bvh, PageBufferMap? readyPages = null, Bounds? bounds = null,
        IReadOnlyList<MeshRegion>? regions = null, IReadOnlyList<uint>? lowestDetailPageIndices = null)
    {
        ImmutableArray<MeshPayloadPageDigest> pages = descriptions.ToImmutableArray();
        ImmutableArray<uint> lowestDetailPages = lowestDetailPageIndices?.ToImmutableArray()
            ?? [checked((uint)pages.Length - 1u)];
        long offset = MeshIntegrity.Validate(pages, bvh.Length, vertexEncoding);
        uint previous = uint.MaxValue;
        foreach (uint pageIndex in lowestDetailPages)
        {
            if (pageIndex >= pages.Length || previous != uint.MaxValue && pageIndex <= previous)
                throw new InvalidDataException("Lowest-detail mesh page indices must be unique, ordered, and in range.");
            previous = pageIndex;
        }
        if (lowestDetailPages.IsEmpty)
            throw new InvalidDataException("A mesh requires at least one lowest-detail page.");
        using PageRange<byte> bvhRead = bvh.Retain();
        var buffers = readyPages?.Duplicate() ?? new PageBufferMap(bvh.Arena);
        try
        {
            foreach (ulong key in buffers.Keys)
            {
                if (key >= (ulong)pages.Length) throw new InvalidDataException("Ready mesh page index is outside the descriptions.");
                using PageBuffer bytes = buffers.DuplicateBuffer(key);
                MeshIntegrity.ValidatePage(bytes, pages[(int)key]);
            }
            buffers.Set(BvhKey, bvh);
            return new Mesh
            {
                Name = name, VertexEncoding = vertexEncoding, Bounds = bounds, PageDigests = pages,
                LowestDetailPageIndices = lowestDetailPages,
                Regions = regions?.ToImmutableArray() ?? [],
                BvhOffset = checked((ulong)offset), BvhLength = checked((ulong)bvh.Length),
                BvhSha256 = MeshIntegrity.Hash(bvhRead), _buffers = buffers,
            };
        }
        catch { buffers.Dispose(); throw; }
    }

    public bool TryRetainPage(int page, [NotNullWhen(true)] out PageRange<byte>? bytes)
    {
        lock (_gate)
        {
            RequireOpen();
            if ((uint)page >= (uint)PageCount) throw new ArgumentOutOfRangeException(nameof(page));
            bytes = _buffers!.Contains((ulong)page) ? _buffers.Retain((ulong)page) : null;
            return bytes is not null;
        }
    }
    public PageRange<byte> RetainBvh()
    {
        lock (_gate) { RequireOpen(); return _buffers!.Retain(BvhKey); }
    }
    public void ReadPage(int page, Span<byte> destination)
    {
        lock (_gate)
        {
            RequireOpen();
            if (destination.Length != GetPageByteLength(page)) throw new ArgumentException("Destination must match the page length.", nameof(destination));
            _buffers!.Read(checked((ulong)page), 0, destination);
        }
    }
    public void ReadBvh(Span<byte> destination)
    {
        lock (_gate)
        {
            RequireOpen();
            if (destination.Length != BvhByteLength) throw new ArgumentException("Destination must match the BVH length.", nameof(destination));
            _buffers!.Read(BvhKey, 0, destination);
        }
    }
    public void AddPages(PageBufferMap pages, ulong expectedRevision)
    {
        using PageBufferMap input = pages.Duplicate();
        lock (_gate)
        {
            RequireOpen();
            if (Revision != expectedRevision) throw new InvalidOperationException("The mesh changed while its pages were being acquired.");
            PageBufferMap replacement = _buffers!.Duplicate();
            try
            {
                foreach (ulong key in input.Keys)
                {
                    if (key >= (ulong)PageCount) throw new InvalidDataException("Mesh page index is outside the descriptions.");
                    if (replacement.Contains(key)) continue;
                    using PageBuffer bytes = input.DuplicateBuffer(key);
                    MeshIntegrity.ValidatePage(bytes, PageDigests[(int)key]);
                    replacement.Set(key, bytes);
                }
            }
            catch { replacement.Dispose(); throw; }
            PageBufferMap previous = _buffers;
            _buffers = replacement;
            previous.Dispose();
        }
    }

    public void Update(Mesh replacement)
    {
        ArgumentNullException.ThrowIfNull(replacement);
        if (ReferenceEquals(this, replacement)) throw new ArgumentException("A mesh cannot replace itself.", nameof(replacement));
        (string? Name, Bounds? Bounds, string Encoding, ImmutableArray<MeshPayloadPageDigest> Pages,
            ImmutableArray<uint> LowestDetailPages, ImmutableArray<MeshRegion> Regions,
            ImmutableArray<byte> BvhHash, ulong BvhOffset, ulong BvhLength, PageBufferMap Buffers) content;
        lock (replacement._gate)
        {
            replacement.RequireOpen();
            content = (replacement.Name, replacement.Bounds, replacement.VertexEncoding, replacement.PageDigests,
                replacement.LowestDetailPageIndices, replacement.Regions, replacement.BvhSha256,
                replacement.BvhOffset, replacement.BvhLength, replacement._buffers!.Duplicate());
        }
        PageBufferMap previous;
        try
        {
            lock (_gate)
            {
                RequireOpen();
                ulong revision = checked(Revision + 1);
                previous = _buffers!;
                Name = content.Name; Bounds = content.Bounds; VertexEncoding = content.Encoding;
                PageDigests = content.Pages; BvhSha256 = content.BvhHash;
                LowestDetailPageIndices = content.LowestDetailPages;
                Regions = content.Regions;
                BvhOffset = content.BvhOffset; BvhLength = content.BvhLength;
                _buffers = content.Buffers;
                Revision = revision;
            }
        }
        catch { content.Buffers.Dispose(); throw; }
        previous.Dispose();
        Changed?.Invoke(this);
    }
    private void RequireOpen() => ObjectDisposedException.ThrowIf(_buffers is null, this);
    public void Dispose()
    {
        lock (_gate)
        {
            Interlocked.Exchange(ref _buffers, null)?.Dispose();
            foreach (Mesh content in _capturedContents) content.Dispose();
            _capturedContents.Clear();
            _capturedContent = null;
        }
    }
    public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
}
