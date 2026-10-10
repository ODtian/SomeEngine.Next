using System.Collections.Concurrent;
using System.Numerics;
using SomeEngine.Assets;
using SomeEngine.Assets.Schema;
using SomeEngine.Core.Math;
using SomeEngine.Graphics;
using SomeEngine.Render.Cluster.Pipeline;
using SomeEngine.RenderGraph;
using SomeEngine.Serialization.IO;

namespace SomeEngine.Render.Cluster.Tests;

[Trait("Category", "Hardware")]
public sealed class GeometryStreamingOrderTests(Xunit.Abstractions.ITestOutputHelper output)
{
    [Fact]
    public async Task OlderFaultsLoadWithoutReplacingTheNewestViewsResidentDetail()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"SomeEngine-FeedbackOrder-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
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

            using MeshAsset visibleFile = await GeometryStreamingHandoffTests.CreateStreamingPartsAsync(
                [(-1f, 1f, -.5f), (-1f, 1f, 0f)], new(0, 0, -.25f, 1.5f));
            ClusterMeshBlock visibleGeometry = visibleFile.GetBlock<ClusterMeshBlock>();
            var visibleMesh = await visibleGeometry.CreateAsync(visibleFile.GetBlock<MeshBlock>(), visibleFile.Name);
            using var visibleSource = await visibleGeometry.OpenPayloadSourceAsync();
            using MeshAsset coldFile = visibleFile.CreateCopy(Guid.NewGuid());
            ClusterMeshBlock coldGeometry = coldFile.GetBlock<ClusterMeshBlock>();
            var coldMesh = await coldGeometry.CreateAsync(coldFile.GetBlock<MeshBlock>(), coldFile.Name);
            using var coldSource = await coldGeometry.OpenPayloadSourceAsync();
            using MeshAsset olderAsset = await GeometryStreamingHandoffTests.CreateStreamingPartsAsync(
                [(-1f, 1f, -.5f), (-1f, 0f, 0f), (0f, 1f, 0f)], new(0, 0, -.25f, 1.5f));
            AssetEntry entry = await olderAsset.SaveAsync(Path.Combine(directory, "older-view.mesh.asset"));
            var reads = new DetailReadResults();
            await using var loader = new AssetLoader(new ObservedStorage(entry, reads));
            using MeshAsset olderFile = await MeshAsset.OpenAsync(loader, new(entry.AssetGuid));
            ClusterMeshBlock olderGeometry = olderFile.GetBlock<ClusterMeshBlock>();
            var olderMesh = await olderGeometry.CreateAsync(olderFile.GetBlock<MeshBlock>(), olderFile.Name);
            using var olderSource = await olderGeometry.OpenPayloadSourceAsync();
            reads.PageBytes = olderMesh.GetPageByteLength(1);
            Assert.Equal(reads.PageBytes, olderMesh.GetPageByteLength(2));

            await using var scene = await FrameStageScene.CreateAsync("D3D12", false,
                options, await RasterTestGeometry.CreatePlaneAsync(), optionalFeatures: DeviceFeatures.ExternalTimelines,
                residencyOptions: new ClusterResidencyOptions
                {
                    // Three coarse pages leave room for exactly two detail pages.
                    PageHeapCapacityBytes = 2560, BvhCapacityBytes = 65536,
                    PageFaultCapacity = 16, MaxConcurrentPageLoads = 1,
                }, graphicsQueueCount: 2);
            scene.SetGeometryVisible(false);
            var visible = scene.AddStreamingMesh(visibleMesh, visibleSource, transform);
            RasterImage coarse = await scene.ReadNextRasterImageAsync();
            Assert.NotEqual(fine.Depth, coarse.Depth);
            RasterImage.AssertEqual(fine, await GeometryStreamingHandoffTests.ReadUntilDepthAsync(scene, fine.Depth));
            scene.RemoveMaterialMesh(visible);
            var cold = scene.AddStreamingMesh(coldMesh, coldSource, transform);
            RasterImage.AssertEqual(fine, await GeometryStreamingHandoffTests.ReadUntilDepthAsync(scene, fine.Depth));
            scene.RemoveMaterialMesh(cold);
            visible = scene.AddStreamingMesh(visibleMesh, visibleSource, transform);
            RasterImage.AssertEqual(fine, await scene.ReadNextRasterImageAsync());
            scene.PumpStreaming();

            var completed = new CompletedFrames();
            scene.ObserveCompletedFrames(completed);
            IGraphicsBackend backend = scene.Backend;
            var renderQueue = backend.GetQueue(scene.Device, QueueType.Graphics);
            // This queue never participates in the graph, so releasing a gate cannot
            // wait behind the graph's cross-queue dependencies.
            var signalQueue = backend.GetQueue(scene.Device, QueueType.Graphics, 1);
            using var firstGate = backend.CreateExternalTimeline(scene.Device, 0);
            using var laterGate = backend.CreateExternalTimeline(scene.Device, 0);
            QueueCompletion firstWait = default, laterWait = default;
            ulong faultsBefore = scene.FeedbackStatistics.ConsumedPageFaults;
            try
            {
                firstWait = backend.Submit(renderQueue, new([], [new(firstGate, 1)], [], [], []));
                FrameStageSubmission first = await scene.SubmitNextFrameAsync();
                laterWait = backend.Submit(renderQueue, new([], [new(laterGate, 1)], [], [], []));
                scene.RemoveMaterialMesh(visible);
                var older = scene.AddStreamingMesh(olderMesh, olderSource, transform);
                FrameStageSubmission second = await scene.SubmitNextFrameAsync();
                FrameStageSubmission third = await scene.SubmitNextFrameAsync();
                _ = backend.Submit(signalQueue, new([], [], [], [], [new(firstGate, 1)]));
                scene.WaitForFrame(first);
                scene.RemoveMaterialMesh(older);
                scene.AddStreamingMesh(visibleMesh, visibleSource, transform);
                // Opening this frame collects only the first and reuses its storage.
                FrameStageSubmission newest = await scene.SubmitNextFrameAsync();
                Assert.Equal(new[] { first.Identity }, completed.Identities);
                _ = backend.Submit(signalQueue, new([], [], [], [], [new(laterGate, 1)]));
                scene.WaitForFrame(newest);
                scene.CollectCompletedFrames();
                Assert.Equal(new[] { first.Identity, newest.Identity, second.Identity, third.Identity },
                    completed.Identities);
                output.WriteLine($"Completed frame identities: {string.Join(", ", completed.Identities)}");

                // Newer ordinary feedback must not hide a regression by repairing
                // protection before the older fault reads have displaced cold pages.
                scene.PauseStreamingFeedback();
                reads.Observe = true;
                scene.PumpStreaming();
                Assert.True(scene.FeedbackStatistics.ConsumedPageFaults > faultsBefore);
                ulong receivedFrames = scene.FeedbackStatistics.ReceivedFrames;
                for (int frame = 0; frame < 24; frame++)
                    RasterImage.AssertEqual(fine, await scene.ReadNextRasterImageAsync());
                await reads.TwoPages.Task.WaitAsync(TimeSpan.FromSeconds(20));
                for (int frame = 0; frame < 3; frame++)
                    RasterImage.AssertEqual(fine, await scene.ReadNextRasterImageAsync());
                Assert.Equal(receivedFrames, scene.FeedbackStatistics.ReceivedFrames);
                Assert.Equal(2, reads.Offsets.Length);
                output.WriteLine($"Older detail source offsets read: {string.Join(", ", reads.Offsets)}");
                output.WriteLine("Newest-view color and depth survived both older detail reads and 27 publication frames.");
            }
            finally
            {
                // Both fences and the independent signal queue exist before any wait
                // is submitted. A failure releasing one must still release the other.
                try { Release(firstGate); }
                finally
                {
                    try { Release(laterGate); }
                    finally
                    {
                        foreach (QueueCompletion wait in new[] { firstWait, laterWait })
                            if (wait != default)
                                Assert.Equal(WaitStatus.Completed, backend.WaitCpu(wait, TimeSpan.FromSeconds(20)));
                        scene.WaitForSubmittedFrames();
                    }
                }
            }

            void Release(ExternalTimeline gate)
            {
                QueueCompletion signal = backend.Submit(signalQueue, new([], [], [], [], [new(gate, 2)]));
                Assert.Equal(WaitStatus.Completed, backend.WaitCpu(signal, TimeSpan.FromSeconds(20)));
            }
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private sealed class CompletedFrames : IGraphReadbackReceiver
    {
        internal List<ulong> Identities { get; } = [];
        public void OnReadback(ReadOnlySpan<byte> bytes, ulong tag) => Identities.Add(tag);
    }

    private sealed class DetailReadResults
    {
        internal bool Observe;
        internal int PageBytes;
        private readonly ConcurrentDictionary<long, byte> _offsets = new();
        internal long[] Offsets => _offsets.Keys.Order().ToArray();
        internal TaskCompletionSource TwoPages { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal void Finished(long offset, int length)
        {
            if (!Observe || length != PageBytes) return;
            _offsets.TryAdd(offset, 0);
            if (_offsets.Count == 2) TwoPages.TrySetResult();
        }
    }

    private sealed class ObservedStorage(AssetEntry entry, DetailReadResults reads) : IAssetStorage
    {
        public bool TryFind(Guid id, out AssetEntry found) { found = entry; return id == entry.AssetGuid; }
        public ValueTask<IRangeSource> OpenAsync(AssetEntry selected, CancellationToken token = default) =>
            ValueTask.FromResult<IRangeSource>(new ObservedSource(FileRangeSource.Open(selected.Path), reads));
    }

    private sealed class ObservedSource(IRangeSource source, DetailReadResults reads) : IRangeSource
    {
        public long Length => source.Length;
        public string Generation => source.Generation;
        public bool LeasesAreImmutable => source.LeasesAreImmutable;
        public bool RetainsResidentBacking => source.RetainsResidentBacking;
        public async ValueTask<RangeLease> AcquireAsync(long offset, int length, CancellationToken token = default)
        {
            RangeLease result = await source.AcquireAsync(offset, length, token);
            reads.Finished(offset, length);
            return result;
        }
        public async ValueTask ReadExactlyAsync(long offset, Memory<byte> destination, CancellationToken token = default)
        {
            await source.ReadExactlyAsync(offset, destination, token);
            reads.Finished(offset, destination.Length);
        }
        public ValueTask DisposeAsync() => source.DisposeAsync();
    }
}
