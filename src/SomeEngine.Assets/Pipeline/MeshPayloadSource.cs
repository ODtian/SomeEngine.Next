using SomeEngine.Assets.Data;
using SomeEngine.Assets.Schema;
using SomeEngine.Serialization;
using SomeEngine.Serialization.Containers;
using SomeEngine.Serialization.IO;

namespace SomeEngine.Assets.Pipeline;

/// <summary>
/// Reference-counted access to the nonresident mesh payload chunk. Each read targets the caller's
/// final destination directly; this object retains only range metadata and authenticated digests.
/// </summary>
internal sealed class MeshPayloadSource : IDisposable
{
    private readonly object _gate = new();
    private SharedState? _state;

    internal MeshPayloadSource(
        BinaryDocument<Mesh> document,
        IRangeSource payload,
        long bvhOffset,
        int bvhLength,
        ReadOnlyMemory<byte> bvhSha256,
        IList<MeshPayloadPageDigest> pageDigests)
        : this(new SharedState(document, payload, bvhOffset, bvhLength, bvhSha256, pageDigests))
    {
    }

    private MeshPayloadSource(SharedState state)
    {
        _state = state;
    }

    internal IReadOnlyList<MeshPayloadPage> Pages => State.Pages;

    internal int BvhLength => State.BvhLength;

    internal long Length => State.Payload.Length;

    internal MeshPayloadSource Retain()
    {
        lock (_gate)
        {
            SharedState state = State;
            state.AddReference();
            try { return new MeshPayloadSource(state); }
            catch { state.Release(); throw; }
        }
    }

    /// <summary>
    /// Reads one authenticated page directly into storage supplied by the publication owner.
    /// The source never allocates or retains a second page-sized buffer.
    /// </summary>
    internal async ValueTask ReadPageIntoAsync(
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

        await state.Payload.ReadExactlyAsync(
            page.Offset,
            destination,
            cancellationToken).ConfigureAwait(false);
        if (!Digest256.ComputeSha256(destination.Span).FixedTimeEquals(page.Sha256.Span))
        {
            throw new InvalidDataException(
                $"Mesh page {pageIndex} bytes do not match the root-authenticated SHA-256 digest.");
        }
    }

    /// <summary>Reads and authenticates the BVH directly into its final publication storage.</summary>
    internal async ValueTask ReadBvhIntoAsync(
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

        await state.Payload.ReadExactlyAsync(
            state.BvhOffset,
            destination,
            cancellationToken).ConfigureAwait(false);
        if (!Digest256.ComputeSha256(destination.Span).FixedTimeEquals(state.BvhSha256))
        {
            throw new InvalidDataException(
                "Mesh BVH bytes do not match the root-authenticated SHA-256 digest.");
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _state?.Release();
            _state = null;
        }
    }

    private SharedState State
        => Volatile.Read(ref _state)
            ?? throw new ObjectDisposedException(nameof(MeshPayloadSource));

    private sealed class SharedState
    {
        private readonly object _gate = new();
        private readonly BinaryDocument<Mesh> _document;
        private ReadOnlyMemory<byte> _bvhSha256;
        private int _references = 1;

        internal SharedState(
            BinaryDocument<Mesh> document,
            IRangeSource payload,
            long bvhOffset,
            int bvhLength,
            ReadOnlyMemory<byte> bvhSha256,
            IList<MeshPayloadPageDigest> pageDigests)
        {
            _document = document;
            Payload = payload;
            BvhOffset = bvhOffset;
            BvhLength = bvhLength;
            _bvhSha256 = bvhSha256.ToArray();
            Pages = new AuthenticatedPageList(pageDigests);
        }

        internal IRangeSource Payload { get; }
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
            lock (_gate)
            {
                if (_references == 0) return;
                if (_references > 1)
                {
                    _references--;
                    return;
                }
                // Keep the final reference until both underlying disposals finish. A caller
                // whose cleanup failed can retry without losing the only source record.
                Payload.DisposeAsync().AsTask().GetAwaiter().GetResult();
                _document.DisposeAsync().AsTask().GetAwaiter().GetResult();
                _bvhSha256 = default;
                _references = 0;
            }
        }
    }

    private sealed class AuthenticatedPageList : IReadOnlyList<MeshPayloadPage>
    {
        private readonly MeshPayloadPage[] _pages;
        internal AuthenticatedPageList(IList<MeshPayloadPageDigest> digests)
        {
            _pages = new MeshPayloadPage[digests.Count];
            for (int index = 0; index < _pages.Length; index++)
            {
                MeshPayloadPageDigest digest = digests[index]
                    ?? throw new InvalidDataException($"Mesh page digest {index} is null.");
                ReadOnlyMemory<byte> sha256 = digest.Sha256
                    ?? throw new InvalidDataException($"Mesh page digest {index} has no SHA-256 digest.");
                _pages[index] = new MeshPayloadPage(
                    checked((long)digest.Offset), checked((int)digest.Length),
                    digest.ClusterCount, digest.VertexEncodingHash, sha256.ToArray());
            }
        }
        public int Count => _pages.Length;
        public MeshPayloadPage this[int index] => _pages[index];
        public IEnumerator<MeshPayloadPage> GetEnumerator() => ((IEnumerable<MeshPayloadPage>)_pages).GetEnumerator();
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
