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
    private readonly HashSet<EngineTexture> _textures =
        new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<(RenderGraph.RenderGraph Graph, ulong Frame), TextureTransfers> _textureTransfers = [];
    private readonly Dictionary<(RenderGraph.RenderGraph Graph, ulong Frame), TextureFrame> _textureFrames = [];
    private TextureRealization? _missingTexture;

    public RhiTexture MissingTexture
    {
        get
        {
            ThrowIfDisposed();
            return RequireMissingTexture().Resource;
        }
    }

    public TextureSrv MissingTextureSrv
    {
        get
        {
            ThrowIfDisposed();
            TextureRealization realization = RequireMissingTexture();
            return GetTextureSrv(realization, realization.Shape.FullRange,
                realization.Shape.SampledFormat, realization.Shape.SampledDimension,
                "RenderDevice missing texture SRV");
        }
    }

    public RhiTexture GetTexture(EngineTexture asset)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(asset);
        return GetOrCreateTexture(asset).Resource;
    }

    public TextureSrv GetTextureSrv(
        EngineTexture asset,
        TextureSubresourceRange? range = null,
        Format? format = null,
        TextureViewDimension? dimension = null,
        string? label = null)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(asset);
        TextureRealization realization = GetOrCreateTexture(asset);
        return GetTextureSrv(realization, range, format, dimension, label);
    }

    private TextureSrv GetTextureSrv(TextureRealization realization, TextureSubresourceRange? range,
        Format? format, TextureViewDimension? dimension, string? label)
    {
        TextureShape shape = realization.Shape;
        TextureSubresourceRange resolved = range ?? shape.ResidentRange(realization.FirstResidentMip);
        ValidateResidentRange(realization, resolved);
        return GetTextureSrv(
            realization,
            resolved,
            format ?? shape.SampledFormat,
            dimension ?? shape.SampledDimension,
            label);
    }

    public TextureUav GetTextureUav(
        EngineTexture asset,
        in TextureSubresourceRange range,
        Format? format = null,
        TextureViewDimension? dimension = null,
        string? label = null)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(asset);
        TextureRealization realization = GetOrCreateTexture(asset);
        return GetTextureUav(realization, range, format, dimension, label);
    }

    private TextureUav GetTextureUav(TextureRealization realization, in TextureSubresourceRange range,
        Format? format = null, TextureViewDimension? dimension = null, string? label = null)
    {
        if (!realization.Resource.Info.Usages.HasFlag(TextureUsages.Storage))
            throw new NotSupportedException("The Texture format does not support storage views.");
        TextureShape shape = realization.Shape;
        Format resolvedFormat = format ?? shape.SampledFormat;
        TextureViewDimension resolvedDimension = dimension ?? shape.SampledDimension;
        var key = new TextureViewKey(
            TextureViewKind.Uav,
            range,
            resolvedFormat,
            resolvedDimension);
        if (realization.Views.TryGetValue(key, out DeviceResource? cached))
            return (TextureUav)cached;
        TextureUav view = _backend.CreateTextureUav(
            _device,
            new TextureUavDesc(
                realization.Resource,
                range,
                resolvedFormat,
                resolvedDimension,
                label));
        realization.Views.Add(key, view);
        return view;
    }

    /// <summary>
    /// Makes the currently resident Texture payload available to one RenderGraph frame and orders
    /// the supplied consumer after any one-shot upload passes. The caller still imports/gets the
    /// returned Graphics.Texture and declares the consumer's real RenderGraph access explicitly.
    /// This method performs no asset I/O.
    /// </summary>
    public RhiTexture PrepareTexture(
        ref RenderGraphFrame frame,
        GraphPassId consumer,
        EngineTexture asset) => PrepareTexture(ref frame, [consumer], asset);

    /// <summary>Prepares one texture for every pass bound to the parameter input.</summary>
    public RhiTexture PrepareTexture(ref RenderGraphFrame frame, GraphParameterInput input, EngineTexture asset) =>
        PrepareTexture(ref frame, [], asset, input).Resource;

    /// <summary>Prepares uploads for all passes bound to the input and returns the resident sampled view.</summary>
    public TextureSrv PrepareTextureSrv(ref RenderGraphFrame frame, GraphParameterInput input, EngineTexture asset)
    {
        TextureRealization realization = PrepareTexture(ref frame, [], asset, input);
        return GetTextureSrv(realization, null, null, null, null);
    }

    /// <summary>Prepares one texture once and orders all shader connections after its uploads.</summary>
    public TextureViews PrepareTextureForInputs(ref RenderGraphFrame frame, scoped ReadOnlySpan<GraphParameterInput> inputs, EngineTexture asset)
    {
        TextureRealization realization = PrepareTexture(ref frame, [], asset, null);
        TextureSrv? srv = null;
        TextureUav? uav = null;
        foreach (GraphParameterInput input in inputs)
        {
            if (realization.UploadPasses.Count != 0) OrderTextureConsumers(ref frame, realization, [], input);
            if (input.Type == ResourceBindingType.TextureSrv && srv is null)
                srv = GetTextureSrv(realization, null, null, null, null);
            if (input.Type == ResourceBindingType.TextureUav && uav is null)
                uav = GetTextureUav(realization, new TextureSubresourceRange(0, 1, 0,
                    realization.Resource.Info.ArrayLayerCount, TextureAspects.Color));
        }
        return new(realization.Resource, srv, uav);
    }

    public readonly record struct TextureViews(RhiTexture Texture, TextureSrv? Srv, TextureUav? Uav);

    /// <summary>Prepares the existing missing texture for every supplied shader connection.</summary>
    public TextureSrv PrepareMissingTextureForInputs(ref RenderGraphFrame frame, scoped ReadOnlySpan<GraphParameterInput> inputs)
    {
        PrepareMissingTexture(ref frame, [], null);
        TextureRealization realization = RequireMissingTexture();
        if (realization.UploadPasses.Count != 0)
            foreach (GraphParameterInput input in inputs) OrderTextureConsumers(ref frame, realization, [], input);
        return GetTextureSrv(realization, realization.Shape.FullRange,
            realization.Shape.SampledFormat, realization.Shape.SampledDimension, null);
    }

    /// <summary>Prepares one texture and orders every supplied consumer after its uploads.</summary>
    public RhiTexture PrepareTexture(
        ref RenderGraphFrame frame,
        scoped ReadOnlySpan<GraphPassId> consumers,
        EngineTexture asset) => PrepareTexture(ref frame, consumers, asset, null).Resource;

    private TextureRealization PrepareTexture(ref RenderGraphFrame frame,
        scoped ReadOnlySpan<GraphPassId> consumers, EngineTexture asset, GraphParameterInput? input)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(asset);
        EngineTexture content = asset.GetUploadContent(_device, out TextureRealization? realization);
        if ((realization is null || !realization.Initialized || realization.Revision != content.Revision) &&
            !content.HasBootstrap)
            throw new InvalidOperationException("A Texture must have a resident Bootstrap before rendering.");

        if (realization is null)
        {
            realization = CreateTexture(TextureShape.From(content, _device), content.Name);
            try
            {
                UploadCurrentResident(ref frame, content, realization, replaceContents: true);
                realization.Revision = content.Revision;
                realization.Initialized = true;
                if (!ReferenceEquals(content, asset)) realization.ReplacedAsset = asset;
                asset.SetDeviceData(_device, realization);
                _textures.Add(asset);
                realization.TextureObject = asset;
            }
            catch
            {
                AbortPendingUpload(realization);
                realization.Dispose();
                throw;
            }
        }
        else if (!realization.Initialized && ReferenceEquals(content, asset))
        {
            UploadCurrentResident(ref frame, content, realization, replaceContents: true);
            realization.Revision = content.Revision;
            realization.Initialized = true;
        }
        else if (realization.Revision != content.Revision)
        {
            TextureShape nextShape = TextureShape.From(content, _device);
            {
                TextureRealization replacement = CreateTexture(nextShape, content.Name);
                try
                {
                    UploadCurrentResident(ref frame, content, replacement, replaceContents: true);
                    replacement.Revision = content.Revision;
                    replacement.Initialized = true;
                }
                catch (Exception error)
                {
                    AbortPendingUpload(replacement);
                    replacement.Dispose();
                    asset.FailDeviceUpdate(content, error);
                    throw;
                }
                replacement.ReplacedAsset = asset;
                replacement.ReplacedRealization = realization;
                asset.SetDeviceData(_device, replacement);
                replacement.TextureObject = asset;
                realization = replacement;
            }
        }
        else
        {
            UploadCurrentResident(ref frame, content, realization, replaceContents: false);
        }

        EnsureImported(ref frame, realization);
        OrderTextureConsumers(ref frame, realization, consumers, input);
        return realization;
    }

    public RhiTexture PrepareMissingTexture(
        ref RenderGraphFrame frame,
        GraphPassId consumer) => PrepareMissingTexture(ref frame, [consumer]);

    /// <summary>Prepares the fallback texture for every pass bound to the parameter input.</summary>
    public RhiTexture PrepareMissingTexture(ref RenderGraphFrame frame, GraphParameterInput input) =>
        PrepareMissingTexture(ref frame, [], input);

    /// <summary>Prepares the fallback texture and orders every supplied consumer after its upload.</summary>
    public RhiTexture PrepareMissingTexture(
        ref RenderGraphFrame frame,
        scoped ReadOnlySpan<GraphPassId> consumers) => PrepareMissingTexture(ref frame, consumers, null);

    private RhiTexture PrepareMissingTexture(ref RenderGraphFrame frame,
        scoped ReadOnlySpan<GraphPassId> consumers, GraphParameterInput? input)
    {
        ThrowIfDisposed();
        TextureRealization realization = RequireMissingTexture();
        EnsureNoOtherUploadFrame(ref frame, realization);
        EnsureImported(ref frame, realization);
        if (!realization.Initialized)
        {
            TextureMipTile tile = MissingTile();
            BeginUpload(ref frame, asset: null, realization, replaceContents: true);
            try
            {
                ScheduleUpload(ref frame, realization, tile,
                    new ReadOnlySequence<byte>(new byte[] { 255, 0, 255, 255 }));
            }
            catch
            {
                AbortPendingUpload(realization);
                throw;
            }
            realization.Initialized = true;
            realization.FirstResidentMip = 0;
        }
        OrderTextureConsumers(ref frame, realization, consumers, input);
        return realization.Resource;
    }

    private void OrderTextureConsumers(ref RenderGraphFrame frame,
        TextureRealization realization, scoped ReadOnlySpan<GraphPassId> consumers, GraphParameterInput? input)
    {
        if (realization.UploadPasses.Count == 0) return;
        EnsureFinalizationPass(ref frame, realization);
        foreach (GraphPassId upload in realization.UploadPasses)
        {
            foreach (GraphPassId consumer in consumers) frame.OrderAfter(consumer, upload);
            if (input is not null) frame.OrderAfter(input, upload);
            frame.OrderAfter(realization.FinalizationPass, upload);
        }
        foreach (GraphPassId consumer in consumers) frame.OrderAfter(realization.FinalizationPass, consumer);
        if (input is not null) frame.OrderAfter(realization.FinalizationPass, input);
    }

    private void CreateMissingTexture()
    {
        var shape = new TextureShape(
            TextureDimension.Texture2D,
            1,
            1,
            1,
            1,
            1,
            Format.R8G8B8A8UNorm,
            Format.R8G8B8A8UNorm,
            TextureViewDimension.Texture2D,
            TextureAspects.Color,
            TextureUsages.Sampled | TextureUsages.CopyDestination);
        _missingTexture = CreateTexture(shape, "RenderDevice missing texture");
    }

    private TextureRealization RequireMissingTexture()
    {
        ThrowIfDisposed();
        if (_missingTexture is null) CreateMissingTexture();
        return _missingTexture!;
    }

    private TextureRealization GetOrCreateTexture(EngineTexture asset)
    {
        if (asset.TryGetDeviceData(_device, out TextureRealization? realization))
            return realization;
        realization = CreateTexture(TextureShape.From(asset, _device), asset.Name);
        asset.SetDeviceData(_device, realization);
                _textures.Add(asset);
                realization.TextureObject = asset;
        return realization;
    }

    private TextureRealization CreateTexture(in TextureShape shape, string? label)
    {
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
            permittedFormats,
            label ?? "RenderDevice Texture realization");
        return new TextureRealization(this, shape, _backend.CreateTexture(_device, description));
    }

    private TextureSrv GetTextureSrv(
        TextureRealization realization,
        in TextureSubresourceRange range,
        Format format,
        TextureViewDimension dimension,
        string? label)
    {
        var key = new TextureViewKey(TextureViewKind.Srv, range, format, dimension);
        if (realization.Views.TryGetValue(key, out DeviceResource? cached))
            return (TextureSrv)cached;
        TextureSrv view = _backend.CreateTextureSrv(
            _device,
            new TextureSrvDesc(realization.Resource, range, format, dimension, label));
        realization.Views.Add(key, view);
        return view;
    }

    private void UploadCurrentResident(
        ref RenderGraphFrame frame,
        EngineTexture asset,
        TextureRealization realization,
        bool replaceContents)
    {
        EnsureNoOtherUploadFrame(ref frame, realization);
        EnsureImported(ref frame, realization);
        // Fully uploaded content cannot acquire another tile without a new content
        // revision. Keep frame synchronization, but skip repeated pixel discovery.
        if (!replaceContents && realization.UploadedChunks.Count == asset.MipTiles.Count)
            return;
        bool started = false;
        try
        {
            foreach (TextureMipTile tile in asset.MipTiles ?? [])
            {
                if (!replaceContents && realization.ContainsUploadedChunk(tile.ChunkKey)) continue;
                if (!asset.TryGetTile(tile.ChunkKey, out ReadOnlyMemory<byte> pixels)) continue;
                if (!started)
                {
                    BeginUpload(ref frame, asset, realization, replaceContents);
                    started = true;
                }
                ScheduleUpload(ref frame, realization, tile, new ReadOnlySequence<byte>(pixels));
            }
        }
        catch
        {
            if (started) AbortPendingUpload(realization);
            throw;
        }
        if (replaceContents && !started)
            throw new InvalidOperationException("A replacement Texture has no resident Bootstrap payload.");
        UpdateResidentMip(asset, realization);
        if (realization.FirstResidentMip > asset.BootstrapMipLevel)
            throw new InvalidOperationException("The resident Texture payload does not contain its Bootstrap mip.");
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

    private void EnsureImported(ref RenderGraphFrame frame, TextureRealization realization)
    {
        if (ReferenceEquals(realization.FrameGraph, frame.Owner) &&
            realization.FrameIdentity == frame.FrameIdentity)
            return;
        realization.AddFrame();
        var frameKey = (frame.Owner, frame.FrameIdentity);
        if (!_textureFrames.TryGetValue(frameKey, out TextureFrame? textures))
        {
            textures = new TextureFrame(this, frameKey);
            _textureFrames.Add(frameKey, textures);
            frame.RetireAfterFrame(textures);
            frame.ObserveExecution(textures);
        }
        textures.Textures.Add(realization);
        realization.FrameGraph = frame.Owner;
        realization.FrameIdentity = frame.FrameIdentity;
        realization.UploadPasses.Clear();
        realization.FinalizationScheduled = false;
        realization.FinalizationPass = default;
        GraphTextureState? states = frame.GetTextureState(realization.Resource);
        realization.GraphResource = states is not null && ReferenceEquals(states, realization.GraphStates)
            ? states.Texture : frame.Import(realization.Resource, CreateBoundaryStates(realization));
        realization.GraphStates = frame.GetTextureState(realization.Resource);
    }

    private void EnsureFinalizationPass(
        ref RenderGraphFrame frame,
        TextureRealization realization)
    {
        if (realization.FinalizationScheduled) return;
        if (!realization.Initialized ||
            realization.FirstResidentMip >= realization.Shape.MipLevelCount)
        {
            throw new InvalidOperationException(
                "A RenderDevice Texture cannot be finalized before resident contents exist.");
        }

        GraphPassId completion = GetTextureTransfers(ref frame).CompletionPass;
        PassDefinition access = frame.GetPass(completion);
        _ = access.Read(realization.GraphResource,
            realization.Shape.ResidentRange(realization.FirstResidentMip),
            PipelineSync.AllShading, ResourceAccess.ShaderResource, TextureLayout.ShaderResource);
        realization.FinalizationPass = completion;
        realization.FinalizationScheduled = true;
    }

    private ReadOnlySpan<TextureBoundaryState> CreateBoundaryStates(TextureRealization realization)
    {
        if (realization.GraphStates is not null) return realization.GraphStates.States;
        if (realization.BoundaryStates.Length != 0) return realization.BoundaryStates;
        if (!realization.Initialized)
        {
            return new TextureBoundaryState[]
            {
                new TextureBoundaryState(
                    realization.Shape.FullRange,
                    realization.Resource.InitialSync,
                    realization.Resource.InitialAccess,
                    realization.Resource.InitialLayout,
                    ResourceContentState.Undefined),
            };
        }

        Queue graphicsQueue = _backend.GetQueue(_device, QueueType.Graphics);
        QueueCompletion? readyAfter = null;
        foreach (QueueCompletion completion in CaptureSubmittedCompletions())
        {
            if (ReferenceEquals(completion.Queue, graphicsQueue))
            {
                readyAfter = completion;
                break;
            }
        }
        return new TextureBoundaryState[]
        {
            new TextureBoundaryState(
                realization.Shape.ResidentRange(realization.FirstResidentMip),
                PipelineSync.AllShading,
                ResourceAccess.ShaderResource,
                TextureLayout.ShaderResource,
                ResourceContentState.Defined,
                graphicsQueue,
                readyAfter),
        };
    }

    private TextureTransfers GetTextureTransfers(ref RenderGraphFrame frame)
    {
        var frameKey = (frame.Owner, frame.FrameIdentity);
        if (_textureTransfers.TryGetValue(frameKey, out TextureTransfers? transfers)) return transfers;
        transfers = new TextureTransfers();
        transfers.CopyPass = frame.AddCopyPass(
            "Upload RenderDevice textures",
            PassQueueSelection.AnyOfType(QueueType.Graphics),
            transfers.Tiles,
            new PassOptions(Culling: PassCullingMode.NeverCull),
            static (ref PassDefinition _, ref List<TextureUploadPass> _) => { },
            static (ref CopyPassCommandScope commands, in List<TextureUploadPass> tiles) =>
            {
                foreach (TextureUploadPass tile in tiles)
                    commands.CopyBufferToTexture(new BufferTextureCopy(
                        commands.GetBuffer(tile.Upload.Buffer), tile.BufferOffset,
                        tile.BufferRowPitch, tile.BufferImageHeight,
                        commands.GetTexture(tile.Destination), tile.MipLevel, tile.ArrayLayer,
                        tile.Aspect, tile.X, tile.Y, tile.Z, tile.Width, tile.Height, 1));
            });
        transfers.CompletionPass = frame.AddGeneralPass(
            "Complete RenderDevice texture uploads",
            PassQueueSelection.AnyOfType(QueueType.Graphics),
            0,
            new PassOptions(Culling: PassCullingMode.NeverCull),
            static (ref PassDefinition _, ref int _) => { },
            static (ref GeneralPassCommandScope _, in int _) => { });
        _textureTransfers.Add(frameKey, transfers);
        return transfers;
    }

    private void ScheduleUpload(
        ref RenderGraphFrame frame,
        TextureRealization realization,
        TextureMipTile tile,
        ReadOnlySequence<byte> pixels)
    {
        TextureTileCopy copy = DescribeTextureTileCopy(realization.Resource, realization.Shape, tile);
        ulong graphValidatedSize = checked(copy.Footprint.Offset +
            ((ulong)Math.Max(copy.Footprint.RowPitch, 1u) * Math.Max(copy.Height, 1u)));
        int byteCount = checked((int)Math.Max(copy.Footprint.TotalSize, graphValidatedSize));
        GraphBufferSlice upload = frame.Upload(byteCount, BufferUsages.CopySource,
            (pixels, tile, copy.Footprint), CopyTextureTile, "Texture mip tile upload");
        TextureTransfers transfers = GetTextureTransfers(ref frame);
        PassDefinition access = frame.GetPass(transfers.CopyPass);
        var state = new TextureUploadPass(
            upload,
            realization.GraphResource,
            checked(upload.Range.Offset + copy.Footprint.Offset),
            copy.Footprint.RowPitch,
            copy.Height,
            copy.MipLevel,
            copy.ArrayLayer,
            copy.Aspect,
            copy.X,
            copy.Y,
            copy.Z,
            copy.Width,
            copy.Height);
        transfers.Tiles.Add(state);
        access.Read(upload.Buffer, upload.Range, PipelineSync.Copy, ResourceAccess.CopySource);
        access.Write(realization.GraphResource,
            new TextureSubresourceRange(copy.MipLevel, 1, copy.ArrayLayer, 1, copy.Aspect),
            PipelineSync.Copy, ResourceAccess.CopyDestination, TextureLayout.CopyDestination,
            WriteCoverage.Partial, ResourceContentState.Defined);
        realization.PendingUploadedChunks.Add(copy.ChunkKey);
        if (!realization.UploadPasses.Contains(transfers.CopyPass))
            realization.UploadPasses.Add(transfers.CopyPass);
    }

    private static void UpdateResidentMip(EngineTexture asset, TextureRealization realization)
    {
        uint first = asset.MipLevelCount;
        for (uint mip = asset.MipLevelCount; mip > 0; mip--)
        {
            uint candidate = mip - 1;
            bool hasTiles = false;
            bool complete = true;
            foreach (TextureMipTile tile in asset.MipTiles ?? [])
            {
                if (tile.MipLevel != candidate)
                    continue;

                hasTiles = true;
                if (!realization.ContainsUploadedChunk(tile.ChunkKey))
                {
                    complete = false;
                    break;
                }
            }
            if (!hasTiles || !complete)
                break;
            first = candidate;
        }
        realization.FirstResidentMip = first;
    }

    private static void ValidateResidentRange(
        TextureRealization realization,
        in TextureSubresourceRange range)
    {
        if (!realization.Initialized)
            throw new InvalidOperationException("The Texture realization has not uploaded Bootstrap data.");
        if (range.FirstMipLevel < realization.FirstResidentMip ||
            range.MipLevelCount == 0 ||
            range.FirstMipLevel >= realization.Shape.MipLevelCount ||
            range.MipLevelCount > realization.Shape.MipLevelCount - range.FirstMipLevel)
        {
            throw new ArgumentOutOfRangeException(nameof(range));
        }
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

    /// <summary>Releases only this texture's allocation on this device after submitted work completes.</summary>
    public void ReleaseTexture(EngineTexture texture)
    {
        if (texture.RemoveDeviceData(_device) is TextureRealization data)
        {
            ConfirmTextureUpdate(data);
            ReleaseTexture(data);
        }
    }

    private void ReleaseTexture(TextureRealization data)
    {
        ConfirmTextureUpdate(data);
        if (data.TextureObject is { } texture)
            _textures.Remove(texture);
        if (data.PendingFrameGraph is not null)
        {
            data.ReleaseAfterUpload = true;
            return;
        }
        data.RequestRelease();
    }

    private void DisposeTextures()
    {
        foreach (EngineTexture texture in _textures.ToArray())
            if (texture.RemoveDeviceData(_device) is TextureRealization data)
            {
                ConfirmTextureUpdate(data);
                data.Dispose();
            }
        _textures.Clear();
        _textureTransfers.Clear();
        _missingTexture?.Dispose();
        _missingTexture = null;
    }

    private static void BeginUpload(
        ref RenderGraphFrame frame,
        EngineTexture? asset,
        TextureRealization realization,
        bool replaceContents)
    {
        if (ReferenceEquals(realization.PendingFrameGraph, frame.Owner) &&
            realization.PendingFrameIdentity == frame.FrameIdentity)
        {
            if (replaceContents != realization.ReplacesContents ||
                !ReferenceEquals(asset, realization.PendingAsset))
            {
                throw new InvalidOperationException(
                    "A Texture realization cannot mix upload transactions in one frame.");
            }
            return;
        }
        if (realization.PendingFrameGraph is not null)
        {
            throw new InvalidOperationException(
                "A Texture realization already belongs to another active frame.");
        }

        realization.PendingFrameGraph = frame.Owner;
        realization.PendingFrameIdentity = frame.FrameIdentity;
        realization.PendingAsset = asset;
        realization.ReplacesContents = replaceContents;
        realization.RollbackInitialized = realization.Initialized;
        realization.RollbackFirstResidentMip = realization.FirstResidentMip;
        realization.RollbackRevision = realization.Revision;
        realization.PendingUploadedChunks.Clear();
        if (replaceContents)
        {
            realization.RollbackUploadedChunks = [.. realization.UploadedChunks];
            realization.UploadedChunks.Clear();
        }
    }

    private static void EnsureNoOtherUploadFrame(
        ref RenderGraphFrame frame,
        TextureRealization realization)
    {
        if (realization.PendingFrameGraph is null ||
            (ReferenceEquals(realization.PendingFrameGraph, frame.Owner) &&
             realization.PendingFrameIdentity == frame.FrameIdentity))
        {
            return;
        }
        throw new InvalidOperationException(
            "A Texture upload is still being authored by another RenderGraph frame.");
    }

    private static void AbortPendingUpload(TextureRealization realization)
    {
        if (realization.PendingFrameGraph is not null)
            realization.FinishUpload(executed: false);
    }

    private void FinishUpload(TextureRealization realization, bool executed, bool wroteContents = false)
    {
        if (realization.PendingFrameGraph is null)
            return;

        EngineTexture? asset = realization.PendingAsset;
        TextureRealization? replaced = realization.ReplacedRealization;
        EngineTexture? replacedAsset = realization.ReplacedAsset;
        if (executed)
        {
            realization.UploadedChunks.UnionWith(realization.PendingUploadedChunks);
            if (asset is not null)
                UpdateResidentMip(asset, realization);
        }
        else
        {
            realization.Initialized = realization.RollbackInitialized;
            realization.FirstResidentMip = realization.RollbackFirstResidentMip;
            realization.Revision = realization.RollbackRevision;
            if (realization.RollbackUploadedChunks is { } uploaded)
            {
                realization.UploadedChunks.Clear();
                realization.UploadedChunks.UnionWith(uploaded);
            }
        }

        if (!executed && wroteContents)
        {
            realization.Initialized = false;
            realization.FirstResidentMip = realization.Shape.MipLevelCount;
            realization.UploadedChunks.Clear();
        }
        realization.ClearUploadTransaction();
        if (realization.ReleaseAfterUpload)
        {
            if (replaced is not null) replaced.RequestRelease();
            realization.RequestRelease();
            return;
        }
        if (replacedAsset is null)
            return;

        if (executed && asset is not null && !ReferenceEquals(asset, replacedAsset))
        {
            replacedAsset.CompleteDeviceUpdate(_device, asset);
            return;
        }
        if (executed)
        {
            ConfirmTextureUpdate(realization);
            return;
        }
        if (asset is not null && !ReferenceEquals(asset, replacedAsset))
            replacedAsset.FailDeviceUpdate(asset, new OperationCanceledException("Texture upload frame did not complete."));
        else
            CancelTextureUpdate(realization);
    }

    private void ConfirmTextureUpdate(TextureRealization data)
    {
        TextureRealization? previous = data.ReplacedRealization;
        data.ReplacedRealization = null;
        data.ReplacedAsset = null;
        if (previous is not null) previous.RequestRelease();
    }

    private void FinishTextureFrame(TextureRealization realization, RenderGraphFrameResult result)
    {
        try
        {
            ReadOnlySpan<TextureBoundaryState> states = result.GetTextureStates(realization.GraphResource);
            if (realization.GraphStates is null && !states.IsEmpty) realization.BoundaryStates = states.ToArray();
            bool wroteContents = false;
            foreach (GraphPassId pass in realization.UploadPasses)
                wroteContents |= result.TryGetPassCompletion(pass, out _);
            FinishUpload(realization, realization.FinalizationScheduled &&
                result.TryGetPassCompletion(realization.FinalizationPass, out _), wroteContents);
        }
        finally
        {
            realization.FrameGraph = null;
            realization.FrameIdentity = 0;
            realization.GraphResource = default;
        }
    }

    private void CancelTextureUpdate(TextureRealization data)
    {
        EngineTexture? texture = data.ReplacedAsset;
        TextureRealization? previous = data.ReplacedRealization;
        if (texture is null) return;
        if (previous is not null)
        {
            if (!texture.IsDisposed) texture.SetDeviceData(_device, previous);
            else previous.RequestRelease();
        }
        else
        {
            texture.RemoveDeviceData(_device);
            _textures.Remove(texture);
        }
        data.ReplacedRealization = null;
        data.ReplacedAsset = null;
        if (data.PendingFrameGraph is not null) data.ReleaseAfterUpload = true;
        else data.RequestRelease();
    }

    private sealed class TextureFrame(RenderDevice renderDevice, (RenderGraph.RenderGraph Graph, ulong Frame) key)
        : IDisposable, IRenderGraphFrameExecutionObserver
    {
        internal readonly List<TextureRealization> Textures = [];
        private bool _released;
        public void OnRenderGraphFrameFinished(RenderGraphFrameResult result)
        {
            List<Exception>? errors = null;
            try
            {
                if (!result.SubmittedCompletions.IsEmpty)
                    try { renderDevice.TrackSubmission(result.SubmittedCompletions); }
                    catch (Exception error) { (errors ??= []).Add(error); }
                foreach (TextureRealization texture in Textures)
                    try { renderDevice.FinishTextureFrame(texture, result); }
                    catch (Exception error) { (errors ??= []).Add(error); }
            }
            finally
            {
                renderDevice._textureFrames.Remove(key);
                renderDevice._textureTransfers.Remove(key);
            }
            if (errors is not null) throw new AggregateException("Texture frame completion failed.", errors);
        }
        public void Dispose()
        {
            if (_released) return;
            _released = true;
            foreach (TextureRealization texture in Textures) texture.CompleteFrame();
            Textures.Clear();
        }
    }

    private sealed class TextureTransfers
    {
        internal readonly List<TextureUploadPass> Tiles = [];
        internal GraphPassId CopyPass;
        internal GraphPassId CompletionPass;
    }

    private enum TextureViewKind : byte
    {
        Srv,
        Uav,
    }

    private readonly record struct TextureViewKey(
        TextureViewKind Kind,
        TextureSubresourceRange Range,
        Format Format,
        TextureViewDimension Dimension);

    private sealed class TextureRealization : IDisposable, ITextureDeviceData
    {
        private readonly RenderDevice _owner;

        internal TextureRealization(RenderDevice owner, TextureShape shape, RhiTexture resource)
        {
            _owner = owner;
            Shape = shape;
            Resource = resource;
            FirstResidentMip = shape.MipLevelCount;
        }

        internal EngineTexture? TextureObject { get; set; }
        internal TextureBoundaryState[] BoundaryStates { get; set; } = [];
        internal GraphTextureState? GraphStates { get; set; }
        SomeEngine.Graphics.Texture ITextureDeviceData.Texture => Resource;
        uint ITextureDeviceData.FirstAvailableMip => FirstResidentMip;
        void ITextureDeviceData.Release() => _owner.ReleaseTexture(this);
        void ITextureDeviceData.ConfirmUpdate() => _owner.ConfirmTextureUpdate(this);
        void ITextureDeviceData.CancelUpdate() => _owner.CancelTextureUpdate(this);
        private readonly object _framesGate = new();
        private int _activeFrames;
        private bool _releaseRequested;
        private bool _retirementQueued;
        internal void AddFrame() { lock (_framesGate) _activeFrames++; }
        internal void CompleteFrame()
        {
            lock (_framesGate)
            {
                _activeFrames--;
                QueueRelease();
            }
        }
        internal void RequestRelease()
        {
            lock (_framesGate)
            {
                _releaseRequested = true;
                QueueRelease();
            }
        }
        private void QueueRelease()
        {
            if (!_releaseRequested || _activeFrames != 0 || _retirementQueued) return;
            _retirementQueued = true;
            _owner.Retire(this);
        }
        private bool _released;

        internal TextureShape Shape { get; }
        internal RhiTexture Resource { get; }
        internal Dictionary<TextureViewKey, DeviceResource> Views { get; } = [];
        internal ulong Revision { get; set; }
        internal bool Initialized { get; set; }
        internal bool ReleaseAfterUpload { get; set; }
        internal uint FirstResidentMip { get; set; }
        internal ulong FrameIdentity { get; set; }
        internal SomeEngine.RenderGraph.RenderGraph? FrameGraph { get; set; }
        internal GraphTextureId GraphResource { get; set; }
        internal GraphPassId FinalizationPass { get; set; }
        internal bool FinalizationScheduled { get; set; }
        internal List<GraphPassId> UploadPasses { get; } = [];
        internal HashSet<ulong> UploadedChunks { get; } = [];
        internal HashSet<ulong> PendingUploadedChunks { get; } = [];
        internal SomeEngine.RenderGraph.RenderGraph? PendingFrameGraph { get; set; }
        internal ulong PendingFrameIdentity { get; set; }
        internal EngineTexture? PendingAsset { get; set; }
        internal bool ReplacesContents { get; set; }
        internal bool RollbackInitialized { get; set; }
        internal uint RollbackFirstResidentMip { get; set; }
        internal ulong RollbackRevision { get; set; }
        internal HashSet<ulong>? RollbackUploadedChunks { get; set; }
        internal EngineTexture? ReplacedAsset { get; set; }
        internal TextureRealization? ReplacedRealization { get; set; }

        internal bool ContainsUploadedChunk(ulong chunk) =>
            UploadedChunks.Contains(chunk) || PendingUploadedChunks.Contains(chunk);

        internal void FinishUpload(bool executed) => _owner.FinishUpload(this, executed);

        internal void ClearUploadTransaction()
        {
            PendingFrameGraph = null;
            PendingFrameIdentity = 0;
            PendingAsset = null;
            ReplacesContents = false;
            PendingUploadedChunks.Clear();
            RollbackUploadedChunks = null;
        }

        public void Dispose()
        {
            if (_released) return;
            _released = true;
            foreach (DeviceResource view in Views.Values)
                view.Dispose();
            Views.Clear();
            Resource.Dispose();
        }
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
