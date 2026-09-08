using SomeEngine.Assets;
using SomeEngine.Assets.Pipeline;
using SomeEngine.Assets.Schema;
using SomeEngine.Graphics;
using SomeEngine.Graphics.Direct3D12;
using SomeEngine.RenderGraph;
using SomeEngine.Serialization.IO;
using SomeEngine.Serialization.Streaming;
using EngineTexture = SomeEngine.Assets.Schema.Texture;
using GraphicsTextureDimension = SomeEngine.Graphics.TextureDimension;
using GraphicsTextureViewDimension = SomeEngine.Graphics.TextureViewDimension;
using RhiTexture = SomeEngine.Graphics.Texture;

namespace SomeEngine.Render.Tests;

public sealed class RenderDeviceTextureTests
{
    [Fact]
    public async Task CanceledFirstPreparationDoesNotCommitUploadState()
    {
        if (!OperatingSystem.IsWindows())
            return;

        using var assets = new TemporaryAssetProject();
        AssetGuid guid = assets.Project.CreateAsset(
            "assets/canceled-first-use.texture.asset",
            SingleMipTexture("canceled-first-use", 2, 1, [8, 9]));
        await using var loader = new AssetLoader(assets.Project.CreateStorage());
        EngineTexture asset = await loader.LoadAsync(new AssetId<EngineTexture>(guid));
        using IGraphicsBackend backend = D3D12GraphicsBackend.Create();
        using Device device = CreateWarpDevice(backend, "RenderDevice cancel test");
        using var renderDevice = new RenderDevice(backend, device);
        using var graph = CreateGraph(backend, device);

        RhiTexture canceledBacking = CancelTextureFrame(graph, renderDevice, asset);

        Assert.Same(canceledBacking, renderDevice.GetTexture(asset));
        Assert.Throws<InvalidOperationException>(() => renderDevice.GetTextureSrv(asset));

        RhiTexture executedBacking = ExecuteTextureFrame(graph, renderDevice, asset);
        Assert.Same(canceledBacking, executedBacking);
        _ = renderDevice.GetTextureSrv(asset);
    }

    [Theory]
    [InlineData(FrameSubmissionMode.RecordAllThenSubmit)]
    [InlineData(FrameSubmissionMode.Pipelined)]
    public async Task FailedConsumerDoesNotCommitTextureUpload(FrameSubmissionMode mode)
    {
        if (!OperatingSystem.IsWindows())
            return;
        using var assets = new TemporaryAssetProject();
        AssetGuid guid = assets.Project.CreateAsset(
            "assets/failed-consumer.texture.asset",
            SingleMipTexture("failed-consumer", 2, 1, [8, 9]));
        await using var loader = new AssetLoader(assets.Project.CreateStorage());
        EngineTexture asset = await loader.LoadAsync(new AssetId<EngineTexture>(guid));
        using IGraphicsBackend backend = D3D12GraphicsBackend.Create();
        using Device device = CreateWarpDevice(backend, "Texture recording failure test");
        using var renderDevice = new RenderDevice(backend, device);
        using var graph = CreateGraph(backend, device);
        RenderGraphFrame frame = graph.BeginFrame(new RenderGraphFrameOptions(SubmissionMode: mode));
        bool failed = false;
        try
        {
            GraphPassId consumer = frame.AddGeneralPass(
                "Fail after Texture upload",
                PassQueueSelection.AnyOfType(QueueType.Graphics), 0,
                new PassOptions(Culling: PassCullingMode.NeverCull,
                    Recording: PassRecordingMode.CallingThread),
                static (ref PassDefinition _, ref int _) => { },
                static (ref GeneralPassCommandScope _, in int _) =>
                    throw new InvalidOperationException("Texture consumer recording failed"));
            _ = renderDevice.PrepareTexture(ref frame, consumer, asset);
            try
            {
                frame.Execute(new QueueCompletion[graph.MaximumQueueCompletionCount]);
            }
            catch (InvalidOperationException exception) when (
                exception.Message == "Texture consumer recording failed")
            {
                failed = true;
            }
        }
        finally
        {
            frame.Dispose();
        }
        Assert.True(failed);
        Assert.Throws<InvalidOperationException>(() => renderDevice.GetTextureSrv(asset));
        _ = ExecuteTextureFrame(graph, renderDevice, asset);
        _ = renderDevice.GetTextureSrv(asset);
    }

    [Fact]
    public async Task BlockCompressedBootstrapUsesItsNativeCopyFootprint()
    {
        if (!OperatingSystem.IsWindows())
            return;

        using var assets = new TemporaryAssetProject();
        AssetGuid guid = assets.Project.CreateAsset(
            "assets/compressed-bootstrap.texture.asset",
            new EngineTexture
            {
                Name = "compressed-bootstrap",
                Width = 4,
                Height = 4,
                Dimension = GraphicsTextureDimension.Texture2D,
                Depth = 1,
                MipLevelCount = 1,
                ArrayLayerCount = 1,
                Format = Format.BC7UNorm,
                SampledFormat = Format.BC7UNorm,
                SampledDimension = GraphicsTextureViewDimension.Texture2D,
                MipTiles =
                [
                    new TextureMipTile
                    {
                        Width = 4,
                        Height = 4,
                        RowPitch = 16,
                        SlicePitch = 16,
                        Payload = new byte[16],
                    },
                ],
            });
        await using var loader = new AssetLoader(assets.Project.CreateStorage());
        EngineTexture asset = await loader.LoadAsync(new AssetId<EngineTexture>(guid));
        ChunkStreamingSnapshot before = asset.StreamingMetrics!.Snapshot();
        using IGraphicsBackend backend = D3D12GraphicsBackend.Create();
        using Device device = CreateWarpDevice(backend, "RenderDevice BC test");
        using var renderDevice = new RenderDevice(backend, device);
        using var graph = CreateGraph(backend, device);

        _ = ExecuteTextureFrame(graph, renderDevice, asset);

        ChunkStreamingSnapshot after = asset.StreamingMetrics.Snapshot();
        Assert.Equal(before.LoadsCompleted, after.LoadsCompleted);
        Assert.Equal(before.StoredBytesRead, after.StoredBytesRead);
    }

    [Fact]
    public async Task FirstPreparationUsesResidentBootstrapWithoutIssuingAssetIo()
    {
        if (!OperatingSystem.IsWindows())
            return;

        using var assets = new TemporaryAssetProject();
        AssetGuid guid = assets.Project.CreateAsset(
            "assets/first-use.texture.asset",
            TwoLayerStreamedTexture("first-use"));
        await using var loader = new AssetLoader(assets.Project.CreateStorage());
        EngineTexture asset = await loader.LoadAsync(new AssetId<EngineTexture>(guid));
        ChunkStreamingSnapshot before = asset.StreamingMetrics!.Snapshot();

        using IGraphicsBackend backend = D3D12GraphicsBackend.Create();
        using Device device = CreateWarpDevice(backend, "RenderDevice first-use test");
        using var renderDevice = new RenderDevice(backend, device);
        using var graph = CreateGraph(backend, device);

        RhiTexture resource = ExecuteTextureFrame(graph, renderDevice, asset);
        ChunkStreamingSnapshot after = asset.StreamingMetrics.Snapshot();

        Assert.Same(resource, renderDevice.GetTexture(asset));
        Assert.Equal(before.LoadsCompleted, after.LoadsCompleted);
        Assert.Equal(before.StoredBytesRead, after.StoredBytesRead);
        Assert.True(after.CacheHits > before.CacheHits);
        Assert.True(after.CacheMisses > before.CacheMisses);

        using (var secondGraph = CreateGraph(backend, device))
        {
            RhiTexture secondGraphResource = ExecuteTextureFrame(
                secondGraph,
                renderDevice,
                asset);
            Assert.Same(resource, secondGraphResource);
        }

        TextureSrv defaultView = renderDevice.GetTextureSrv(asset);
        Assert.Equal(1u, defaultView.Description.Range.FirstMipLevel);
        Assert.Equal(1u, defaultView.Description.Range.MipLevelCount);
        Assert.Equal(2u, defaultView.Description.Range.ArrayLayerCount);

        TextureSubresourceRange firstLayer = new(1, 1, 0, 1, TextureAspects.Color);
        TextureSrv first = renderDevice.GetTextureSrv(
            asset,
            firstLayer,
            Format.R8UNorm,
            GraphicsTextureViewDimension.Texture2DArray,
            "first label");
        TextureSrv sameDescription = renderDevice.GetTextureSrv(
            asset,
            firstLayer,
            Format.R8UNorm,
            GraphicsTextureViewDimension.Texture2DArray,
            "ignored cache label");
        TextureSrv secondLayer = renderDevice.GetTextureSrv(
            asset,
            new TextureSubresourceRange(1, 1, 1, 1, TextureAspects.Color),
            Format.R8UNorm,
            GraphicsTextureViewDimension.Texture2DArray,
            "second layer");

        Assert.Same(first, sameDescription);
        Assert.NotSame(first, secondLayer);

        TextureUav firstUav = renderDevice.GetTextureUav(
            asset,
            firstLayer,
            Format.R8UNorm,
            GraphicsTextureViewDimension.Texture2DArray,
            "first UAV");
        TextureUav sameUav = renderDevice.GetTextureUav(
            asset,
            firstLayer,
            Format.R8UNorm,
            GraphicsTextureViewDimension.Texture2DArray,
            "ignored UAV label");
        TextureUav secondUav = renderDevice.GetTextureUav(
            asset,
            new TextureSubresourceRange(1, 1, 1, 1, TextureAspects.Color),
            Format.R8UNorm,
            GraphicsTextureViewDimension.Texture2DArray,
            "second UAV");
        Assert.Same(firstUav, sameUav);
        Assert.NotSame(firstUav, secondUav);

        using (ResidentChunkLease mipZeroLayerZero = await asset.AcquireMipTileAsync(
                   0, 0, 0, 0, 0, 0))
        using (ResidentChunkLease mipZeroLayerOne = await asset.AcquireMipTileAsync(
                   0, 1, 0, 0, 0, 0))
        {
            Assert.Equal(4, mipZeroLayerZero.Memory.Length);
            Assert.Equal(4, mipZeroLayerOne.Memory.Length);
        }

        _ = ExecuteTextureFrame(graph, renderDevice, asset);
        TextureSrv refinedView = renderDevice.GetTextureSrv(asset);
        Assert.Equal(0u, refinedView.Description.Range.FirstMipLevel);
        Assert.Equal(2u, refinedView.Description.Range.MipLevelCount);
    }

    [Fact]
    public async Task TextureAndMissingRealizationsAreIndependentAcrossDevices()
    {
        if (!OperatingSystem.IsWindows())
            return;

        using var assets = new TemporaryAssetProject();
        AssetGuid guid = assets.Project.CreateAsset(
            "assets/multi-device.texture.asset",
            SingleMipTexture("multi-device", 2, 1, [3, 7]));
        await using var loader = new AssetLoader(assets.Project.CreateStorage());
        EngineTexture asset = await loader.LoadAsync(new AssetId<EngineTexture>(guid));

        using IGraphicsBackend backend = D3D12GraphicsBackend.Create();
        using Device firstDevice = CreateWarpDevice(backend, "RenderDevice A");
        using Device secondDevice = CreateWarpDevice(backend, "RenderDevice B");
        using var first = new RenderDevice(backend, firstDevice);
        using var second = new RenderDevice(backend, secondDevice);

        RhiTexture firstTexture = first.GetTexture(asset);
        RhiTexture secondTexture = second.GetTexture(asset);

        Assert.NotSame(firstTexture, secondTexture);
        Assert.Same(firstDevice, firstTexture.Device);
        Assert.Same(secondDevice, secondTexture.Device);
        Assert.NotSame(first.MissingTexture, second.MissingTexture);
        Assert.NotSame(first.MissingTextureSrv, second.MissingTextureSrv);
    }

    [Fact]
    public async Task ShapeReloadUsesNewBackingWhileOldFrameCompletionRemainsTracked()
    {
        if (!OperatingSystem.IsWindows())
            return;

        using var assets = new TemporaryAssetProject();
        AssetGuid guid = AssetGuid.New();
        string beforePath = assets.File("before.texture.asset");
        string afterPath = assets.File("after.texture.asset");
        EngineTexture beforeAsset = SingleMipTexture("before", 2, 1, [1, 2]);
        beforeAsset.AssetGuid = guid.ToFlatString();
        EngineTexture afterAsset = SingleMipTexture("after", 4, 1, [4, 3, 2, 1]);
        afterAsset.AssetGuid = guid.ToFlatString();
        AssetWriter.Write(beforeAsset, beforePath);
        AssetWriter.Write(afterAsset, afterPath);
        var storage = new MutableAssetStorage();
        storage.Publish(guid, beforePath);
        await using var loader = new AssetLoader(storage);
        EngineTexture asset = await loader.LoadAsync(new AssetId<EngineTexture>(guid));

        using IGraphicsBackend backend = D3D12GraphicsBackend.Create();
        using Device device = CreateWarpDevice(backend, "RenderDevice reload test");
        using var renderDevice = new RenderDevice(backend, device);
        using var graph = CreateGraph(backend, device);

        RhiTexture oldBacking = ExecuteTextureFrame(graph, renderDevice, asset);

        storage.Publish(guid, afterPath);
        EngineTexture reloadResult = await loader.ReloadAsync(asset);
        Assert.Same(asset, reloadResult);
        await loader.ApplyUpdatesAsync();

        RhiTexture canceledReplacement = CancelTextureFrame(graph, renderDevice, asset);
        Assert.NotSame(oldBacking, canceledReplacement);
        Assert.Same(oldBacking, renderDevice.GetTexture(asset));

        RhiTexture newBacking = ExecuteTextureFrame(graph, renderDevice, asset);

        Assert.Same(asset, reloadResult);
        Assert.NotSame(oldBacking, newBacking);
        Assert.Equal(2u, oldBacking.Info.Width);
        Assert.Equal(4u, newBacking.Info.Width);
        renderDevice.WaitForTrackedSubmissions(TimeSpan.FromSeconds(10));
        renderDevice.CollectCompleted();
    }

    private static RhiTexture ExecuteTextureFrame(
        RenderGraph.RenderGraph graph,
        RenderDevice renderDevice,
        EngineTexture asset)
    {
        var completions = new QueueCompletion[graph.MaximumQueueCompletionCount];
        RhiTexture resource;
        int count;
        RenderGraphFrame frame = graph.BeginFrame();
        try
        {
            GraphPassId consumer = frame.AddGeneralPass(
                "Texture test consumer",
                PassQueueSelection.AnyOfType(QueueType.Graphics),
                0,
                new PassOptions(Culling: PassCullingMode.NeverCull),
                static (ref PassDefinition _, ref int _) => { },
                static (ref GeneralPassCommandScope _, in int _) => { });
            resource = renderDevice.PrepareTexture(ref frame, consumer, asset);
            TextureSrv view = renderDevice.GetTextureSrv(asset);
            GraphTextureId graphTexture = frame.GetImported(resource);
            GraphTextureSrvId graphView = frame.CreateTextureSrv(
                graphTexture,
                view.Description.Range,
                view.Description.Format,
                view.Description.Dimension,
                "Texture test graph view");
            _ = frame.AddGeneralPass(
                "Texture test reader",
                PassQueueSelection.AnyOfType(QueueType.Graphics),
                graphView,
                new PassOptions(Culling: PassCullingMode.NeverCull),
                static (ref PassDefinition definition, ref GraphTextureSrvId value) =>
                    _ = definition.Read(value, PipelineSync.AllShading),
                static (ref GeneralPassCommandScope _, in GraphTextureSrvId _) => { });
            count = frame.Execute(completions);
        }
        finally
        {
            frame.Dispose();
        }
        renderDevice.TrackSubmission(completions.AsSpan(0, count));
        return resource;
    }

    private static RhiTexture CancelTextureFrame(
        RenderGraph.RenderGraph graph,
        RenderDevice renderDevice,
        EngineTexture asset)
    {
        RenderGraphFrame frame = graph.BeginFrame();
        try
        {
            GraphPassId consumer = frame.AddGeneralPass(
                "Canceled Texture consumer",
                PassQueueSelection.AnyOfType(QueueType.Graphics),
                0,
                new PassOptions(Culling: PassCullingMode.NeverCull),
                static (ref PassDefinition _, ref int _) => { },
                static (ref GeneralPassCommandScope _, in int _) => { });
            return renderDevice.PrepareTexture(ref frame, consumer, asset);
        }
        finally
        {
            frame.Dispose();
        }
    }

    private static RenderGraph.RenderGraph CreateGraph(
        IGraphicsBackend backend,
        Device device)
        => new(
            backend,
            device,
            [backend.GetQueue(device, QueueType.Graphics)]);

    private static Device CreateWarpDevice(IGraphicsBackend backend, string label)
    {
        AdapterEnumerationOptions options = new(
            AdapterPreference.HighPerformance,
            IncludeSoftware: true);
        _ = backend.TryEnumerateAdapters(options, [], out int count);
        if (count == 0)
            throw new NotSupportedException("No Direct3D 12 adapter is available.");
        var adapters = new AdapterInfo[count];
        if (!backend.TryEnumerateAdapters(options, adapters, out int confirmed) ||
            confirmed != adapters.Length)
        {
            throw new InvalidOperationException("The adapter set changed during enumeration.");
        }
        AdapterInfo warp = adapters.FirstOrDefault(static adapter => !adapter.HardwareAccelerated);
        if (string.IsNullOrWhiteSpace(warp.Name))
            throw new NotSupportedException("The Direct3D 12 WARP adapter is unavailable.");
        return backend.CreateDevice(new DeviceDesc(
            warp.Id,
            [new DeviceQueueDesc(QueueType.Graphics)],
            label: label));
    }

    private static EngineTexture TwoLayerStreamedTexture(string name)
        => new()
        {
            Name = name,
            Width = 2,
            Height = 2,
            Dimension = GraphicsTextureDimension.Texture2D,
            Depth = 1,
            MipLevelCount = 2,
            ArrayLayerCount = 2,
            Format = Format.R8UNorm,
            SampledFormat = Format.R8UNorm,
            SampledDimension = GraphicsTextureViewDimension.Texture2DArray,
            MipTiles =
            [
                Tile(0, 0, 2, 2, new byte[4]),
                Tile(0, 1, 2, 2, new byte[4]),
                Tile(1, 0, 1, 1, [11]),
                Tile(1, 1, 1, 1, [29]),
            ],
        };

    private static EngineTexture SingleMipTexture(
        string name,
        uint width,
        uint height,
        byte[] payload)
        => new()
        {
            Name = name,
            Width = width,
            Height = height,
            Dimension = GraphicsTextureDimension.Texture2D,
            Depth = 1,
            MipLevelCount = 1,
            ArrayLayerCount = 1,
            Format = Format.R8UNorm,
            SampledFormat = Format.R8UNorm,
            SampledDimension = GraphicsTextureViewDimension.Texture2D,
            MipTiles = [Tile(0, 0, width, height, payload)],
        };

    private static TextureMipTile Tile(
        uint mip,
        uint layer,
        uint width,
        uint height,
        byte[] payload)
        => new()
        {
            MipLevel = mip,
            ArrayLayer = layer,
            Width = width,
            Height = height,
            RowPitch = width,
            SlicePitch = checked((ulong)width * height),
            Payload = payload,
        };

    private sealed class TemporaryAssetProject : IDisposable
    {
        internal TemporaryAssetProject()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"SomeEngine-RenderDevice-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
            Project = new AssetProject(Path, []);
        }

        internal string Path { get; }
        internal AssetProject Project { get; }
        internal string File(string name) => System.IO.Path.Combine(Path, name);

        public void Dispose()
        {
            if (Directory.Exists(Path))
                Directory.Delete(Path, recursive: true);
        }
    }

    private sealed class MutableAssetStorage : IAssetStorage
    {
        private readonly object _gate = new();
        private AssetEntry _entry;
        private string? _path;

        internal void Publish(AssetGuid guid, string path)
        {
            lock (_gate)
            {
                _entry = new AssetEntry(
                    guid,
                    AssetType<EngineTexture>.Name,
                    EngineTexture.SchemaFingerprint,
                    Guid.NewGuid());
                _path = path;
            }
        }

        public bool TryFind(AssetGuid assetGuid, out AssetEntry entry)
        {
            lock (_gate)
            {
                if (_path is not null && assetGuid == _entry.AssetGuid)
                {
                    entry = _entry;
                    return true;
                }
            }
            entry = default;
            return false;
        }

        public ValueTask<IRangeSource> OpenAsync(
            AssetEntry entry,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                if (_path is null || entry != _entry)
                    throw new InvalidDataException("The asset publication is no longer current.");
                return ValueTask.FromResult<IRangeSource>(FileRangeSource.Open(_path));
            }
        }
    }
}
