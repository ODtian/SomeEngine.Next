using SomeEngine.Resources.Geometry;
using SomeEngine.Assets.Schema;
using SomeEngine.Resources;
using SomeEngine.Serialization;
using SomeEngine.Serialization.Containers;
using SomeEngine.Serialization.IO;

namespace SomeEngine.Assets.Pipeline;

/// <summary>
/// Explicit access to mesh content, retaining either a file range or supplied resident pages.
/// Reads into caller storage avoid an intermediate page buffer; acquired pages share the common arena.
/// </summary>
public sealed class MeshPayloadSource : IDisposable
{
    private SharedState? _state;

    internal MeshPayloadSource(
        AssetBlockSource source,
        IRangeSource payload,
        long bvhOffset,
        int bvhLength,
        ReadOnlyMemory<byte> bvhSha256,
        IList<MeshPayloadPageDigest> pageDigests)
        : this(new SharedState(source, payload, bvhOffset, bvhLength, bvhSha256, pageDigests))
    {
    }

    internal MeshPayloadSource(PageBuffer payload, long bvhOffset, int bvhLength,
        ReadOnlyMemory<byte> bvhSha256, IList<MeshPayloadPageDigest> pageDigests)
        : this(new SharedState(payload, bvhOffset, bvhLength, bvhSha256, pageDigests)) { }

    private MeshPayloadSource(SharedState state)
    {
        _state = state;
    }

    /// <summary>Retains complete procedural geometry for explicit page reads without a file.</summary>
    public static MeshPayloadSource FromMesh(Mesh mesh)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        Mesh copy = mesh.Duplicate();
        try
        {
            for (int index = 0; index < copy.PageCount; index++)
            {
                if (!copy.TryRetainPage(index, out var read))
                    throw new InvalidOperationException("Missing geometry requires an explicit asset source.");
                read.Dispose();
            }
            return new(new SharedState(copy));
        }
        catch { copy.Dispose(); throw; }
    }

    public bool Matches(Mesh mesh)
    {
        using MeshPayloadSource access = Retain();
        using Mesh content = mesh.Duplicate();
        return MeshIntegrity.SamePages(content.PageDigests, access.State.Digests)
            && content.BvhLength == (ulong)access.BvhLength
            && content.BvhSha256.AsSpan().SequenceEqual(access.State.BvhSha256);
    }

    public IReadOnlyList<MeshPayloadPage> Pages => State.Pages;

    public int BvhLength => State.BvhLength;

    public long Length => State.Length;

    public MeshPayloadSource Retain()
    {
        SharedState state = State;
        state.AddReference();
        try
        {
            return new MeshPayloadSource(state);
        }
        catch
        {
            state.Release();
            throw;
        }
    }

    /// <summary>
    /// Reads one authenticated page directly into storage supplied by the caller.
    /// The source never allocates or retains a second page-sized buffer.
    /// </summary>
    public async ValueTask ReadPageIntoAsync(
        int pageIndex,
        Memory<byte> destination,
        CancellationToken cancellationToken = default)
    {
        using MeshPayloadSource lifetime = Retain();
        SharedState state = lifetime.State;
        if ((uint)pageIndex >= (uint)state.Pages.Count)
            throw new ArgumentOutOfRangeException(nameof(pageIndex));

        MeshPayloadPage page = state.Pages[pageIndex];
        if (destination.Length != page.Size)
        {
            throw new ArgumentException(
                $"Mesh page {pageIndex} requires exactly {page.Size} destination bytes.",
                nameof(destination));
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (state.Mesh is { } mesh) mesh.ReadPage(pageIndex, destination.Span);
        else await state.ReadExactlyAsync(page.Offset, destination, cancellationToken).ConfigureAwait(false);
        if (!Digest256.ComputeSha256(destination.Span).FixedTimeEquals(page.Sha256.Span))
        {
            throw new InvalidDataException(
                $"Mesh page {pageIndex} bytes do not match the root-authenticated SHA-256 digest.");
        }
    }

    /// <summary>Reads and authenticates the BVH directly into its final publication storage.</summary>
    public async ValueTask ReadBvhIntoAsync(
        Memory<byte> destination,
        CancellationToken cancellationToken = default)
    {
        using MeshPayloadSource lifetime = Retain();
        SharedState state = lifetime.State;
        if (destination.Length != state.BvhLength)
        {
            throw new ArgumentException(
                $"Mesh BVH requires exactly {state.BvhLength} destination bytes.",
                nameof(destination));
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (state.Mesh is { } mesh) mesh.ReadBvh(destination.Span);
        else await state.ReadExactlyAsync(state.BvhOffset, destination, cancellationToken).ConfigureAwait(false);
        if (!Digest256.ComputeSha256(destination.Span).FixedTimeEquals(state.BvhSha256))
        {
            throw new InvalidDataException(
                "Mesh BVH bytes do not match the root-authenticated SHA-256 digest.");
        }
    }

    public void Dispose()
        => Interlocked.Exchange(ref _state, null)?.Release();

    public async ValueTask<PageBuffer> AcquirePageAsync(int pageIndex, PageArena pages,
        CancellationToken cancellationToken = default)
    {
        using MeshPayloadSource access = Retain();
        MeshPayloadPage page = access.Pages[pageIndex];
        cancellationToken.ThrowIfCancellationRequested();
        PageBuffer bytes = access.State.Mesh?.DuplicatePage(pageIndex)
            ?? await access.State.ReadPagesAsync(page.Offset, page.Size, pages, cancellationToken).ConfigureAwait(false);
        try
        {
            using PageRange<byte> read = bytes.Retain();
            if (!System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(MeshIntegrity.Hash(read).AsSpan(), page.Sha256.Span))
                throw new InvalidDataException("Mesh page bytes do not match their authenticated SHA-256 digest.");
            return bytes;
        }
        catch { bytes.Dispose(); throw; }
    }

    public async ValueTask<PageBuffer> AcquireBvhAsync(PageArena pages, CancellationToken cancellationToken = default)
    {
        using MeshPayloadSource access = Retain();
        SharedState state = access.State;
        cancellationToken.ThrowIfCancellationRequested();
        PageBuffer bytes = state.Mesh?.DuplicateBvh()
            ?? await state.ReadPagesAsync(state.BvhOffset, state.BvhLength, pages, cancellationToken).ConfigureAwait(false);
        try
        {
            using PageRange<byte> read = bytes.Retain();
            if (!System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(MeshIntegrity.Hash(read).AsSpan(), state.BvhSha256))
                throw new InvalidDataException("Mesh BVH bytes do not match their authenticated SHA-256 digest.");
            return bytes;
        }
        catch { bytes.Dispose(); throw; }
    }

    private SharedState State
        => Volatile.Read(ref _state)
            ?? throw new ObjectDisposedException(nameof(MeshPayloadSource));

    private sealed class SharedState
    {
        private readonly object _gate = new();
        private readonly AssetBlockSource? _source;
        private readonly PageBuffer? _bytes;
        private ReadOnlyMemory<byte> _bvhSha256;
        private int _references = 1;

        internal SharedState(
            AssetBlockSource source,
            IRangeSource payload,
            long bvhOffset,
            int bvhLength,
            ReadOnlyMemory<byte> bvhSha256,
            IList<MeshPayloadPageDigest> pageDigests)
        {
            _source = source;
            Payload = payload;
            BvhOffset = bvhOffset;
            BvhLength = bvhLength;
            _bvhSha256 = bvhSha256;
            Pages = new AuthenticatedPageList(pageDigests.ToImmutableArray());
        }

        internal SharedState(PageBuffer payload, long bvhOffset, int bvhLength,
            ReadOnlyMemory<byte> bvhSha256, IList<MeshPayloadPageDigest> pageDigests)
        {
            _bytes = payload;
            BvhOffset = bvhOffset;
            BvhLength = bvhLength;
            _bvhSha256 = bvhSha256;
            Pages = new AuthenticatedPageList(pageDigests.ToImmutableArray());
        }

        internal SharedState(Mesh mesh)
        {
            Mesh = mesh;
            BvhOffset = checked((long)mesh.BvhOffset);
            BvhLength = mesh.BvhByteLength;
            _bvhSha256 = mesh.BvhSha256.AsMemory();
            Pages = new AuthenticatedPageList(mesh.PageDigests);
        }

        internal IRangeSource? Payload { get; }
        internal Mesh? Mesh { get; }
        internal ImmutableArray<MeshPayloadPageDigest> Digests => ((AuthenticatedPageList)Pages).Digests;
        internal long Length => Mesh is not null ? checked(BvhOffset + BvhLength) : _bytes?.Length ?? Payload!.Length;
        internal ValueTask ReadExactlyAsync(long offset, Memory<byte> destination, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_bytes is null) return Payload!.ReadExactlyAsync(offset, destination, cancellationToken);
            _bytes.Read(offset, destination.Span);
            return ValueTask.CompletedTask;
        }
        internal ValueTask<PageBuffer> ReadPagesAsync(long offset, int length, PageArena pages, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return _bytes is null ? PageBuffer.ReadAsync(Payload!, offset, length, pages, cancellationToken)
                : ValueTask.FromResult(_bytes.Slice(offset, length));
        }
        internal IReadOnlyList<MeshPayloadPage> Pages { get; }

        internal long BvhOffset { get; }
        internal int BvhLength { get; }
        internal ReadOnlySpan<byte> BvhSha256
        {
            get
            {
                ReadOnlyMemory<byte> value = _bvhSha256;
                ObjectDisposedException.ThrowIf(value.IsEmpty, this);
                return value.Span;
            }
        }

        internal void AddReference()
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_references == 0, this);
                _references = checked(_references + 1);
            }
        }

        internal void Release()
        {
            bool dispose;
            lock (_gate)
            {
                if (_references == 0)
                    return;
                _references--;
                dispose = _references == 0;
            }

            if (!dispose)
                return;

            _bvhSha256 = default;
            try
            {
                _bytes?.Dispose();
                Mesh?.Dispose();
                Payload?.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
            finally { _source?.Dispose(); }
        }
    }

    private sealed class AuthenticatedPageList(ImmutableArray<MeshPayloadPageDigest> digests)
        : IReadOnlyList<MeshPayloadPage>
    {
        internal ImmutableArray<MeshPayloadPageDigest> Digests => digests;
        public int Count => digests.Length;

        public MeshPayloadPage this[int index]
        {
            get
            {
                MeshPayloadPageDigest digest = digests[index]
                    ?? throw new InvalidDataException($"Mesh page digest {index} is null.");
                ReadOnlyMemory<byte> sha256 = digest.Sha256.AsMemory();
                return new MeshPayloadPage(
                    checked((long)digest.Offset),
                    checked((int)digest.Length),
                    digest.ClusterCount,
                    digest.VertexEncodingHash,
                    sha256);
            }
        }

        public IEnumerator<MeshPayloadPage> GetEnumerator()
        {
            for (int index = 0; index < Count; index++)
                yield return this[index];
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator()
            => GetEnumerator();
    }
}
