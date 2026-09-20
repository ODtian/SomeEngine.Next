using System.Runtime.InteropServices;
using SomeEngine.Assets;
using SomeEngine.Resources.Geometry;
using SomeEngine.Assets.Pipeline;
using SomeEngine.Assets.Schema;
using SomeEngine.Resources;
using SomeEngine.Render.Assets;
using SomeEngine.Render.Cluster;
using SomeEngine.Serialization.IO;

namespace SomeEngine.Render.Cluster.Tests;

public sealed class MeshStreamingTests
{
    private const int PageBytes = ClusterTestAssets.PageBytes;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BvhReadFailureOrCancellationReleasesTheSourceAndAllowsRetry(bool cancel)
    {
        using var directory = new TemporaryDirectory();
        string path = System.IO.Path.Combine(directory.Path, "failed.mesh.asset");
        using Mesh mesh = TwoPageMesh();
        ClusterTestFiles.WriteMesh(mesh, path);
        var source = new ControlledRangeSource(FileRangeSource.Open(path), mesh.BvhByteLength);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();
        source.BeforeRead = async (_, _, token) =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(token);
            throw new IOException("Injected required-BVH read failure.");
        };
        source.Arm();
        Task<MeshFileContent> opening = ClusterTestFiles.OpenMeshAsync(source, true, cancellation.Token).AsTask();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if (cancel) cancellation.Cancel();
            else release.TrySetResult();
            if (cancel) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => opening);
            else await Assert.ThrowsAsync<IOException>(() => opening);
            await Assert.ThrowsAsync<ObjectDisposedException>(() => source.ReadExactlyAsync(0, new byte[1]).AsTask());
            using var reopened = await ClusterTestFiles.OpenMeshAsync(FileRangeSource.Open(path), true);
            Assert.Equal(mesh.BvhByteLength, reopened.Mesh.BvhByteLength);
            Assert.False(reopened.Mesh.TryRetainPage(0, out _));
        }
        finally { cancellation.Cancel(); release.TrySetResult(); }
    }

    [Fact]
    public async Task StreamedOpenAndOneHundredDuplicateFaultsReadOnlyOneRequestedPage()
    {
        using var directory = new TemporaryDirectory();
        Mesh sourceAsset = TwoPageMesh();
        Guid guid = Guid.NewGuid();
        string path = System.IO.Path.Combine(directory.Path, "streamed.mesh.asset");
        ClusterTestFiles.WriteMesh(sourceAsset, path);
        var counted = new CountingRangeSource(FileRangeSource.Open(path));
        using var content = await ClusterTestFiles.OpenMeshAsync(
            counted,
            ownsSource: true);
        Mesh streamed = content.Mesh;
        Assert.False(streamed.TryRetainPage(0, out _));
        Assert.Equal(2, streamed.PageCount);
        Assert.Equal(3 * Marshal.SizeOf<ClusterBVHNode>(), streamed.BvhByteLength);

        RangeRead[] metadataReads = counted.Reads;
        Assert.DoesNotContain(metadataReads, static read => read.Length == MeshPageHeader.Size);

        Assert.True(content.PageSource.Matches(streamed));
        using var manager = new ClusterMeshes();
        int readCountBeforeRegistration = counted.Reads.Length;
        ClusterMeshRegistration registration = await manager.AddMeshAsync(streamed, pageSource: content.PageSource);

        RangeRead[] registeredReads = counted.Reads;
        Assert.Equal(readCountBeforeRegistration, registeredReads.Length);
        Assert.Contains(metadataReads, read => read.Length == streamed.BvhByteLength);
        Assert.Same(streamed, registration.Mesh);
        Assert.Equal(2u, registration.PageCount);
        ClusterMeshesSnapshot registrationSnapshot = manager.CaptureSnapshot();
        Assert.Equal(2u, registrationSnapshot.Pages.Registered);
        Assert.Equal(2u, registrationSnapshot.Pages.Missing);
        Assert.Equal(0, registrationSnapshot.Pages.UncompletedLoads);
        Assert.Equal(0u, registrationSnapshot.Heap.UsedBytes);
        CompletePublication(manager);
        Assert.True(manager.TryGetPublishedRoot(streamed, out uint publishedRoot));
        uint requestedLeafNode = checked(publishedRoot - 2);

        counted.Clear();
        using var pageStream = new PageStream(manager);
        var duplicateFaults = new uint[100];
        Array.Fill(duplicateFaults, requestedLeafNode);
        pageStream.Push(new PageFaultRead(manager.EpochId, 100, duplicateFaults));
        pageStream.Update();
        PageStreamSnapshot faultSnapshot = pageStream.CaptureSnapshot();
        Assert.Equal(100ul, faultSnapshot.LastUpdate.ReportedFaults);
        Assert.Equal(100ul, faultSnapshot.LastUpdate.StoredFaults);
        Assert.Equal(1u, faultSnapshot.LastUpdate.UniqueLeafNodeIndices);
        Assert.Equal(1u, faultSnapshot.LastUpdate.KnownLeafNodeIndices);

        bool staged = false;
        PageStreamSnapshot stagedSnapshot = pageStream.CaptureSnapshot();
        for (int attempt = 0; attempt < 500; attempt++)
        {
            pageStream.Update();
            stagedSnapshot = pageStream.CaptureSnapshot();
            if (stagedSnapshot.LastUpdate.StagedPages == 1)
            {
                staged = true;
                break;
            }
            await Task.Delay(10);
        }

        Assert.True(staged);
        Assert.Equal(1u, stagedSnapshot.LastUpdate.StagedPages);
        Assert.Equal(0ul, stagedSnapshot.Totals.LoadFailures);

        RangeRead read = Assert.Single(counted.Reads);
        Assert.Equal(PageBytes, read.Length);
        Assert.Equal(1, counted.DirectReadCalls);
        Assert.Equal(0, counted.AcquireCalls);
    }

    [Fact]
    public async Task RootAuthenticatedLayoutRejectsHeaderPageAndBvhCorruptionBeforePublication()
    {
        using var directory = new TemporaryDirectory();
        string path = System.IO.Path.Combine(directory.Path, "integrity.mesh.asset");
        Mesh asset = TwoPageMesh();
        ClusterTestFiles.WriteMesh(asset, path);

        var probe = new CountingRangeSource(FileRangeSource.Open(path));
        using (var valid = await ClusterTestFiles.OpenMeshAsync(probe, ownsSource: true))
        {
            Assert.All(valid.Mesh.PageDigests, static page => Assert.Equal(32, page.Sha256.Length));
            var page = new byte[valid.Mesh.GetPageByteLength(0)];
            await valid.PageSource.ReadPageIntoAsync(0, page);
            Assert.Equal(PageBytes, page.Length);
        }

        RangeRead firstPageRead = probe.Reads
            .Where(static read => read.Length == PageBytes)
            .OrderBy(static read => read.Offset)
            .First();
        long firstPageOffset = firstPageRead.Offset;
        long bvhOffset = checked(firstPageOffset + (long)asset.BvhOffset);
        int bvhLength = asset.BvhByteLength;

        var headerCorruption = new TamperingRangeSource(
            FileRangeSource.Open(path),
            firstPageOffset,
            PageBytes,
            static bytes =>
            {
                int encodingHashOffset = checked((int)Marshal.OffsetOf<MeshPageHeader>(
                    nameof(MeshPageHeader.VertexEncodingHash)));
                bytes[encodingHashOffset] ^= 0x01;
            });
        using (var corrupted = await ClusterTestFiles.OpenMeshAsync(
            headerCorruption,
            ownsSource: true))
        {
            var page = new byte[corrupted.Mesh.GetPageByteLength(0)];
            await Assert.ThrowsAsync<InvalidDataException>(async () =>
            {
                await corrupted.PageSource.ReadPageIntoAsync(0, page);
            });
        }

        var pageCorruption = new TamperingRangeSource(
            FileRangeSource.Open(path),
            firstPageOffset,
            PageBytes,
            static bytes => bytes[MeshPageHeader.Size + PackedCluster.SizeInBytes] ^= 0x01);
        using (var corrupted = await ClusterTestFiles.OpenMeshAsync(
            pageCorruption,
            ownsSource: true))
        {
            var page = new byte[corrupted.Mesh.GetPageByteLength(0)];
            await Assert.ThrowsAsync<InvalidDataException>(async () =>
            {
                await corrupted.PageSource.ReadPageIntoAsync(0, page);
            });
        }

        var bvhCorruption = new TamperingRangeSource(
            FileRangeSource.Open(path),
            bvhOffset,
            bvhLength,
            static bytes => bytes[^1] ^= 0x01);
        // The required BVH must be authenticated before a usable Mesh can be returned.
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await ClusterTestFiles.OpenMeshAsync(bvhCorruption, ownsSource: true));
    }

    private static void CompletePublication(ClusterMeshes manager)
    {
        Assert.True(manager.SubmitPending());
    }



    private static Mesh TwoPageMesh()
    {
        byte[] first = PageData(marker: 0x11);
        byte[] second = PageData(marker: 0x22);
        ClusterBVHNode firstLeaf = Leaf(localPage: 0);
        ClusterBVHNode secondLeaf = Leaf(localPage: 1);
        var root = new ClusterBVHNode
        {
            ChildPointer = 0,
            ChildCount = 2,
            NodeType = 0,
        };
        ClusterBVHNode[] nodes = [firstLeaf, secondLeaf, root];

        byte[] payload = new byte[(2 * PageBytes) + (nodes.Length * Marshal.SizeOf<ClusterBVHNode>())];
        first.CopyTo(payload, 0);
        second.CopyTo(payload, PageBytes);
        MemoryMarshal.AsBytes(nodes.AsSpan()).CopyTo(payload.AsSpan(2 * PageBytes));
        return Mesh.Create("RangeStreamedMesh", ClusterTestAssets.StandardVertexEncoding, payload.AsMemory(), 2 * PageBytes,
            new Bounds { Center = new Vec3(), Radius = 1f });
    }

    private static byte[] PageData(byte marker)
        => ClusterTestAssets.CreatePageData(marker);

    private static ClusterBVHNode Leaf(uint localPage)
    {
        var leaf = new ClusterBVHNode
        {
            ChildPointer = localPage,
            NodeType = 1,
        };
        leaf.SetLeafData(0, 1);
        return leaf;
    }

    private readonly record struct RangeRead(long Offset, int Length);

    private sealed class CountingRangeSource : IRangeSource
    {
        private readonly IRangeSource _inner;
        private readonly object _readsGate = new();
        private readonly List<RangeRead> _reads = [];
        private int _directReadCalls;
        private int _acquireCalls;

        internal CountingRangeSource(IRangeSource inner)
        {
            _inner = inner;
        }

        public long Length => _inner.Length;
        public string Generation => _inner.Generation;
        public bool LeasesAreImmutable => _inner.LeasesAreImmutable;
        public bool RetainsResidentBacking => _inner.RetainsResidentBacking;
        internal RangeRead[] Reads
        {
            get
            {
                lock (_readsGate)
                {
                    var snapshot = new RangeRead[_reads.Count];
                    _reads.CopyTo(snapshot);
                    return snapshot;
                }
            }
        }
        internal int DirectReadCalls => Volatile.Read(ref _directReadCalls);
        internal int AcquireCalls => Volatile.Read(ref _acquireCalls);

        public async ValueTask ReadExactlyAsync(
            long offset,
            Memory<byte> destination,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _directReadCalls);
            AddRead(new RangeRead(offset, destination.Length));
            await _inner.ReadExactlyAsync(offset, destination, cancellationToken).ConfigureAwait(false);
        }

        public ValueTask<RangeLease> AcquireAsync(
            long offset,
            int length,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _acquireCalls);
            AddRead(new RangeRead(offset, length));
            return _inner.AcquireAsync(offset, length, cancellationToken);
        }

        public ValueTask DisposeAsync() => _inner.DisposeAsync();

        internal void Clear()
        {
            lock (_readsGate)
                _reads.Clear();
            Volatile.Write(ref _directReadCalls, 0);
            Volatile.Write(ref _acquireCalls, 0);
        }

        private void AddRead(RangeRead read)
        {
            lock (_readsGate)
                _reads.Add(read);
        }
    }

    private delegate void Tamper(Span<byte> bytes);

    private sealed class TamperingRangeSource : IRangeSource
    {
        private readonly IRangeSource _inner;
        private readonly long _targetOffset;
        private readonly int _targetLength;
        private readonly Tamper _tamper;

        internal TamperingRangeSource(
            IRangeSource inner,
            long targetOffset,
            int targetLength,
            Tamper tamper)
        {
            _inner = inner;
            _targetOffset = targetOffset;
            _targetLength = targetLength;
            _tamper = tamper;
        }

        public long Length => _inner.Length;
        public string Generation => _inner.Generation;
        public bool LeasesAreImmutable => _inner.LeasesAreImmutable;
        public bool RetainsResidentBacking => _inner.RetainsResidentBacking;

        public async ValueTask ReadExactlyAsync(
            long offset,
            Memory<byte> destination,
            CancellationToken cancellationToken = default)
        {
            await _inner.ReadExactlyAsync(offset, destination, cancellationToken);
            if (offset != _targetOffset || destination.Length != _targetLength)
                return;

            _tamper(destination.Span);
        }

        public async ValueTask<RangeLease> AcquireAsync(
            long offset,
            int length,
            CancellationToken cancellationToken = default)
        {
            RangeLease lease = await _inner.AcquireAsync(offset, length, cancellationToken);
            if (offset != _targetOffset || length != _targetLength)
                return lease;

            using (lease)
            {
                byte[] bytes = GC.AllocateUninitializedArray<byte>(length);
                lease.Memory.CopyTo(bytes);
                _tamper(bytes);
                return RangeLease.Borrow(bytes);
            }
        }

        public ValueTask DisposeAsync() => _inner.DisposeAsync();
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        internal TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"SomeEngine-MeshStreaming-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        internal string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
                Directory.Delete(Path, recursive: true);
        }
    }
}
