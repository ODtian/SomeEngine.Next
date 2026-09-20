using SomeEngine.Assets;
using SomeEngine.Assets.Pipeline;
using SomeEngine.Assets.Schema;
using SomeEngine.Resources;
using SomeEngine.Graphics;
using SomeEngine.Graphics.Direct3D12;
using SomeEngine.RenderGraph;
using SomeEngine.Serialization.IO;
using SomeEngine.Serialization.Containers;
using SomeEngine.Serialization.Streaming;
using EngineTexture = SomeEngine.Resources.Texture;
using EngineTextureAsset = SomeEngine.Assets.Schema.TextureAsset;
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
        using var source = SingleMipTexture("canceled-first-use", 2, 1, [8, 9]);
        Guid guid = assets.Project.RegisterAsset(await source.SaveAsync(Path.Combine(assets.Project.RootPath, "assets/canceled-first-use.texture.asset")));
        await using var loader = new AssetLoader(assets.Project.CreateStorage());
        await using EngineTextureAsset file = await EngineTextureAsset.OpenAsync(loader, new AssetId<EngineTextureAsset>(guid));
        await using EngineTexture asset = await file.GetBlock<TextureBlock>().CreateAsync(file.Name);
        using IGraphicsBackend backend = D3D12GraphicsBackend.Create();
        using Device device = CreateWarpDevice(backend, "RenderDevice cancel test");
        using var renderDevice = new RenderDevice(backend, device);
        using var graph = CreateGraph(backend, device);

        RhiTexture canceledBacking = CancelTextureFrame(graph, renderDevice, asset);

        Assert.Same(canceledBacking, renderDevice.GetTexture(asset.CaptureData()));
        Assert.Throws<InvalidOperationException>(() => renderDevice.GetTextureSrv(asset.CaptureData()));

        RhiTexture executedBacking = ExecuteTextureFrame(graph, renderDevice, asset);
        Assert.Same(canceledBacking, executedBacking);
        _ = renderDevice.GetTextureSrv(asset.CaptureData());
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
            _ = renderDevice.PrepareTexture(ref frame, consumer, asset.CaptureData());
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
        Assert.Throws<InvalidOperationException>(() => renderDevice.GetTextureSrv(asset.CaptureData()));
        _ = ExecuteTextureFrame(graph, renderDevice, asset);
        _ = renderDevice.GetTextureSrv(asset.CaptureData());
    }

    [Fact]
    public async Task BlockCompressedBootstrapUsesItsNativeCopyFootprint()
    {
        if (!OperatingSystem.IsWindows())
            return;

        using var assets = new TemporaryAssetProject();
        using var pixels = new TextureBlock
            {
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
                        ChunkKey = TextureTiles.ChunkKey(0, 0, 0), DecodedLength = 16,
                    },
                ],
            };
        pixels.Chunks.Set(pixels.MipTiles[0].ChunkKey, new byte[16]);
        using var original = new EngineTextureAsset { Name = "compressed-bootstrap" };
        original.SetBlock(pixels);
        Guid guid = assets.Project.RegisterAsset(await original.SaveAsync(Path.Combine(assets.Project.RootPath, "assets/compressed-bootstrap.texture.asset")));
        await using var loader = new AssetLoader(assets.Project.CreateStorage());
        await using EngineTextureAsset file = await EngineTextureAsset.OpenAsync(loader, new AssetId<EngineTextureAsset>(guid));
        await using EngineTexture asset = await file.GetBlock<TextureBlock>().CreateAsync(file.Name);
        ChunkStreamingSnapshot before = file.StreamingMetrics!.Snapshot();
        using IGraphicsBackend backend = D3D12GraphicsBackend.Create();
        using Device device = CreateWarpDevice(backend, "RenderDevice BC test");
        using var renderDevice = new RenderDevice(backend, device);
        using var graph = CreateGraph(backend, device);

        _ = ExecuteTextureFrame(graph, renderDevice, asset);

        ChunkStreamingSnapshot after = file.StreamingMetrics.Snapshot();
        Assert.Equal(before.LoadsCompleted, after.LoadsCompleted);
        Assert.Equal(before.StoredBytesRead, after.StoredBytesRead);
    }

    [Fact]
    public async Task FirstPreparationUsesResidentBootstrapWithoutIssuingAssetIo()
    {
        if (!OperatingSystem.IsWindows())
            return;

        using var assets = new TemporaryAssetProject();
        using var source = TwoLayerStreamedTexture("first-use");
        Guid guid = assets.Project.RegisterAsset(await source.SaveAsync(Path.Combine(assets.Project.RootPath, "assets/first-use.texture.asset")));
        await using var loader = new AssetLoader(assets.Project.CreateStorage());
        await using EngineTextureAsset file = await EngineTextureAsset.OpenAsync(loader, new AssetId<EngineTextureAsset>(guid));
        await using EngineTexture asset = await file.GetBlock<TextureBlock>().CreateAsync(file.Name);
        ChunkStreamingSnapshot before = file.StreamingMetrics!.Snapshot();

        using IGraphicsBackend backend = D3D12GraphicsBackend.Create();
        using Device device = CreateWarpDevice(backend, "RenderDevice first-use test");
        using var renderDevice = new RenderDevice(backend, device);
        using var graph = CreateGraph(backend, device);

        RhiTexture resource = ExecuteTextureFrame(graph, renderDevice, asset);
        ChunkStreamingSnapshot after = file.StreamingMetrics.Snapshot();

        Assert.Same(resource, renderDevice.GetTexture(asset.CaptureData()));
        Assert.Equal(before.LoadsCompleted, after.LoadsCompleted);
        Assert.Equal(before.StoredBytesRead, after.StoredBytesRead);
        Assert.Equal(before.CacheHits, after.CacheHits);
        Assert.Equal(before.CacheMisses, after.CacheMisses);

        using (var secondGraph = CreateGraph(backend, device))
        {
            RhiTexture secondGraphResource = ExecuteTextureFrame(
                secondGraph,
                renderDevice,
                asset);
            Assert.Same(resource, secondGraphResource);
        }

        TextureSrv defaultView = renderDevice.GetTextureSrv(asset.CaptureData());
        Assert.Equal(1u, defaultView.Description.Range.FirstMipLevel);
        Assert.Equal(1u, defaultView.Description.Range.MipLevelCount);
        Assert.Equal(2u, defaultView.Description.Range.ArrayLayerCount);

        TextureSubresourceRange firstLayer = new(1, 1, 0, 1, TextureAspects.Color);
        TextureSrv first = renderDevice.GetTextureSrv(
            asset.CaptureData(),
            firstLayer,
            Format.R8UNorm,
            GraphicsTextureViewDimension.Texture2DArray,
            "first label");
        TextureSrv sameDescription = renderDevice.GetTextureSrv(
            asset.CaptureData(),
            firstLayer,
            Format.R8UNorm,
            GraphicsTextureViewDimension.Texture2DArray,
            "ignored cache label");
        TextureSrv secondLayer = renderDevice.GetTextureSrv(
            asset.CaptureData(),
            new TextureSubresourceRange(1, 1, 1, 1, TextureAspects.Color),
            Format.R8UNorm,
            GraphicsTextureViewDimension.Texture2DArray,
            "second layer");

        Assert.Same(first, sameDescription);
        Assert.NotSame(first, secondLayer);

        TextureUav firstUav = renderDevice.GetTextureUav(
            asset.CaptureData(),
            firstLayer,
            Format.R8UNorm,
            GraphicsTextureViewDimension.Texture2DArray,
            "first UAV");
        TextureUav sameUav = renderDevice.GetTextureUav(
            asset.CaptureData(),
            firstLayer,
            Format.R8UNorm,
            GraphicsTextureViewDimension.Texture2DArray,
            "ignored UAV label");
        TextureUav secondUav = renderDevice.GetTextureUav(
            asset.CaptureData(),
            new TextureSubresourceRange(1, 1, 1, 1, TextureAspects.Color),
            Format.R8UNorm,
            GraphicsTextureViewDimension.Texture2DArray,
            "second UAV");
        Assert.Same(firstUav, sameUav);
        Assert.NotSame(firstUav, secondUav);

        await file.GetBlock<TextureBlock>().LoadMipAsync(asset, 0);
        Assert.True(asset.TryRetainTile(TextureTiles.ChunkKey(0, 0, 0), out var mipZeroLayerZero));
        Assert.True(asset.TryRetainTile(TextureTiles.ChunkKey(0, 1, 0, 0, 0, 0), out var mipZeroLayerOne));
        using (mipZeroLayerZero)
        using (mipZeroLayerOne)
        {
            Assert.Equal(4, mipZeroLayerZero.Sequence.Length);
            Assert.Equal(4, mipZeroLayerOne.Sequence.Length);
        }

        _ = ExecuteTextureFrame(graph, renderDevice, asset);
        TextureSrv refinedView = renderDevice.GetTextureSrv(asset.CaptureData());
        Assert.Equal(0u, refinedView.Description.Range.FirstMipLevel);
        Assert.Equal(2u, refinedView.Description.Range.MipLevelCount);
    }

    [Fact]
    public async Task TextureAndMissingRealizationsAreIndependentAcrossDevices()
    {
        if (!OperatingSystem.IsWindows())
            return;

        using var assets = new TemporaryAssetProject();
        using var source = SingleMipTexture("multi-device", 2, 1, [3, 7]);
        Guid guid = assets.Project.RegisterAsset(await source.SaveAsync(Path.Combine(assets.Project.RootPath, "assets/multi-device.texture.asset")));
        await using var loader = new AssetLoader(assets.Project.CreateStorage());
        await using EngineTextureAsset file = await EngineTextureAsset.OpenAsync(loader, new AssetId<EngineTextureAsset>(guid));
        await using EngineTexture asset = await file.GetBlock<TextureBlock>().CreateAsync(file.Name);

        using IGraphicsBackend backend = D3D12GraphicsBackend.Create();
        using Device firstDevice = CreateWarpDevice(backend, "RenderDevice A");
        using Device secondDevice = CreateWarpDevice(backend, "RenderDevice B");
        using var first = new RenderDevice(backend, firstDevice);
        using var second = new RenderDevice(backend, secondDevice);

        RhiTexture firstTexture = first.GetTexture(asset.CaptureData());
        RhiTexture secondTexture = second.GetTexture(asset.CaptureData());

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
        using EngineTextureAsset beforeAsset = SingleMipTexture("before", 2, 1, [1, 2], new(guid));

        using EngineTextureAsset afterAsset = SingleMipTexture("after", 4, 1, [4, 3, 2, 1], new(guid));

        await beforeAsset.SaveAsync(beforePath);
        await afterAsset.SaveAsync(afterPath);
        var storage = new MutableAssetStorage();
        storage.Publish(guid, beforePath);
        await using var loader = new AssetLoader(storage);
        await using EngineTextureAsset file = await EngineTextureAsset.OpenAsync(loader, new AssetId<EngineTextureAsset>(guid));
        await using EngineTexture asset = await file.GetBlock<TextureBlock>().CreateAsync(file.Name);

        using IGraphicsBackend backend = D3D12GraphicsBackend.Create();
        using Device device = CreateWarpDevice(backend, "RenderDevice reload test");
        using var renderDevice = new RenderDevice(backend, device);
        using var graph = CreateGraph(backend, device);

        RhiTexture oldBacking = ExecuteTextureFrame(graph, renderDevice, asset);

        storage.Publish(guid, afterPath);
        await using EngineTextureAsset nextFile = await EngineTextureAsset.OpenAsync(loader, new AssetId<EngineTextureAsset>(guid));
        Task canceledUpdate = nextFile.GetBlock<TextureBlock>().UpdateAsync(asset, nextFile.Name).AsTask();
        await WaitForUpdateAsync(asset, canceledUpdate);
        RhiTexture canceledReplacement = CancelTextureFrame(graph, renderDevice, asset);
        Assert.NotSame(oldBacking, canceledReplacement);
        Assert.Same(oldBacking, renderDevice.GetTexture(asset));
        Assert.Equal("before", asset.Name);
        Assert.Equal(2u, asset.Width);
        Task update = nextFile.GetBlock<TextureBlock>().UpdateAsync(asset, nextFile.Name).AsTask();
        await WaitForUpdateAsync(asset, update);
        RhiTexture newBacking = ExecuteTextureFrame(graph, renderDevice, asset);

        Assert.Same(asset, reloadResult);
        Assert.NotSame(oldBacking, newBacking);
        Assert.Equal(2u, oldBacking.Info.Width);
        Assert.Equal(4u, newBacking.Info.Width);
        renderDevice.WaitForTrackedSubmissions(TimeSpan.FromSeconds(10));
        renderDevice.CollectCompleted();
    }

    [Fact]
    public async Task UploadFailureLeavesOldTextureContentAndAllocationUsable()
    {
        using var assets = new TemporaryAssetProject();
        using EngineTextureAsset before = SingleMipTexture("before", 2, 1, [7, 8]);
        Guid id = assets.Project.RegisterAsset(await before.SaveAsync(Path.Combine(assets.Project.RootPath, "before.texture.asset")));
        await using var loader = new AssetLoader(assets.Project.CreateStorage());
        await using EngineTextureAsset file = await EngineTextureAsset.OpenAsync(loader, new AssetId<EngineTextureAsset>(id));
        await using EngineTexture texture = await file.GetBlock<TextureBlock>().CreateAsync(file.Name);
        using IGraphicsBackend backend = D3D12GraphicsBackend.Create();
        using Device device = CreateWarpDevice(backend, "texture failed update");
        using var renderDevice = new RenderDevice(backend, device);
        using var graph = CreateGraph(backend, device);
        RhiTexture old = ExecuteTextureFrame(graph, renderDevice, texture);
        using var fields = SingleMipTexture("invalid upload", 4, 1, [3]);
        fields.GetBlock<TextureBlock>().MipTiles[0] = fields.GetBlock<TextureBlock>().MipTiles[0] with { RowPitch = 0, SlicePitch = 0 };
        using EngineTextureAsset bad = fields.Duplicate();
        Task update = bad.GetBlock<TextureBlock>().UpdateAsync(texture, bad.Name).AsTask();
        await WaitForUpdateAsync(texture, update);
        Assert.Throws<InvalidDataException>(() => CancelTextureFrame(graph, renderDevice, texture));
        await Assert.ThrowsAsync<InvalidDataException>(() => update);
        Assert.Equal("before", texture.Name);
        Assert.Equal(2u, texture.Width);
        Assert.Same(old, ExecuteTextureFrame(graph, renderDevice, texture));
    }

    [Fact]
    public void ProgrammaticTextureReleaseWaitsForItsRecordedFrameAndLeavesOtherInstancesAlive()
    {
        using IGraphicsBackend backend = D3D12GraphicsBackend.Create();
        using Device device = CreateWarpDevice(backend, "texture release");
        using var renderDevice = new RenderDevice(backend, device);
        using var graph = CreateGraph(backend, device);
        var description = new TextureDesc(GraphicsTextureDimension.Texture2D, 1, 1, 1, 1, 1, 1,
            Format.R8UNorm, TextureUsages.Sampled | TextureUsages.CopyDestination, label: "generated");
        using EngineTexture first = EngineTexture.Create(description,
            [Tile(0, 0, 1, 1, [1])], GraphicsTextureViewDimension.Texture2D);
        using EngineTexture second = EngineTexture.Create(description,
            [Tile(0, 0, 1, 1, [2])], GraphicsTextureViewDimension.Texture2D);
        Assert.False(first.TryGetTexture(device, out _));
        RhiTexture other = renderDevice.GetTexture(second);
        RhiTexture resource;
        var completions = new QueueCompletion[graph.MaximumQueueCompletionCount];
        int count;
        RenderGraphFrame frame = graph.BeginFrame();
        try
        {
            GraphPassId consumer = frame.AddGeneralPass("live texture", PassQueueSelection.AnyOfType(QueueType.Graphics), 0,
                new PassOptions(Culling: PassCullingMode.NeverCull),
                static (ref PassDefinition _, ref int _) => { }, static (ref GeneralPassCommandScope _, in int _) => { });
            resource = renderDevice.PrepareTexture(ref frame, consumer, first);
            Assert.NotSame(resource, other);
            first.Dispose();
            Assert.False(first.TryGetTexture(device, out _));
            using TextureSrv readable = backend.CreateTextureSrv(device, new TextureSrvDesc(resource,
                new(0, 1, 0, 1, TextureAspects.Color), Format.R8UNorm, GraphicsTextureViewDimension.Texture2D));
            count = frame.Execute(completions);
        }
        finally { frame.Dispose(); }
        renderDevice.TrackSubmission(completions.AsSpan(0, count));
        renderDevice.WaitForTrackedSubmissions(TimeSpan.FromSeconds(10));
        graph.Dispose();
        renderDevice.CollectCompleted();
        Assert.Throws<ObjectDisposedException>(() => backend.CreateTextureSrv(device, new TextureSrvDesc(resource,
            new(0, 1, 0, 1, TextureAspects.Color), Format.R8UNorm, GraphicsTextureViewDimension.Texture2D)));
        using TextureSrv otherView = backend.CreateTextureSrv(device, new TextureSrvDesc(other,
            new(0, 1, 0, 1, TextureAspects.Color), Format.R8UNorm, GraphicsTextureViewDimension.Texture2D));
    }

    [Theory]
    [InlineData(GraphicsTextureViewDimension.Cube, 6u, 1u)]
    [InlineData(GraphicsTextureViewDimension.CubeArray, 12u, 1u)]
    [InlineData(GraphicsTextureViewDimension.Texture3D, 1u, 2u)]
    public void CubeArrayAndVolumeContentUploadsWithoutDisk(GraphicsTextureViewDimension view, uint layers, uint depth)
    {
        bool volume = view == GraphicsTextureViewDimension.Texture3D;
        var tiles = new List<TexturePixels>();
        uint cubes = volume ? 1 : layers / 6;
        for (uint layer = 0; layer < cubes; layer++)
        for (uint face = 0; face < (volume ? 1 : 6); face++)
        for (uint slice = 0; slice < depth; slice++)
            tiles.Add(new(new TextureMipTile { Width = 2, Height = 2, ArrayLayer = layer, Face = face,
                DepthSlice = slice, RowPitch = 2, SlicePitch = 4 }, new byte[] { 1, 2, 3, 4 }));
        using EngineTexture texture = EngineTexture.Create(new TextureDesc(
            volume ? GraphicsTextureDimension.Texture3D : GraphicsTextureDimension.Texture2D,
            2, 2, depth, 1, layers, 1, Format.R8UNorm, TextureUsages.Sampled | TextureUsages.CopyDestination), tiles, view);
        using IGraphicsBackend backend = D3D12GraphicsBackend.Create();
        using Device device = CreateWarpDevice(backend, "texture shape");
        using var renderDevice = new RenderDevice(backend, device);
        using var graph = CreateGraph(backend, device);
        RhiTexture resource = ExecuteTextureFrame(graph, renderDevice, texture);
        Assert.Equal(layers, resource.Info.ArrayLayerCount);
        Assert.Equal(depth, resource.Info.Depth);
        Assert.Equal(view, renderDevice.GetTextureSrv(texture).Description.Dimension);
        renderDevice.WaitForTrackedSubmissions(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task ProceduralUpdateWaitsForBothDevicesAndDeviceRemovalDoesNotBlockLaterUpdates()
    {
        var original = new TextureDesc(GraphicsTextureDimension.Texture2D,
            1, 1, 1, 1, 1, 1, Format.R8UNorm, TextureUsages.Sampled | TextureUsages.CopyDestination);
        using EngineTexture texture = EngineTexture.Create(original,
            [Tile(0, 0, 1, 1, [7])], GraphicsTextureViewDimension.Texture2D);
        using IGraphicsBackend backend = D3D12GraphicsBackend.Create();
        using Device firstDevice = CreateWarpDevice(backend, "procedural update first");
        using Device secondDevice = CreateWarpDevice(backend, "procedural update second");
        using var firstRenderer = new RenderDevice(backend, firstDevice);
        using var secondRenderer = new RenderDevice(backend, secondDevice);
        using var firstGraph = CreateGraph(backend, firstDevice);
        using var secondGraph = CreateGraph(backend, secondDevice);
        RhiTexture oldFirst = ExecuteTextureFrame(firstGraph, firstRenderer, texture);
        RhiTexture oldSecond = ExecuteTextureFrame(secondGraph, secondRenderer, texture);

        var changed = new TextureDesc(GraphicsTextureDimension.Texture2D,
            2, 2, 1, 1, 1, 1, Format.R8UNorm, TextureUsages.Sampled | TextureUsages.CopyDestination);
        Task update = texture.UpdateAsync(changed,
            [Tile(0, 0, 2, 2, [1, 2, 3, 4])], GraphicsTextureViewDimension.Texture2D).AsTask();
        Assert.True(texture.IsUpdating);
        RhiTexture newFirst = ExecuteTextureFrame(firstGraph, firstRenderer, texture);
        Assert.False(update.IsCompleted);
        Assert.Equal(1u, texture.Width);
        Assert.NotSame(oldFirst, newFirst);
        Assert.Same(oldSecond, secondRenderer.GetTexture(texture));
        RhiTexture newSecond = ExecuteTextureFrame(secondGraph, secondRenderer, texture);
        await update.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.NotSame(oldSecond, newSecond);
        Assert.Equal(2u, texture.Width);
        Assert.Equal(2ul, texture.Revision);

        Task nextUpdate = texture.UpdateAsync(new TextureDesc(GraphicsTextureDimension.Texture2D,
            1, 1, 1, 1, 1, 1, Format.R8UNorm, TextureUsages.Sampled | TextureUsages.CopyDestination),
            [Tile(0, 0, 1, 1, [9])], GraphicsTextureViewDimension.Texture2D).AsTask();
        firstRenderer.ReleaseTexture(texture);
        Assert.False(texture.TryGetTexture(firstDevice, out _));
        ExecuteTextureFrame(secondGraph, secondRenderer, texture);
        await nextUpdate.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(1u, texture.Width);
        Assert.Equal(3ul, texture.Revision);
        firstRenderer.WaitForTrackedSubmissions(TimeSpan.FromSeconds(10));
        secondRenderer.WaitForTrackedSubmissions(TimeSpan.FromSeconds(10));
    }

    private static async Task WaitForUpdateAsync(EngineTexture texture, Task update)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!texture.IsUpdating)
        {
            if (update.IsCompleted) { await update; return; }
            await Task.Delay(1, timeout.Token);
        }
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
            resource = renderDevice.PrepareTexture(ref frame, consumer, asset.CaptureData());
            TextureSrv view = renderDevice.GetTextureSrv(asset.CaptureData());
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
            return renderDevice.PrepareTexture(ref frame, consumer, asset.CaptureData());
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

    private static EngineTextureAsset TwoLayerStreamedTexture(string name)
        => WithPixels(name, new TextureBlock
        {
            Width = 2,
            Height = 2,
            Dimension = GraphicsTextureDimension.Texture2D,
            Depth = 1,
            MipLevelCount = 2,
            ArrayLayerCount = 2,
            Format = Format.R8UNorm,
            SampledFormat = Format.R8UNorm,
            SampledDimension = GraphicsTextureViewDimension.Texture2DArray,
        }, [
                Tile(0, 0, 2, 2, new byte[4]),
                Tile(0, 1, 2, 2, new byte[4]),
                Tile(1, 0, 1, 1, [11]),
                Tile(1, 1, 1, 1, [29]),
            ]);

    private static EngineTextureAsset SingleMipTexture(
        string name,
        uint width,
        uint height,
        byte[] payload,
        AssetId<EngineTextureAsset> id = default)
        => WithPixels(name, new TextureBlock
        {
            Width = width,
            Height = height,
            Dimension = GraphicsTextureDimension.Texture2D,
            Depth = 1,
            MipLevelCount = 1,
            ArrayLayerCount = 1,
            Format = Format.R8UNorm,
            SampledFormat = Format.R8UNorm,
            SampledDimension = GraphicsTextureViewDimension.Texture2D,
        }, [Tile(0, 0, width, height, payload)], id.Value);

    private static TexturePixels Tile(
        uint mip,
        uint layer,
        uint width,
        uint height,
        byte[] payload)
        => new(new TextureMipTile
        {
            MipLevel = mip,
            ArrayLayer = layer,
            Width = width,
            Height = height,
            RowPitch = width,
            SlicePitch = checked((ulong)width * height),
            ChunkKey = TextureTiles.ChunkKey(mip, layer, 0, 0, 0, 0), DecodedLength = (ulong)payload.Length,
        }, payload);

    private static EngineTextureAsset WithPixels(string name, TextureBlock content, IReadOnlyList<TexturePixels> pixels, Guid id = default)
    {
        using (content)
        {
            content.MipTiles = pixels.Select(static item => item.Tile).ToArray();
            foreach (TexturePixels item in pixels) content.Chunks.Set(item.Tile.ChunkKey, item.Bytes.Span);
            var asset = new EngineTextureAsset(PageArena.Shared, id) { Name = name };
            try { asset.SetBlock(content); return asset; }
            catch { asset.Dispose(); throw; }
        }
    }

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
                    EngineTextureAsset.FileType,
                    EngineTextureAsset.SchemaHash,
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
