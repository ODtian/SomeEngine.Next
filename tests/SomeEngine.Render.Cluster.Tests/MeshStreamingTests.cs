using System.Numerics;
using System.Runtime.InteropServices;
using SomeEngine.Assets;
using SomeEngine.Assets.Data;
using SomeEngine.Assets.Pipeline;
using SomeEngine.Assets.Schema;
using SomeEngine.Render.Assets;
using SomeEngine.Render.Cluster;
using SomeEngine.Serialization.IO;

namespace SomeEngine.Render.Cluster.Tests;

public sealed class MeshStreamingTests
{
    private const int PositionBytes = 3 * sizeof(ushort);
    private const int IndexBytes = 3;
    private const int PageBytes = MeshPageHeader.Size + GPUCluster.SizeInBytes + PositionBytes + IndexBytes;

    [Fact]
    public async Task StreamedOpenAndOneHundredDuplicateFaultsReadOnlyOneRequestedPage()
    {
        using var directory = new TemporaryDirectory();
        Mesh sourceAsset = TwoPageMesh();
        AssetGuid guid = AssetGuid.New();
        string path = System.IO.Path.Combine(directory.Path, "streamed.mesh.asset");
        AssetWriter.Write(sourceAsset, path);
        var counted = new CountingRangeSource(FileRangeSource.Open(path));
        using Mesh streamed = await Mesh.OpenStreamedAsync(
            counted,
            ownsSource: true);
        Assert.Null(streamed.Payload);
        using MeshPayloadSource payloadSource = RetainPayloadSource(streamed);
        Assert.Equal(2, payloadSource.Pages.Count);
        Assert.Equal(3 * Marshal.SizeOf<ClusterBVHNode>(), payloadSource.BvhLength);

        RangeRead[] metadataReads = counted.Reads;
        Assert.DoesNotContain(metadataReads, static read => read.Length == MeshPageHeader.Size);

        var handle = new AssetHandle<Mesh>(1, 1);
        Assert.True(streamed.IsStreamed);
        using var manager = new ClusterMeshes();
        int readCountBeforeRegistration = counted.Reads.Length;
        ClusterMeshRegistration registration = await manager.AddMeshAsync(handle, streamed);

        RangeRead[] registeredReads = counted.Reads;
        Assert.Equal(readCountBeforeRegistration + 1, registeredReads.Length);
        Assert.Equal(payloadSource.BvhLength, registeredReads[readCountBeforeRegistration].Length);
        Assert.Equal(handle, registration.Mesh);
        Assert.Equal(2u, registration.PageCount);
        ClusterMeshesSnapshot registrationSnapshot = manager.CaptureSnapshot();
        Assert.Equal(2u, registrationSnapshot.Pages.Registered);
        Assert.Equal(2u, registrationSnapshot.Pages.Missing);
        Assert.Equal(0, registrationSnapshot.Pages.UncompletedLoads);
        Assert.Equal(0u, registrationSnapshot.Heap.UsedBytes);
        CompletePublication(manager);
        Assert.True(manager.TryGetPublishedRoot(handle, out uint publishedRoot));
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
        AssetWriter.Write(asset, path);

        var probe = new CountingRangeSource(FileRangeSource.Open(path));
        using (Mesh valid = await Mesh.OpenStreamedAsync(probe, ownsSource: true))
        {
            using MeshPayloadSource source = RetainPayloadSource(valid);
            Assert.All(source.Pages, static page => Assert.Equal(32, page.Sha256.Length));
            Assert.True(MemoryMarshal.TryGetArray(
                (ReadOnlyMemory<byte>)valid.PageDigests![0].Sha256!.Value,
                out ArraySegment<byte> rootDigest));
            Assert.True(MemoryMarshal.TryGetArray(source.Pages[0].Sha256, out ArraySegment<byte> sourceDigest));
            Assert.Same(rootDigest.Array, sourceDigest.Array);
            Assert.Equal(rootDigest.Offset, sourceDigest.Offset);
            Assert.Equal(rootDigest.Count, sourceDigest.Count);
            var page = new byte[source.Pages[0].Size];
            await source.ReadPageIntoAsync(0, page);
            Assert.Equal(PageBytes, page.Length);
        }

        RangeRead firstPageRead = probe.Reads
            .Where(static read => read.Length == PageBytes)
            .OrderBy(static read => read.Offset)
            .First();
        long firstPageOffset = firstPageRead.Offset;
        long bvhOffset = checked(firstPageOffset + (long)asset.BvhOffset);
        int bvhLength = checked((int)(asset.Payload!.Value.Length - (long)asset.BvhOffset));

        var headerCorruption = new TamperingRangeSource(
            FileRangeSource.Open(path),
            firstPageOffset,
            PageBytes,
            static bytes =>
            {
                int quantStepOffset = checked((int)Marshal.OffsetOf<MeshPageHeader>(nameof(MeshPageHeader.QuantStep)));
                bytes[quantStepOffset] ^= 0x01;
            });
        using (Mesh corrupted = await Mesh.OpenStreamedAsync(
            headerCorruption,
            ownsSource: true))
        using (MeshPayloadSource source = RetainPayloadSource(corrupted))
        {
            var page = new byte[source.Pages[0].Size];
            await Assert.ThrowsAsync<InvalidDataException>(async () =>
            {
                await source.ReadPageIntoAsync(0, page);
            });
        }

        var pageCorruption = new TamperingRangeSource(
            FileRangeSource.Open(path),
            firstPageOffset,
            PageBytes,
            static bytes => bytes[MeshPageHeader.Size + GPUCluster.SizeInBytes] ^= 0x01);
        using (Mesh corrupted = await Mesh.OpenStreamedAsync(
            pageCorruption,
            ownsSource: true))
        using (MeshPayloadSource source = RetainPayloadSource(corrupted))
        {
            var page = new byte[source.Pages[0].Size];
            await Assert.ThrowsAsync<InvalidDataException>(async () =>
            {
                await source.ReadPageIntoAsync(0, page);
            });
        }

        var bvhCorruption = new TamperingRangeSource(
            FileRangeSource.Open(path),
            bvhOffset,
            bvhLength,
            static bytes => bytes[^1] ^= 0x01);
        using (Mesh corrupted = await Mesh.OpenStreamedAsync(
            bvhCorruption,
            ownsSource: true))
        using (MeshPayloadSource source = RetainPayloadSource(corrupted))
        {
            var bvh = new byte[source.BvhLength];
            await Assert.ThrowsAsync<InvalidDataException>(async () =>
            {
                await source.ReadBvhIntoAsync(bvh);
            });
        }
    }

    private static void CompletePublication(ClusterMeshes manager)
    {
        Assert.True(manager.PublishPending());
    }

    private static MeshPayloadSource RetainPayloadSource(Mesh mesh)
    {
        Assert.True(mesh.TryRetainPayloadSource(out MeshPayloadSource? source));
        return Assert.IsType<MeshPayloadSource>(source);
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
        return new Mesh
        {
            Name = "RangeStreamedMesh",
            Bounds = new Bounds { Center = new Vec3(), Radius = 1f },
            Attributes = [],
            Payload = payload,
            BvhOffset = 2 * PageBytes,
            QuantStep = 1f,
        };
    }

    private static byte[] PageData(byte marker)
    {
        byte[] data = new byte[PageBytes];
        var header = new MeshPageHeader
        {
            ClusterCount = 1,
            TotalVertexCount = 1,
            TotalTriangleCount = 1,
            ClustersOffset = MeshPageHeader.Size,
            PositionsOffset = MeshPageHeader.Size + GPUCluster.SizeInBytes,
            AttributesOffset = MeshPageHeader.Size + GPUCluster.SizeInBytes + PositionBytes,
            IndicesOffset = MeshPageHeader.Size + GPUCluster.SizeInBytes + PositionBytes,
            QuantStep = 1f,
        };
        MemoryMarshal.Write(data.AsSpan(0, MeshPageHeader.Size), in header);
        var cluster = new GPUCluster
        {
            PackedCounts = 1u | (1u << 8),
            MaterialTableOffset = uint.MaxValue,
            BoundMax = new Vector3(marker),
        };
        MemoryMarshal.Write(data.AsSpan(MeshPageHeader.Size, GPUCluster.SizeInBytes), in cluster);
        data[^1] = 0;
        return data;
    }

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
