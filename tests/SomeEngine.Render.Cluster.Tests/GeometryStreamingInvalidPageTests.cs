using System.Numerics;
using SomeEngine.Assets;
using SomeEngine.Assets.Schema;
using SomeEngine.Core.Math;
using SomeEngine.Render.Cluster.Pipeline;
using SomeEngine.Serialization.IO;

namespace SomeEngine.Render.Cluster.Tests;

[Trait("Category", "Hardware")]
public sealed class GeometryStreamingInvalidPageTests(Xunit.Abstractions.ITestOutputHelper output)
{
    [Fact]
    public async Task ACorruptDetailPageKeepsCoarseCoverageWithoutBlockingHealthyPages()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"SomeEngine-CorruptPage-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var corruptReads = new DetailReads();
        var healthyReads = new DetailReads();
        var corruptRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var left = new TransformQvvs(new Vector3(-1.25f, 0, -4), Quaternion.Identity);
            var right = new TransformQvvs(new Vector3(1.25f, 0, -4), Quaternion.Identity);
            var options = new ClusterPipelineOptions
                { MaxCandidates = 1024, ForceHardwareRaster = true, EnableFrameMetricsReadback = true };
            RasterImage leftCoarseRightFine;
            await using (var reference = await FrameStageScene.CreateAsync("D3D12", false,
                options, await RasterTestGeometry.CreatePlaneAsync()))
            {
                reference.SetGeometryVisible(false);
                reference.AddGeometryMesh(await RasterTestGeometry.CreatePlaneAsync(),
                    new TransformQvvs(new Vector3(-1.25f, 0, -4.5f), Quaternion.Identity));
                reference.AddGeometryMesh(await RasterTestGeometry.CreatePlaneAsync(), right);
                leftCoarseRightFine = await reference.ReadNextRasterImageAsync();
            }

            using (MeshAsset corruptAsset = await GeometryStreamingHandoffTests.CreateStreamingPartsAsync(
                [(-1f, 1f, -.5f), (-1f, 1f, 0f)], new(0, 0, -.25f, 1.5f)))
            using (MeshAsset healthyAsset = await GeometryStreamingHandoffTests.CreateStreamingPartsAsync(
                [(-1f, 1f, -.5f), (-1f, 1f, 0f)], new(0, 0, -.25f, 1.5f)))
            {
                AssetEntry corruptEntry = await corruptAsset.SaveAsync(Path.Combine(directory, "corrupt.mesh.asset"));
                AssetEntry healthyEntry = await healthyAsset.SaveAsync(Path.Combine(directory, "healthy.mesh.asset"));
                await using var corruptLoader = new AssetLoader(new ObservedStorage(corruptEntry, corruptReads));
                await using var healthyLoader = new AssetLoader(new ObservedStorage(healthyEntry, healthyReads));
                using MeshAsset corruptFile = await MeshAsset.OpenAsync(corruptLoader, new(corruptEntry.AssetGuid));
                using MeshAsset healthyFile = await MeshAsset.OpenAsync(healthyLoader, new(healthyEntry.AssetGuid));
                ClusterMeshBlock corruptGeometry = corruptFile.GetBlock<ClusterMeshBlock>();
                ClusterMeshBlock healthyGeometry = healthyFile.GetBlock<ClusterMeshBlock>();
                var corruptMesh = await corruptGeometry.CreateAsync(corruptFile.GetBlock<MeshBlock>(), corruptFile.Name);
                var healthyMesh = await healthyGeometry.CreateAsync(healthyFile.GetBlock<MeshBlock>(), healthyFile.Name);
                using var corruptSource = await corruptGeometry.OpenPayloadSourceAsync();
                using var healthySource = await healthyGeometry.OpenPayloadSourceAsync();
                await using var scene = await FrameStageScene.CreateAsync("D3D12", false,
                    options, await RasterTestGeometry.CreatePlaneAsync(),
                    residencyOptions: new ClusterResidencyOptions
                    {
                        // Both roots and both details fit; capacity must not suppress a retry.
                        PageHeapCapacityBytes = 4096, BvhCapacityBytes = 65536,
                        PageFaultCapacity = 16, MaxConcurrentPageLoads = 1,
                    });
                scene.SetGeometryVisible(false);
                RasterImage empty = await scene.ReadNextRasterImageAsync();
                scene.AddStreamingMesh(corruptMesh, corruptSource, left);
                RasterImage coarse = await scene.ReadNextRasterImageAsync();
                Assert.NotEqual(empty.Depth, coarse.Depth);
                corruptReads.ObserveDetail(corruptMesh.GetPageByteLength(1), bytes =>
                {
                    // Alter real source bytes; normal payload authentication reports the failure.
                    bytes.Span[0] ^= 1;
                    corruptRead.TrySetResult();
                });
                healthyReads.ObserveDetail(healthyMesh.GetPageByteLength(1), static _ => { });
                scene.PumpStreaming();
                await corruptRead.Task.WaitAsync(TimeSpan.FromSeconds(20));

                scene.AddStreamingMesh(healthyMesh, healthySource, right);
                RasterImage bothCoarse = await scene.ReadNextRasterImageAsync();
                Assert.NotEqual(leftCoarseRightFine.Depth, bothCoarse.Depth);
                RasterImage.AssertEqual(leftCoarseRightFine,
                    await GeometryStreamingHandoffTests.ReadUntilDepthAsync(scene, leftCoarseRightFine.Depth));
                // A later healthy read completed through the sole loading task, so the failure
                // was processed rather than left in flight. Keep the corrupt surface in demand.
                for (int frame = 0; frame < 12; frame++)
                    RasterImage.AssertEqual(leftCoarseRightFine, await scene.ReadNextRasterImageAsync());
                Assert.Equal(1, corruptReads.Attempts);
                Assert.Equal(1, healthyReads.Attempts);

                // Explicit acquisition still reaches storage and authenticates the bad bytes.
                // The single automatic attempt above is therefore the streaming failure policy,
                // rather than a cached storage exception or an uncompleted I/O operation.
                await Assert.ThrowsAsync<InvalidDataException>(async () =>
                    await corruptGeometry.LoadPagesAsync(corruptMesh, [1]));
                Assert.Equal(2, corruptReads.Attempts);
                for (int frame = 0; frame < 3; frame++)
                    RasterImage.AssertEqual(leftCoarseRightFine, await scene.ReadNextRasterImageAsync());
                Assert.Equal(2, corruptReads.Attempts);
                Assert.Equal(1, healthyReads.Attempts);
                output.WriteLine("Corrupt detail: one automatic read, no retry across 15 completed frames; " +
                    "explicit acquisition reached storage again and threw InvalidDataException. " +
                    "A healthy detail loaded through the same single-load budget while corrupt coarse coverage remained.");
            }
            Assert.Equal(0, corruptReads.OpenSources);
            Assert.Equal(0, healthyReads.OpenSources);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private sealed class DetailReads
    {
        private int _pageBytes, _attempts, _openSources;
        private Action<Memory<byte>>? _afterRead;
        internal int Attempts => Volatile.Read(ref _attempts);
        internal int OpenSources => Volatile.Read(ref _openSources);
        internal void Opened() => Interlocked.Increment(ref _openSources);
        internal void Closed() => Interlocked.Decrement(ref _openSources);

        internal void ObserveDetail(int pageBytes, Action<Memory<byte>> afterRead)
        {
            _afterRead = afterRead;
            Volatile.Write(ref _pageBytes, pageBytes);
        }

        internal bool Observes(int length) => length == Volatile.Read(ref _pageBytes);
        internal void Complete(Memory<byte> bytes)
        {
            if (!Observes(bytes.Length)) return;
            Interlocked.Increment(ref _attempts);
            _afterRead!(bytes);
        }
    }

    private sealed class ObservedStorage(AssetEntry entry, DetailReads reads) : IAssetStorage
    {
        public bool TryFind(Guid id, out AssetEntry found) { found = entry; return id == entry.AssetGuid; }
        public ValueTask<IRangeSource> OpenAsync(AssetEntry selected, CancellationToken token = default)
        {
            IRangeSource source = FileRangeSource.Open(selected.Path);
            reads.Opened();
            return ValueTask.FromResult<IRangeSource>(new ObservedSource(source, reads));
        }
    }

    private sealed class ObservedSource(IRangeSource source, DetailReads reads) : IRangeSource
    {
        private int _disposed;
        public long Length => source.Length;
        public string Generation => source.Generation;
        public bool LeasesAreImmutable => source.LeasesAreImmutable;
        public bool RetainsResidentBacking => source.RetainsResidentBacking;

        public async ValueTask<RangeLease> AcquireAsync(long offset, int length, CancellationToken token = default)
        {
            RangeLease result = await source.AcquireAsync(offset, length, token);
            if (!reads.Observes(length)) return result;
            using (result)
            {
                byte[] bytes = result.Memory.ToArray();
                reads.Complete(bytes);
                return RangeLease.Borrow(bytes);
            }
        }

        public async ValueTask ReadExactlyAsync(long offset, Memory<byte> destination, CancellationToken token = default)
        {
            await source.ReadExactlyAsync(offset, destination, token);
            reads.Complete(destination);
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            await source.DisposeAsync();
            reads.Closed();
        }
    }
}
