using SomeEngine.Graphics;
using SomeEngine.Resources;
using SomeEngine.RenderGraph;
using EngineTexture = SomeEngine.Resources.Texture;
using GraphicsTextureDimension = SomeEngine.Graphics.TextureDimension;
using GraphicsTextureViewDimension = SomeEngine.Graphics.TextureViewDimension;
using RhiTexture = SomeEngine.Graphics.Texture;

namespace SomeEngine.Render.Tests;

public sealed class RenderDeviceRetirementTests
{
    [Fact]
    public void LaterSubmissionDoesNotExtendEarlierRetirements()
    {
        using var test = new RenderTestDevice(DeviceFeatures.ExternalTimelines,
            [new DeviceQueueDesc(QueueType.Graphics), new DeviceQueueDesc(QueueType.Compute)]);
        var graphics = test.Backend.GetQueue(test.Device, QueueType.Graphics);
        var signalQueue = test.Backend.GetQueue(test.Device, QueueType.Compute);
        using var gate = test.Backend.CreateExternalTimeline(test.Device, 0);
        var released = new List<string>();
        QueueCompletion first = test.Backend.Submit(graphics, new([], [new(gate, 1)], [], [], []));
        QueueCompletion latest = first;
        try
        {
            test.RenderDevice.TrackSubmission([first]);
            test.RenderDevice.RetireAfterTrackedSubmissions(new DisposalCallback(() => released.Add("first")));
            test.RenderDevice.RetireAfterTrackedSubmissions(new DisposalCallback(() => released.Add("second")));

            QueueCompletion later = test.Backend.Submit(graphics, new([], [new(gate, 2)], [], [], []));
            latest = later;
            test.RenderDevice.TrackSubmission([later]);
            test.RenderDevice.RetireAfterTrackedSubmissions(new DisposalCallback(() => released.Add("later")));
            Assert.Empty(released);

            _ = test.Backend.Submit(signalQueue, new([], [], [], [], [new(gate, 1)]));
            Assert.Equal(WaitStatus.Completed, test.Backend.WaitCpu(first, TimeSpan.FromSeconds(10)));
            Assert.False(test.Backend.IsComplete(later));
            test.RenderDevice.CollectCompleted();
            Assert.Equal(new[] { "second", "first" }, released);

            _ = test.Backend.Submit(signalQueue, new([], [], [], [], [new(gate, 2)]));
            Assert.Equal(WaitStatus.Completed, test.Backend.WaitCpu(later, TimeSpan.FromSeconds(10)));
            test.RenderDevice.CollectCompleted();
            Assert.Equal(new[] { "second", "first", "later" }, released);
        }
        finally
        {
            ReleaseGateAndDrain(test, signalQueue, gate, 3, latest);
        }
    }

    [Fact]
    public void RetirementFailurePreservesSuccessfulDisposalPrefix()
    {
        using var test = new RenderTestDevice(DeviceFeatures.ExternalTimelines,
            [new DeviceQueueDesc(QueueType.Graphics), new DeviceQueueDesc(QueueType.Compute)]);
        var graphics = test.Backend.GetQueue(test.Device, QueueType.Graphics);
        var signalQueue = test.Backend.GetQueue(test.Device, QueueType.Compute);
        using var gate = test.Backend.CreateExternalTimeline(test.Device, 0);
        var released = new List<string>();
        Exception? disposalFailure = new InvalidOperationException();
        QueueCompletion completion = test.Backend.Submit(graphics, new([], [new(gate, 1)], [], [], []));
        try
        {
            test.RenderDevice.TrackSubmission([completion]);
            test.RenderDevice.RetireAfterTrackedSubmissions(new DisposalCallback(() => released.Add("first")));
            test.RenderDevice.RetireAfterTrackedSubmissions(new DisposalCallback(() =>
            {
                released.Add("middle");
                if (disposalFailure is { } failure)
                {
                    disposalFailure = null;
                    throw failure;
                }
            }));
            test.RenderDevice.RetireAfterTrackedSubmissions(new DisposalCallback(() => released.Add("last")));
            Assert.Empty(released);

            _ = test.Backend.Submit(signalQueue, new([], [], [], [], [new(gate, 1)]));
            Assert.Equal(WaitStatus.Completed, test.Backend.WaitCpu(completion, TimeSpan.FromSeconds(10)));
            Assert.Throws<InvalidOperationException>(() => test.RenderDevice.CollectCompleted());
            Assert.Equal(new[] { "last", "middle" }, released);

            test.RenderDevice.CollectCompleted();
            Assert.Equal(new[] { "last", "middle", "middle", "first" }, released);
            test.RenderDevice.CollectCompleted();
            Assert.Equal(new[] { "last", "middle", "middle", "first" }, released);
        }
        finally
        {
            disposalFailure = null;
            ReleaseGateAndDrain(test, signalQueue, gate, 2, completion);
        }
    }

    [Fact]
    public void EqualBoundariesDoNotMergeResourceLifetimes()
    {
        using var test = new RenderTestDevice(DeviceFeatures.ExternalTimelines,
            [new DeviceQueueDesc(QueueType.Graphics), new DeviceQueueDesc(QueueType.Compute)]);
        var graphics = test.Backend.GetQueue(test.Device, QueueType.Graphics);
        var signalQueue = test.Backend.GetQueue(test.Device, QueueType.Compute);
        using var gate = test.Backend.CreateExternalTimeline(test.Device, 0);
        var released = new List<int>();
        QueueCompletion completion = test.Backend.Submit(graphics, new([], [new(gate, 1)], [], [], []));
        try
        {
            test.RenderDevice.TrackSubmission([completion]);
            for (int index = 0; index < 8; index++)
            {
                int resource = index;
                test.RenderDevice.RetireAfterTrackedSubmissions(new DisposalCallback(() => released.Add(resource)));
            }
            test.RenderDevice.CollectCompleted();
            Assert.False(test.Backend.IsComplete(completion));
            Assert.Empty(released);

            _ = test.Backend.Submit(signalQueue, new([], [], [], [], [new(gate, 1)]));
            Assert.Equal(WaitStatus.Completed, test.Backend.WaitCpu(completion, TimeSpan.FromSeconds(10)));
            test.RenderDevice.CollectCompleted();
            Assert.Equal(Enumerable.Range(0, 8), released.OrderBy(resource => resource));
            test.RenderDevice.CollectCompleted();
            Assert.Equal(Enumerable.Range(0, 8), released.OrderBy(resource => resource));
        }
        finally
        {
            ReleaseGateAndDrain(test, signalQueue, gate, 2, completion);
        }
    }

    [Fact]
    public void TextureReleaseSurvivesAnEarlierDisposalFailure()
    {
        using var test = new RenderTestDevice(DeviceFeatures.ExternalTimelines,
            [new DeviceQueueDesc(QueueType.Graphics), new DeviceQueueDesc(QueueType.Compute)]);
        var graphics = test.Backend.GetQueue(test.Device, QueueType.Graphics);
        var signalQueue = test.Backend.GetQueue(test.Device, QueueType.Compute);
        using var gate = test.Backend.CreateExternalTimeline(test.Device, 0);
        var description = new TextureDesc(GraphicsTextureDimension.Texture2D, 1, 1, 1, 1, 1, 1,
            Format.R8UNorm, TextureUsages.Sampled | TextureUsages.CopyDestination, label: "release failure");
        EngineTexture texture = EngineTexture.Create(description,
            [new TexturePixels(new TextureMipTile
            {
                Width = 1, Height = 1, RowPitch = 1, SlicePitch = 1,
                ChunkKey = TextureTiles.ChunkKey(0, 0, 0, 0, 0, 0), DecodedLength = 1,
            }, new byte[] { 1 })], GraphicsTextureViewDimension.Texture2D);
        var resource = test.RenderDevice.GetTexture(texture);
        var released = new List<string>();
        Exception? disposalFailure = new InvalidOperationException();
        QueueCompletion completion = test.Backend.Submit(graphics, new([], [new(gate, 1)], [], [], []));
        try
        {
            test.RenderDevice.TrackSubmission([completion]);
            test.RenderDevice.RetireAfterTrackedSubmissions(new DisposalCallback(() =>
            {
                released.Add("earlier");
                if (disposalFailure is { } failure)
                {
                    disposalFailure = null;
                    throw failure;
                }
            }));
            test.RenderDevice.RetireAfterTrackedSubmissions(new DisposalCallback(() => released.Add("successful")));
            Assert.Empty(released);

            _ = test.Backend.Submit(signalQueue, new([], [], [], [], [new(gate, 1)]));
            Assert.Equal(WaitStatus.Completed, test.Backend.WaitCpu(completion, TimeSpan.FromSeconds(10)));
            test.RenderDevice.ReleaseTexture(texture);
            Assert.Throws<InvalidOperationException>(() => test.RenderDevice.CollectCompleted());
            Assert.False(texture.TryGetTexture(test.Device, out _));
            Assert.Equal(new[] { "successful", "earlier" }, released);

            test.RenderDevice.CollectCompleted();
            Assert.Throws<ObjectDisposedException>(() =>
            {
                using var view = test.Backend.CreateTextureSrv(test.Device,
                    new TextureSrvDesc(resource, new(0, 1, 0, 1, TextureAspects.Color),
                        Format.R8UNorm, GraphicsTextureViewDimension.Texture2D));
            });
            Assert.Equal(new[] { "successful", "earlier", "earlier" }, released);

            test.RenderDevice.ReleaseTexture(texture);
            test.RenderDevice.CollectCompleted();
            Assert.Equal(new[] { "successful", "earlier", "earlier" }, released);
        }
        finally
        {
            disposalFailure = null;
            try { ReleaseGateAndDrain(test, signalQueue, gate, 2, completion); }
            finally { resource.Dispose(); }
        }
    }

    [Fact]
    public async Task TextureReplacementReleaseSurvivesAnEarlierDisposalFailure()
    {
        RenderTestDevice? first = null;
        RenderTestDevice? second = null;
        RenderGraph.RenderGraph? graph = null;
        EngineTexture? texture = null;
        ExternalTimeline? gate = null;
        CancellationTokenSource? cancelUpdate = null;
        Queue? signalQueue = null;
        QueueCompletion? completion = null;
        RhiTexture? previous = null;
        RhiTexture? current = null;
        RhiTexture? other = null;
        Task? update = null;
        var released = new List<string>();
        var expectedFailure = new InvalidOperationException();
        Exception? disposalFailure = expectedFailure;
        Exception? testFailure = null;
        var cleanupErrors = new List<Exception>();
        try
        {
            first = new RenderTestDevice(DeviceFeatures.ExternalTimelines,
                [new DeviceQueueDesc(QueueType.Graphics), new DeviceQueueDesc(QueueType.Compute)]);
            second = new RenderTestDevice();
            Assert.NotSame(first.Device, second.Device);
            var graphics = first.Backend.GetQueue(first.Device, QueueType.Graphics);
            signalQueue = first.Backend.GetQueue(first.Device, QueueType.Compute);
            gate = first.Backend.CreateExternalTimeline(first.Device, 0);
            graph = new RenderGraph.RenderGraph(first.Backend, first.Device, [graphics]);
            cancelUpdate = new CancellationTokenSource();
            var description = new TextureDesc(GraphicsTextureDimension.Texture2D, 1, 1, 1, 1, 1, 1,
                Format.R8UNorm, TextureUsages.Sampled | TextureUsages.CopyDestination,
                label: "replacement release failure");
            var tile = new TextureMipTile
            {
                Width = 1, Height = 1, RowPitch = 1, SlicePitch = 1,
                ChunkKey = TextureTiles.ChunkKey(0, 0, 0, 0, 0, 0), DecodedLength = 1,
            };
            texture = EngineTexture.Create(description,
                [new TexturePixels(tile, new byte[] { 1 })], GraphicsTextureViewDimension.Texture2D);
            previous = first.RenderDevice.GetTexture(texture);
            other = second.RenderDevice.GetTexture(texture);
            completion = first.Backend.Submit(graphics, new([], [new(gate, 1)], [], [], []));
            first.RenderDevice.TrackSubmission([completion.Value]);
            first.RenderDevice.RetireAfterTrackedSubmissions(new DisposalCallback(() =>
            {
                released.Add("earlier");
                if (disposalFailure is { } failure)
                {
                    disposalFailure = null;
                    throw failure;
                }
            }));
            first.RenderDevice.RetireAfterTrackedSubmissions(new DisposalCallback(() => released.Add("successful")));

            // Both devices keep the CPU update open while only the first authors its replacement.
            update = texture.UpdateAsync(description,
                [new TexturePixels(tile, new byte[] { 2 })], GraphicsTextureViewDimension.Texture2D,
                cancellationToken: cancelUpdate.Token).AsTask();
            Assert.True(texture.IsUpdating);
            Assert.False(update.IsCompleted);
            RenderGraphFrame frame = graph.BeginFrame();
            try
            {
                GraphPassId consumer = frame.AddGeneralPass("replacement texture",
                    PassQueueSelection.AnyOfType(QueueType.Graphics), 0,
                    new PassOptions(Culling: PassCullingMode.NeverCull),
                    static (ref PassDefinition _, ref int _) => { },
                    static (ref GeneralPassCommandScope _, in int _) => { });
                current = first.RenderDevice.PrepareTexture(ref frame, consumer, texture);
                Assert.NotSame(previous, current);
                Assert.True(texture.TryGetTexture(first.Device, out RhiTexture? firstTexture));
                Assert.Same(current, firstTexture);
                Assert.True(texture.TryGetTexture(second.Device, out RhiTexture? secondTexture));
                Assert.Same(other, secondTexture);
                Assert.False(update.IsCompleted);
                Assert.False(first.Backend.IsComplete(completion.Value));
                Assert.Empty(released);

                // Only the backend readies the older retirement; the frame remains unsubmitted.
                _ = first.Backend.Submit(signalQueue, new([], [], [], [], [new(gate, 1)]));
                Assert.Equal(WaitStatus.Completed, first.Backend.WaitCpu(completion.Value, TimeSpan.FromSeconds(10)));
                first.RenderDevice.ReleaseTexture(texture);
                Assert.Same(expectedFailure,
                    Assert.Throws<InvalidOperationException>(() => first.RenderDevice.CollectCompleted()));
                Assert.False(texture.TryGetTexture(first.Device, out _));
                Assert.True(texture.TryGetTexture(second.Device, out secondTexture));
                Assert.Same(other, secondTexture);
                Assert.True(texture.IsUpdating);
                Assert.False(update.IsCompleted);
                Assert.Equal(new[] { "successful", "earlier" }, released);
                // A failed drain must retain the release request for a later collection.
                first.RenderDevice.CollectCompleted();
                Assert.Equal(new[] { "successful", "earlier", "earlier" }, released);
                Assert.Throws<ObjectDisposedException>(() =>
                {
                    using var view = first.Backend.CreateTextureSrv(first.Device,
                        new TextureSrvDesc(previous, new(0, 1, 0, 1, TextureAspects.Color),
                            Format.R8UNorm, GraphicsTextureViewDimension.Texture2D));
                });
                using var liveView = first.Backend.CreateTextureSrv(first.Device,
                    new TextureSrvDesc(current, new(0, 1, 0, 1, TextureAspects.Color),
                        Format.R8UNorm, GraphicsTextureViewDimension.Texture2D));
            }
            finally
            {
                try { frame.Dispose(); }
                catch (Exception error) { cleanupErrors.Add(error); }
            }

            graph.Dispose();
            graph = null;
            first.RenderDevice.CollectCompleted();
            Assert.Throws<ObjectDisposedException>(() =>
            {
                using var view = first.Backend.CreateTextureSrv(first.Device,
                    new TextureSrvDesc(current, new(0, 1, 0, 1, TextureAspects.Color),
                        Format.R8UNorm, GraphicsTextureViewDimension.Texture2D));
            });
            Assert.Equal(new[] { "successful", "earlier", "earlier" }, released);
            first.RenderDevice.ReleaseTexture(texture);
            first.RenderDevice.CollectCompleted();
            Assert.Equal(new[] { "successful", "earlier", "earlier" }, released);
            Assert.False(texture.TryGetTexture(first.Device, out _));
            Assert.True(texture.IsUpdating);
            Assert.False(update.IsCompleted);
            using (var otherView = second.Backend.CreateTextureSrv(second.Device,
                new TextureSrvDesc(other, new(0, 1, 0, 1, TextureAspects.Color),
                    Format.R8UNorm, GraphicsTextureViewDimension.Texture2D)))
            {
                Assert.True(texture.TryGetTexture(second.Device, out RhiTexture? secondTexture));
                Assert.Same(other, secondTexture);
            }
            cancelUpdate.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => update.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.False(texture.IsUpdating);
        }
        catch (Exception error)
        {
            testFailure = error;
            throw;
        }
        finally
        {
            void Cleanup(Action action)
            {
                try { action(); }
                catch (Exception error) { cleanupErrors.Add(error); }
            }

            disposalFailure = null;
            if (first is not null && signalQueue is not null && gate is not null && completion is { } submitted)
                Cleanup(() => ReleaseGateAndDrain(first, signalQueue, gate, 2, submitted));
            if (cancelUpdate is not null) Cleanup(cancelUpdate.Cancel);
            if (graph is not null) Cleanup(graph.Dispose);
            if (update is not null)
            {
                try { await update.WaitAsync(TimeSpan.FromSeconds(10)); }
                catch (OperationCanceledException) when (cancelUpdate?.IsCancellationRequested == true) { }
                catch (Exception error)
                {
                    if (!ReferenceEquals(testFailure, error)) cleanupErrors.Add(error);
                }
            }
            if (texture is not null)
            {
                if (first is not null) Cleanup(() => first.RenderDevice.ReleaseTexture(texture));
                if (second is not null) Cleanup(() => second.RenderDevice.ReleaseTexture(texture));
            }
            if (first is not null) Cleanup(first.RenderDevice.CollectCompleted);
            if (second is not null) Cleanup(second.RenderDevice.CollectCompleted);
            if (current is not null) Cleanup(current.Dispose);
            if (previous is not null) Cleanup(previous.Dispose);
            if (other is not null) Cleanup(other.Dispose);
            if (gate is not null) Cleanup(gate.Dispose);
            if (cancelUpdate is not null) Cleanup(cancelUpdate.Dispose);
            if (second is not null)
            {
                Cleanup(second.RenderDevice.Dispose);
                Cleanup(second.Device.Dispose);
                Cleanup(second.Backend.Dispose);
            }
            if (first is not null)
            {
                Cleanup(first.RenderDevice.Dispose);
                Cleanup(first.Device.Dispose);
                Cleanup(first.Backend.Dispose);
            }
            if (cleanupErrors.Count != 0)
            {
                if (testFailure is not null) cleanupErrors.Insert(0, testFailure);
                throw new AggregateException(cleanupErrors);
            }
        }
    }

    private static void ReleaseGateAndDrain(RenderTestDevice test, Queue signalQueue,
        ExternalTimeline gate, ulong value, QueueCompletion completion)
    {
        QueueCompletion signal = test.Backend.Submit(signalQueue, new([], [], [], [], [new(gate, value)]));
        try
        {
            Assert.Equal(WaitStatus.Completed, test.Backend.WaitCpu(completion, TimeSpan.FromSeconds(10)));
        }
        finally
        {
            Assert.Equal(WaitStatus.Completed, test.Backend.WaitCpu(signal, TimeSpan.FromSeconds(10)));
        }
    }

    private sealed class DisposalCallback(Action dispose) : IDisposable
    {
        public void Dispose() => dispose();
    }
}
