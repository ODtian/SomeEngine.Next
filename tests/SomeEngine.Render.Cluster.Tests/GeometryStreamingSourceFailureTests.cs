using System.Numerics;
using SomeEngine.Assets;
using SomeEngine.Assets.Schema;
using SomeEngine.Core.Math;
using SomeEngine.Render.Cluster.Pipeline;
using SomeEngine.Serialization.IO;

namespace SomeEngine.Render.Cluster.Tests;

[Trait("Category", "Hardware")]
public sealed class GeometryStreamingSourceFailureTests(Xunit.Abstractions.ITestOutputHelper output)
{
    [Fact]
    public async Task ASourceReadFailureKeepsCoarseCoverageUntilALaterRequestRecoversDetail()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"SomeEngine-SourceRecovery-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var reads = new SourceReads();
        var firstFailure = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var retryStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var continueRetry = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var transform = new TransformQvvs(new Vector3(0, 0, -4), Quaternion.Identity);
            var options = new ClusterPipelineOptions
                { MaxCandidates = 1024, ForceHardwareRaster = true, EnableFrameMetricsReadback = true };
            RasterImage fine;
            await using (var reference = await FrameStageScene.CreateAsync("D3D12", false,
                options, await RasterTestGeometry.CreatePlaneAsync()))
            {
                reference.SetGeometryVisible(false);
                reference.AddGeometryMesh(await RasterTestGeometry.CreatePlaneAsync(), transform);
                fine = await reference.ReadNextRasterImageAsync();
            }
            using (MeshAsset asset = await GeometryStreamingHandoffTests.CreateStreamingPartsAsync(
                [(-1f, 1f, -.5f), (-1f, 1f, 0f)], new(0, 0, -.25f, 1.5f)))
            {
                AssetEntry entry = await asset.SaveAsync(Path.Combine(directory, "recovering.mesh.asset"));
                await using var loader = new AssetLoader(new ObservedStorage(entry, reads));
                using MeshAsset file = await MeshAsset.OpenAsync(loader, new(entry.AssetGuid));
                ClusterMeshBlock geometry = file.GetBlock<ClusterMeshBlock>();
                var mesh = await geometry.CreateAsync(file.GetBlock<MeshBlock>(), file.Name);
                using var source = await geometry.OpenPayloadSourceAsync();
                var scene = await FrameStageScene.CreateAsync("D3D12", false,
                    options, await RasterTestGeometry.CreatePlaneAsync(),
                    residencyOptions: new ClusterResidencyOptions
                    {
                        // One coarse and one detail page. A failed reservation must be reusable.
                        PageHeapCapacityBytes = 1024, BvhCapacityBytes = 65536,
                        PageFaultCapacity = 16, MaxConcurrentPageLoads = 1,
                    });
                try
                {
                    scene.SetGeometryVisible(false);
                    RasterImage empty = await scene.ReadNextRasterImageAsync();
                    scene.AddStreamingMesh(mesh, source, transform);
                    RasterImage coarse = await scene.ReadNextRasterImageAsync();
                    Assert.NotEqual(empty.Depth, coarse.Depth);
                    Assert.NotEqual(fine.Depth, coarse.Depth);
                    reads.ObserveDetail(mesh.GetPageByteLength(1), async (attempt, token) =>
                    {
                        if (attempt == 1)
                        {
                            firstFailure.TrySetResult();
                            throw new IOException("Injected source read failure.");
                        }
                        retryStarted.TrySetResult();
                        await continueRetry.Task.WaitAsync(token);
                    });
                    scene.PumpStreaming();
                    await firstFailure.Task.WaitAsync(TimeSpan.FromSeconds(20));
                    int coarseFrames = 0;
                    for (; coarseFrames < 24 && !retryStarted.Task.IsCompleted; coarseFrames++)
                        RasterImage.AssertEqual(coarse, await scene.ReadNextRasterImageAsync());
                    await retryStarted.Task.WaitAsync(TimeSpan.FromSeconds(20));
                    for (int frame = 0; frame < 3; frame++)
                        RasterImage.AssertEqual(coarse, await scene.ReadNextRasterImageAsync());
                    Assert.Equal(2, reads.Attempts);
                    continueRetry.TrySetResult();
                    RasterImage.AssertEqual(fine, await GeometryStreamingHandoffTests.ReadUntilDepthAsync(scene, fine.Depth));
                    for (int frame = 0; frame < 3; frame++)
                        RasterImage.AssertEqual(fine, await scene.ReadNextRasterImageAsync());
                    Assert.Equal(2, reads.Attempts);
                    Assert.Equal(1, reads.SuccessfulReads);
                    output.WriteLine($"One IOException, then detail recovered on source attempt {reads.Attempts}; " +
                        $"coarse color/depth preserved for {coarseFrames + 3} frames before release.");
                }
                finally
                {
                    continueRetry.TrySetResult();
                    await scene.DisposeAsync();
                }
                Assert.Equal(0, reads.ActiveReads);
            }
            Assert.Equal(0, reads.OpenSources);
            output.WriteLine("Scene and source disposal finished with zero active detail reads and zero open source handles.");
        }
        finally
        {
            continueRetry.TrySetResult();
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task RepeatedSourceFailuresKeepCoarseCoverageAndSceneDisposalCancelsTheNextRead()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"SomeEngine-SourceFailures-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var reads = new SourceReads();
        var fourthStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fourthCancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var abandonRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            using (MeshAsset asset = await GeometryStreamingHandoffTests.CreateStreamingPartsAsync(
                [(-1f, 1f, -.5f), (-1f, 1f, 0f)], new(0, 0, -.25f, 1.5f)))
            {
                AssetEntry entry = await asset.SaveAsync(Path.Combine(directory, "unreadable.mesh.asset"));
                await using var loader = new AssetLoader(new ObservedStorage(entry, reads));
                using MeshAsset file = await MeshAsset.OpenAsync(loader, new(entry.AssetGuid));
                ClusterMeshBlock geometry = file.GetBlock<ClusterMeshBlock>();
                var mesh = await geometry.CreateAsync(file.GetBlock<MeshBlock>(), file.Name);
                using var source = await geometry.OpenPayloadSourceAsync();
                var scene = await FrameStageScene.CreateAsync("D3D12", false,
                    new ClusterPipelineOptions
                        { MaxCandidates = 1024, ForceHardwareRaster = true, EnableFrameMetricsReadback = true },
                    await RasterTestGeometry.CreatePlaneAsync(),
                    residencyOptions: new ClusterResidencyOptions
                    {
                        PageHeapCapacityBytes = 1024, BvhCapacityBytes = 65536,
                        PageFaultCapacity = 16, MaxConcurrentPageLoads = 1,
                    });
                bool disposed = false;
                try
                {
                    scene.SetGeometryVisible(false);
                    RasterImage empty = await scene.ReadNextRasterImageAsync();
                    scene.AddStreamingMesh(mesh, source,
                        new TransformQvvs(new Vector3(0, 0, -4), Quaternion.Identity));
                    RasterImage coarse = await scene.ReadNextRasterImageAsync();
                    Assert.NotEqual(empty.Depth, coarse.Depth);
                    reads.ObserveDetail(mesh.GetPageByteLength(1), async (attempt, token) =>
                    {
                        if (attempt <= 3) throw new IOException("Injected persistent source read failure.");
                        fourthStarted.TrySetResult();
                        try
                        {
                            await abandonRead.Task.WaitAsync(token);
                            throw new IOException("Injected persistent source read failure.");
                        }
                        catch (OperationCanceledException) when (token.IsCancellationRequested)
                        {
                            fourthCancelled.TrySetResult();
                            throw;
                        }
                    });
                    scene.PumpStreaming();
                    int coarseFrames = 0;
                    for (; coarseFrames < 24 && !fourthStarted.Task.IsCompleted; coarseFrames++)
                        RasterImage.AssertEqual(coarse, await scene.ReadNextRasterImageAsync());
                    await fourthStarted.Task.WaitAsync(TimeSpan.FromSeconds(20));
                    for (int frame = 0; frame < 3; frame++)
                        RasterImage.AssertEqual(coarse, await scene.ReadNextRasterImageAsync());
                    Assert.Equal(4, reads.Attempts);
                    Assert.Equal(0, reads.SuccessfulReads);
                    await scene.DisposeAsync();
                    disposed = true;
                    await fourthCancelled.Task.WaitAsync(TimeSpan.FromSeconds(20));
                    Assert.Equal(0, reads.ActiveReads);
                    output.WriteLine($"Three IOException failures led to a fourth source attempt; " +
                        $"coarse color/depth preserved for {coarseFrames + 3} frames.");
                    output.WriteLine("Scene shutdown cancelled the fourth read and awaited its actual completion.");
                }
                finally
                {
                    abandonRead.TrySetResult();
                    if (!disposed) await scene.DisposeAsync();
                }
            }
            Assert.Equal(0, reads.OpenSources);
            output.WriteLine("Source disposal finished with zero active detail reads and zero open source handles.");
        }
        finally
        {
            abandonRead.TrySetResult();
            Directory.Delete(directory, recursive: true);
        }
    }

    private sealed class SourceReads
    {
        private int _pageBytes;
        private Func<int, CancellationToken, ValueTask>? _beforeRead;
        private int _attempts, _successfulReads, _activeReads, _openSources;
        internal int Attempts => Volatile.Read(ref _attempts);
        internal int SuccessfulReads => Volatile.Read(ref _successfulReads);
        internal int ActiveReads => Volatile.Read(ref _activeReads);
        internal int OpenSources => Volatile.Read(ref _openSources);
        internal void Opened() => Interlocked.Increment(ref _openSources);
        internal void Closed() => Interlocked.Decrement(ref _openSources);

        internal void ObserveDetail(int pageBytes, Func<int, CancellationToken, ValueTask> beforeRead)
        {
            _pageBytes = pageBytes;
            _beforeRead = beforeRead;
        }

        internal int BeginRead(int length)
        {
            if (_beforeRead is null || length != _pageBytes) return 0;
            Interlocked.Increment(ref _activeReads);
            return Interlocked.Increment(ref _attempts);
        }

        internal ValueTask BeforeReadAsync(int attempt, CancellationToken token) =>
            attempt == 0 ? ValueTask.CompletedTask : _beforeRead!(attempt, token);
        internal void Succeeded(int attempt)
        {
            if (attempt != 0) Interlocked.Increment(ref _successfulReads);
        }
        internal void Finished(int attempt)
        {
            if (attempt != 0) Interlocked.Decrement(ref _activeReads);
        }
    }

    private sealed class ObservedStorage(AssetEntry entry, SourceReads reads) : IAssetStorage
    {
        public bool TryFind(Guid id, out AssetEntry found) { found = entry; return id == entry.AssetGuid; }
        public ValueTask<IRangeSource> OpenAsync(AssetEntry selected, CancellationToken token = default)
        {
            IRangeSource source = FileRangeSource.Open(selected.Path);
            reads.Opened();
            return ValueTask.FromResult<IRangeSource>(new ObservedSource(source, reads));
        }
    }

    private sealed class ObservedSource(IRangeSource source, SourceReads reads) : IRangeSource
    {
        private int _disposed;
        public long Length => source.Length;
        public string Generation => source.Generation;
        public bool LeasesAreImmutable => source.LeasesAreImmutable;
        public bool RetainsResidentBacking => source.RetainsResidentBacking;
        public async ValueTask<RangeLease> AcquireAsync(long offset, int length, CancellationToken token = default)
        {
            int attempt = reads.BeginRead(length);
            try
            {
                await reads.BeforeReadAsync(attempt, token);
                RangeLease result = await source.AcquireAsync(offset, length, token);
                reads.Succeeded(attempt);
                return result;
            }
            finally { reads.Finished(attempt); }
        }

        public async ValueTask ReadExactlyAsync(long offset, Memory<byte> destination, CancellationToken token = default)
        {
            int attempt = reads.BeginRead(destination.Length);
            try
            {
                await reads.BeforeReadAsync(attempt, token);
                await source.ReadExactlyAsync(offset, destination, token);
                reads.Succeeded(attempt);
            }
            finally { reads.Finished(attempt); }
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            await source.DisposeAsync();
            reads.Closed();
        }
    }
}
