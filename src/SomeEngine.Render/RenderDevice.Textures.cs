using System.Buffers;
using SomeEngine.Resources;
using SomeEngine.Graphics;
using SomeEngine.RenderGraph;
using SomeEngine.Serialization.Containers;
using EngineTexture = SomeEngine.Resources.Texture;
using RhiTexture = SomeEngine.Graphics.Texture;
using TextureDimension = SomeEngine.Graphics.TextureDimension;
using TextureViewDimension = SomeEngine.Graphics.TextureViewDimension;

namespace SomeEngine.Render;

public sealed partial class RenderDevice
{
    private readonly object _textureGate = new();
    private readonly HashSet<TextureRealization> _textures = [];
    private readonly Queue<TextureRealization> _textureReleases = new();
    private readonly Dictionary<EngineTexture, TextureRealization> _textureAllocations = [];
    private TextureRealization? _missingTexture;

    public void OrderTextureUploadsBefore(ref RenderGraphFrame frame, GraphPassId consumer)
    {
        TextureTransfers transfers = GetTextureTransfers(ref frame);
        frame.OrderAfter(consumer, transfers.CompletionPass);
    }

    public RhiTexture MissingTexture => RequireMissingTexture().Resource;
    public TextureSrv MissingTextureSrv
    {
        get
        {
            TextureRealization data = RequireMissingTexture();
            return GetTextureSrv(data, data.Shape.FullRange, data.Shape.SampledFormat,
                data.Shape.SampledDimension, "RenderDevice missing texture SRV");
        }
    }

    /// <summary>Creates the graph allocation for resident texture content before constructing a frame.</summary>
    public RhiTexture PrepareTexture(EngineTexture asset)
    {
        TextureRealization data = GetOrCreateTexture(asset);
        _ = GetTextureSrv(asset);
        return data.Resource;
    }
    public RhiTexture GetTexture(EngineTexture asset) => GetOrCreateTexture(asset).Resource;

    public TextureSrv GetTextureSrv(EngineTexture asset, TextureSubresourceRange? range = null,
        Format? format = null, TextureViewDimension? dimension = null, string? label = null)
    {
        TextureRealization data = GetOrCreateTexture(asset);
        TextureShape shape = data.Shape;
        uint first = FirstAvailableMip(asset.GetUploadContent());
        TextureSrv view = GetTextureSrv(data, range ?? shape.ResidentRange(first),
            format ?? shape.SampledFormat, dimension ?? shape.SampledDimension, label);
        if (range is null && format is null && dimension is null) UpdateSampledDescriptor(data, view);
        return view;
    }

    public TextureUav GetTextureUav(EngineTexture asset, in TextureSubresourceRange range,
        Format? format = null, TextureViewDimension? dimension = null, string? label = null)
    {
        TextureRealization data = GetOrCreateTexture(asset);
        Format resolvedFormat = format ?? data.Shape.SampledFormat;
        TextureViewDimension resolvedDimension = dimension ?? data.Shape.SampledDimension;
        var key = new TextureViewKey(TextureViewKind.Uav, range, resolvedFormat, resolvedDimension);
        if (data.Views.TryGetValue(key, out DeviceResource? cached)) return (TextureUav)cached;
        TextureUav view = _backend.CreateTextureUav(_device,
            new(data.Resource, range, resolvedFormat, resolvedDimension, label));
        data.Views.Add(key, view);
        return view;
    }

    public RhiTexture PrepareTexture(ref RenderGraphFrame frame, GraphPassId consumer, EngineTexture asset) =>
        PrepareTexture(ref frame, [consumer], asset);

    public RhiTexture PrepareTexture(ref RenderGraphFrame frame,
        scoped ReadOnlySpan<GraphPassId> consumers, EngineTexture asset)
    {
        TextureRealization data = GetTextureAllocation(asset);
        EngineTexture content = asset.GetUploadContent();
        TextureFrame uploads = GetTextureFrame(ref frame, data, asset, content);
        if (!uploads.Discovered)
        {
            foreach (TextureMipTile tile in content.MipTiles ?? [])
            {
                if (uploads.Chunks.Contains(tile.ChunkKey)) continue;
                if (!content.TryGetTile(tile.ChunkKey, out ReadOnlyMemory<byte> pixels)) continue;
                ScheduleUpload(ref frame, uploads, tile, new(pixels));
            }
            uploads.FirstMip = FirstAvailableMip(content);
            uploads.Discovered = true;
        }
        OrderTextureConsumers(ref frame, uploads, consumers);
        return data.Resource;
    }

    public RhiTexture PrepareMissingTexture(ref RenderGraphFrame frame, GraphPassId consumer) =>
        PrepareMissingTexture(ref frame, [consumer]);

    public RhiTexture PrepareMissingTexture(ref RenderGraphFrame frame, scoped ReadOnlySpan<GraphPassId> consumers)
    {
        TextureRealization data = RequireMissingTexture();
        TextureFrame uploads = GetTextureFrame(ref frame, data, null, null);
        if (!uploads.Discovered)
        {
            if (uploads.Chunks.Count == 0)
                ScheduleUpload(ref frame, uploads, MissingTile(), new(new byte[] { 255, 0, 255, 255 }));
            uploads.FirstMip = 0;
            uploads.Discovered = true;
        }
        OrderTextureConsumers(ref frame, uploads, consumers);
        return data.Resource;
    }

    private TextureFrame GetTextureFrame(ref RenderGraphFrame frame, TextureRealization data,
        EngineTexture? asset, EngineTexture? content)
    {
        TextureFrame uploads = frame.GetFrameData(data, () => new TextureFrame(this, data, asset, content));
        frame.ObserveExecution(uploads);
        return uploads;
    }

    private void OrderTextureConsumers(ref RenderGraphFrame frame, TextureFrame uploads,
        scoped ReadOnlySpan<GraphPassId> consumers)
    {
        if (!uploads.HasUploads) return;
        TextureTransfers transfers = GetTextureTransfers(ref frame);
        foreach (GraphPassId consumer in consumers) frame.OrderAfter(consumer, transfers.CopyPass);
        if (!uploads.Finalized)
        {
            frame.GetPass(transfers.CompletionPass).Read(uploads.Texture.GraphResource,
                uploads.Texture.Shape.ResidentRange(uploads.FirstMip), PipelineSync.AllShading,
                ResourceAccess.ShaderResource, TextureLayout.ShaderResource);
            uploads.Finalized = true;
        }
        foreach (GraphPassId consumer in consumers) frame.OrderAfter(transfers.CompletionPass, consumer);
    }

    private void CreateMissingTexture()
    {
        _missingTexture = CreateTexture(new(TextureDimension.Texture2D, 1, 1, 1, 1, 1,
            Format.R8G8B8A8UNorm, Format.R8G8B8A8UNorm, TextureViewDimension.Texture2D,
            TextureAspects.Color, TextureUsages.Sampled | TextureUsages.CopyDestination),
            "RenderDevice missing texture", 0);
        _ = MissingTextureSrv;
    }

    private TextureRealization RequireMissingTexture() { ThrowIfDisposed(); return _missingTexture!; }

    private TextureRealization GetOrCreateTexture(EngineTexture asset)
    {
        ThrowIfDisposed();
        EngineTexture content = asset.GetUploadContent();
        if (_textureAllocations.TryGetValue(asset, out TextureRealization? data) && data.Revision == content.Revision)
            return data;
        if (asset.TryGetDeviceData(_device, out data) && data.Revision == content.Revision)
            return data;
        if (_textureAllocations.Remove(asset, out TextureRealization? replaced)) replaced.RequestRelease();
        data = CreateTexture(TextureShape.From(content, _device), content.Name, content.Revision);
        _textureAllocations[asset] = data;
        return data;
    }

    private TextureRealization GetTextureAllocation(EngineTexture asset)
    {
        if (_textureAllocations.TryGetValue(asset, out TextureRealization? data)) return data;
        if (asset.TryGetDeviceData(_device, out data)) return data;
        throw new InvalidOperationException("Prepare the texture allocation before beginning the render frame.");
    }

    private TextureRealization CreateTexture(in TextureShape shape, string? label, ulong revision)
    {
        Format[] formats = shape.Format == shape.SampledFormat ? [] : [shape.SampledFormat];
        var description = new TextureDesc(shape.Dimension, shape.Width, shape.Height, shape.Depth,
            shape.MipLevelCount, shape.ResourceArrayLayerCount, 1, shape.Format, shape.Usages, formats, label);
        using RenderGraphEdit edit = _graph.BeginEdit();
        GraphTextureId resource = edit.CreatePersistentTexture(description);
        edit.Commit();
        var data = new TextureRealization(this, shape, resource, _graph.GetTexture(resource), revision);
        _textures.Add(data);
        return data;
    }

    private TextureSrv GetTextureSrv(TextureRealization data, in TextureSubresourceRange range,
        Format format, TextureViewDimension dimension, string? label)
    {
        var key = new TextureViewKey(TextureViewKind.Srv, range, format, dimension);
        if (data.Views.TryGetValue(key, out DeviceResource? cached)) return (TextureSrv)cached;
        TextureSrv view = _backend.CreateTextureSrv(_device, new(data.Resource, range, format, dimension, label));
        data.Views.Add(key, view);
        return view;
    }

    private TextureTransfers GetTextureTransfers(ref RenderGraphFrame frame)
    {
        TextureTransfers transfers = frame.GetFrameData(this, static () => new TextureTransfers());
        if (transfers.CopyPass != default) return transfers;
        transfers.CopyPass = frame.AddCopyPass("Upload RenderDevice textures", PassQueueSelection.AnyOfType(QueueType.Graphics),
            transfers.Tiles, new PassOptions(Culling: PassCullingMode.NeverCull),
            static (ref PassDefinition _, ref List<TextureUploadPass> _) => { },
            static (ref CopyPassCommandScope commands, in List<TextureUploadPass> tiles) =>
            {
                foreach (TextureUploadPass tile in tiles)
                    commands.CopyBufferToTexture(new(commands.GetBuffer(tile.Upload.Buffer), tile.BufferOffset,
                        tile.BufferRowPitch, tile.BufferImageHeight, commands.GetTexture(tile.Destination), tile.MipLevel,
                        tile.ArrayLayer, tile.Aspect, tile.X, tile.Y, tile.Z, tile.Width, tile.Height, 1));
            }, position: FrameSectionPosition.Start);
        transfers.CompletionPass = frame.AddGeneralPass("Complete RenderDevice texture uploads",
            PassQueueSelection.AnyOfType(QueueType.Graphics), 0, new PassOptions(Culling: PassCullingMode.NeverCull),
            static (ref PassDefinition _, ref int _) => { }, static (ref GeneralPassCommandScope _, in int _) => { });
        frame.OrderAfter(transfers.CompletionPass, transfers.CopyPass);
        return transfers;
    }

    private void ScheduleUpload(ref RenderGraphFrame frame, TextureFrame uploads, TextureMipTile tile, ReadOnlySequence<byte> pixels)
    {
        TextureRealization data = uploads.Texture;
        TextureTileCopy copy = DescribeTextureTileCopy(data.Resource, data.Shape, tile);
        ulong validatedSize = copy.Footprint.Offset + (ulong)Math.Max(copy.Footprint.RowPitch, 1u) * Math.Max(copy.Height, 1u);
        int bytes = checked((int)Math.Max(copy.Footprint.TotalSize, validatedSize));
        GraphBufferSlice upload = frame.Upload(bytes, BufferUsages.CopySource, (pixels, tile, copy.Footprint),
            CopyTextureTile, "Texture mip tile upload");
        TextureTransfers transfers = GetTextureTransfers(ref frame);
        transfers.Tiles.Add(new(upload, data.GraphResource, upload.Range.Offset + copy.Footprint.Offset,
            copy.Footprint.RowPitch, copy.Height, copy.MipLevel, copy.ArrayLayer, copy.Aspect,
            copy.X, copy.Y, copy.Z, copy.Width, copy.Height));
        PassDefinition access = frame.GetPass(transfers.CopyPass);
        access.Read(upload.Buffer, upload.Range, PipelineSync.Copy, ResourceAccess.CopySource);
        access.Write(data.GraphResource, new(copy.MipLevel, 1, copy.ArrayLayer, 1, copy.Aspect),
            PipelineSync.Copy, ResourceAccess.CopyDestination, TextureLayout.CopyDestination,
            WriteCoverage.Partial, ResourceContentState.Defined);
        uploads.Chunks.Add(copy.ChunkKey);
        uploads.HasUploads = true;
    }

    private static uint FirstAvailableMip(EngineTexture content)
    {
        uint first = content.MipLevelCount;
        for (uint mip = content.MipLevelCount; mip > 0; mip--)
        {
            TextureMipTile[] tiles = (content.MipTiles ?? []).Where(tile => tile.MipLevel == mip - 1).ToArray();
            if (tiles.Length == 0 || tiles.Any(tile => !content.TryGetTile(tile.ChunkKey, out _))) break;
            first = mip - 1;
        }
        return first;
    }

    private TextureTileCopy DescribeTextureTileCopy(
        RhiTexture destination,
        in TextureShape shape,
        TextureMipTile tile)
    {
        uint arrayLayer = shape.ResolveArrayLayer(tile.ArrayLayer, tile.Face);
        uint z = shape.Dimension == TextureDimension.Texture3D ? tile.DepthSlice : 0;
        TextureAspects copyAspect = SelectCopyAspect(shape.Aspects);
        var copy = new BufferTextureCopy(
            null!, 0, 0, 0, destination,
            tile.MipLevel, arrayLayer, copyAspect,
            tile.TileX, tile.TileY, z,
            tile.Width, tile.Height, 1);
        Format[] permittedFormats = shape.Format == shape.SampledFormat
            ? []
            : [shape.SampledFormat];
        var description = new TextureDesc(
            shape.Dimension,
            shape.Width,
            shape.Height,
            shape.Depth,
            shape.MipLevelCount,
            shape.ResourceArrayLayerCount,
            1,
            shape.Format,
            shape.Usages,
            permittedFormats);
        TextureCopyFootprint footprint = _backend.GetTextureCopyFootprint(
            _device,
            description,
            copy,
            0);
        return new TextureTileCopy(
            footprint,
            tile.ChunkKey,
            tile.MipLevel,
            arrayLayer,
            copyAspect,
            tile.TileX,
            tile.TileY,
            z,
            tile.Width,
            tile.Height);
    }

    private static void CopyTextureTile(
        Span<byte> destination,
        (ReadOnlySequence<byte> Pixels, TextureMipTile Tile, TextureCopyFootprint Footprint) data)
    {
        ReadOnlySequence<byte> source = data.Pixels;
        TextureMipTile tile = data.Tile;
        TextureCopyFootprint footprint = data.Footprint;
        ulong sourceRowPitch = tile.RowPitch == 0 ? footprint.RowSize : tile.RowPitch;
        if (sourceRowPitch < footprint.RowSize)
            throw new InvalidDataException("A Texture tile row pitch is smaller than its RHI copy row size.");
        ulong requiredSource = checked(
            footprint.RowCount == 0
                ? 0
                : ((ulong)(footprint.RowCount - 1) * sourceRowPitch) + footprint.RowSize);
        if (requiredSource > checked((ulong)source.Length))
            throw new InvalidDataException("A Texture tile payload is shorter than its storage layout.");

        var reader = new SequenceReader<byte>(source);
        int written = 0;
        for (uint row = 0; row < footprint.RowCount; row++)
        {
            long sourceOffset = checked((long)((ulong)row * sourceRowPitch));
            int destinationOffset = checked((int)(
                footprint.Offset + ((ulong)row * footprint.RowPitch)));
            reader.Advance(sourceOffset - reader.Consumed);
            int rowBytes = checked((int)footprint.RowSize);
            destination[written..destinationOffset].Clear();
            reader.UnreadSequence.Slice(0, rowBytes).CopyTo(destination.Slice(destinationOffset, rowBytes));
            reader.Advance(rowBytes);
            written = checked(destinationOffset + rowBytes);
        }
        destination[written..].Clear();
    }

    private static TextureAspects SelectCopyAspect(TextureAspects aspects)
    {
        if ((aspects & TextureAspects.Color) != 0)
            return TextureAspects.Color;
        if ((aspects & TextureAspects.Depth) != 0)
            return TextureAspects.Depth;
        if ((aspects & TextureAspects.Stencil) != 0)
            return TextureAspects.Stencil;
        throw new InvalidDataException("A Texture has no copyable aspect.");
    }

    private static TextureMipTile MissingTile() => new()
    {
        MipLevel = 0,
        ArrayLayer = 0,
        Face = 0,
        DepthSlice = 0,
        TileX = 0,
        TileY = 0,
        Width = 1,
        Height = 1,
        RowPitch = 4,
        SlicePitch = 4,
        ChunkKey = 1,
        DecodedLength = 4,
    };

    public void ReleaseTexture(EngineTexture texture)
    {
        if (_textureAllocations.Remove(texture, out TextureRealization? prepared)) prepared.RequestRelease();
        if (texture.RemoveDeviceData(_device) is TextureRealization data) data.RequestRelease();
    }

    private void CollectTextureReleases()
    {
        while (true)
        {
            TextureRealization data;
            lock (_textureGate) if (!_textureReleases.TryDequeue(out data!)) return;
            data.Dispose();
        }
    }

    private void DisposeTextures()
    {
        foreach (TextureRealization data in _textures.ToArray()) data.Dispose();
        _textureAllocations.Clear();
        _textureReleases.Clear();
        _missingTexture = null;
    }

    private enum TextureViewKind : byte { Srv, Uav }
    private readonly record struct TextureViewKey(TextureViewKind Kind, TextureSubresourceRange Range,
        Format Format, TextureViewDimension Dimension);

    private sealed class TextureTransfers
    {
        internal readonly List<TextureUploadPass> Tiles = [];
        internal GraphPassId CopyPass;
        internal GraphPassId CompletionPass;
    }

    private sealed class TextureFrame(RenderDevice device, TextureRealization texture,
        EngineTexture? asset, EngineTexture? content) : IRenderGraphFrameExecutionObserver
    {
        internal TextureRealization Texture { get; } = texture;
        internal HashSet<ulong> Chunks { get; } = [.. texture.UploadedChunks];
        internal uint FirstMip = texture.FirstResidentMip;
        internal bool HasUploads;
        internal bool Discovered;
        internal bool Finalized;
        public void OnFrameSubmitted(in RenderGraphSubmission submission)
        {
            Texture.UploadedChunks.UnionWith(Chunks);
            Texture.FirstResidentMip = FirstMip;
            if (asset is null) return;
            asset.TryGetDeviceData(device._device, out TextureRealization? previous);
            asset.SetDeviceData(device._device, Texture);
            device._textureAllocations.Remove(asset);
            if (previous is not null && !ReferenceEquals(previous, Texture)) previous.RequestRelease();
            if (content is not null && !ReferenceEquals(asset, content)) asset.CompleteDeviceUpdate(device._device, content);
        }
    }

    private sealed class TextureRealization(RenderDevice device, TextureShape shape, GraphTextureId resource,
        RhiTexture texture, ulong revision) : IDisposable, ITextureDeviceData
    {
        internal TextureShape Shape { get; } = shape;
        internal GraphTextureId GraphResource { get; } = resource;
        internal RhiTexture Resource { get; } = texture;
        internal ulong Revision { get; } = revision;
        internal uint FirstResidentMip { get; set; } = shape.MipLevelCount;
        internal HashSet<ulong> UploadedChunks { get; } = [];
        internal Dictionary<TextureViewKey, DeviceResource> Views { get; } = [];
        internal DescriptorTable? SampledDescriptor { get; set; }
        internal TextureSrv? SampledView { get; set; }
        internal uint SampledIndex { get; set; }
        private WeakReference<EngineTexture>? _asset;
        private bool _released;
        SomeEngine.Graphics.Texture ITextureDeviceData.Texture => Resource;
        uint ITextureDeviceData.FirstAvailableMip => FirstResidentMip;
        bool ITextureDeviceData.IsValid => !_released;
        void ITextureDeviceData.ClearUpdateReferences() { }
        void ITextureDeviceData.Release() => RequestRelease();
        void ITextureDeviceData.ConfirmUpdate() { }
        void ITextureDeviceData.CancelUpdate() => RequestRelease();
        bool ITextureDeviceData.TryAttach(EngineTexture asset, EngineTexture? replacement)
        {
            _asset = new(asset);
            return !_released;
        }
        internal void RequestRelease() { lock (device._textureGate) device._textureReleases.Enqueue(this); }
        public void Dispose()
        {
            if (_released) return;
            _released = true;
            if (_asset?.TryGetTarget(out EngineTexture? asset) == true) asset.RemoveDeviceData(device._device, this);
            device._textures.Remove(this);
            if (!device._graph.IsDisposed && !device._graph.IsLost)
            {
                using RenderGraphEdit edit = device._graph.BeginEdit();
                edit.Remove(GraphResource);
                edit.Commit();
            }
            var views = Views.Values.ToArray();
            DescriptorTable? descriptor = SampledDescriptor;
            device.Retire(new TextureDescriptors(views, descriptor));
            Views.Clear();
        }
    }

    private sealed class TextureDescriptors(DeviceResource[] views, DescriptorTable? descriptor) : IDisposable
    {
        public void Dispose() { foreach (DeviceResource view in views) view.Dispose(); descriptor?.Dispose(); }
    }

    private readonly record struct TextureTileCopy(
        TextureCopyFootprint Footprint,
        ulong ChunkKey,
        uint MipLevel,
        uint ArrayLayer,
        TextureAspects Aspect,
        uint X,
        uint Y,
        uint Z,
        uint Width,
        uint Height);

    private readonly record struct TextureUploadPass(
        GraphBufferSlice Upload,
        GraphTextureId Destination,
        ulong BufferOffset,
        uint BufferRowPitch,
        uint BufferImageHeight,
        uint MipLevel,
        uint ArrayLayer,
        TextureAspects Aspect,
        uint X,
        uint Y,
        uint Z,
        uint Width,
        uint Height);

    private readonly record struct TextureShape(
        TextureDimension Dimension,
        uint Width,
        uint Height,
        uint Depth,
        uint MipLevelCount,
        uint ResourceArrayLayerCount,
        Format Format,
        Format SampledFormat,
        TextureViewDimension SampledDimension,
        TextureAspects Aspects,
        TextureUsages Usages)
    {
        internal TextureSubresourceRange FullRange =>
            new(0, MipLevelCount, 0, ResourceArrayLayerCount, Aspects);

        internal TextureSubresourceRange ResidentRange(uint firstMip) =>
            new(firstMip, MipLevelCount - firstMip, 0, ResourceArrayLayerCount, Aspects);

        internal uint ResolveArrayLayer(uint arrayLayer, uint face) =>
            SampledDimension is TextureViewDimension.Cube or TextureViewDimension.CubeArray
                ? checked((arrayLayer * 6) + face)
                : arrayLayer;

        internal static TextureShape From(EngineTexture asset, Device device)
        {
            bool cube = asset.SampledDimension is
                TextureViewDimension.Cube or TextureViewDimension.CubeArray;
            uint layers = cube
                ? checked(asset.ArrayLayerCount * 6)
                : asset.ArrayLayerCount;
            TextureAspects aspects = asset.Format switch
            {
                Format.D16UNorm or Format.D32Float => TextureAspects.Depth,
                Format.D24UNormS8UInt or Format.D32FloatS8UInt =>
                    TextureAspects.Depth | TextureAspects.Stencil,
                _ => TextureAspects.Color,
            };
            FormatFeatures features = device.Capabilities
                .GetFormatSupport(asset.Format)
                .Features;
            TextureUsages usages = TextureUsages.Sampled | TextureUsages.CopyDestination;
            if (features.HasFlag(FormatFeatures.Storage))
                usages |= TextureUsages.Storage;
            return new TextureShape(
                asset.Dimension,
                asset.Width,
                asset.Height,
                asset.Depth,
                asset.MipLevelCount,
                layers,
                asset.Format,
                asset.SampledFormat,
                asset.SampledDimension,
                aspects,
                usages);
        }
    }
}
