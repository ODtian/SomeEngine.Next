using SomeEngine.Assets.Schema;
using SomeEngine.Graphics;
using SomeEngine.RenderGraph;
using SomeEngine.Serialization.Streaming;
using EngineTexture = SomeEngine.Assets.Schema.Texture;
using RhiTexture = SomeEngine.Graphics.Texture;
using TextureDimension = SomeEngine.Graphics.TextureDimension;
using TextureViewDimension = SomeEngine.Graphics.TextureViewDimension;

namespace SomeEngine.Render;

internal sealed class TextureDeviceRealizations : IDisposable
{
    private readonly IGraphicsBackend _backend;
    private readonly Device _device;
    private readonly Dictionary<EngineTexture, Realization> _realizations =
        new(ReferenceEqualityComparer.Instance);
    private bool _disposed;

    internal TextureDeviceRealizations(IGraphicsBackend backend, Device device)
    {
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        _device = device ?? throw new ArgumentNullException(nameof(device));
    }

    internal TextureUse Use(
        ref RenderGraphFrame frame,
        ref PassDefinition pass,
        EngineTexture asset,
        PipelineSync sync)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(asset);

        if (!_realizations.TryGetValue(asset, out Realization? realization))
        {
            realization = Create(asset);
            try
            {
                PrepareMipZero(asset, realization.Resource!, realization.Shape, out PreparedTile[] tiles);
                ScheduleUpload(ref frame, pass.Id, realization, tiles);
                realization.Revision = asset.Revision;
                realization.Initialized = true;
                _realizations.Add(asset, realization);
            }
            catch
            {
                realization.Dispose();
                throw;
            }
        }
        else if (realization.Revision != asset.Revision)
        {
            TextureShape nextShape = TextureShape.From(asset);
            if (realization.Shape == nextShape)
            {
                PrepareMipZero(asset, realization.Resource!, nextShape, out PreparedTile[] tiles);
                realization.UploadedChunks.Clear();
                ScheduleUpload(ref frame, pass.Id, realization, tiles);
                realization.Revision = asset.Revision;
                realization.Initialized = true;
            }
            else
            {
                Realization replacement = Create(asset);
                try
                {
                    PrepareMipZero(asset, replacement.Resource!, replacement.Shape, out PreparedTile[] tiles);
                    ScheduleUpload(ref frame, pass.Id, replacement, tiles);
                    replacement.Revision = asset.Revision;
                    replacement.Initialized = true;
                }
                catch
                {
                    replacement.Dispose();
                    throw;
                }

                _realizations[asset] = replacement;
                frame.RetireAfterSubmittedFrames(
                    new DisposeGroup(realization.Resource, realization.View));
                realization.Detach();
                realization = replacement;
            }
        }


        PrepareResidentTiles(asset, realization, out PreparedTile[] residentTiles);
        if (residentTiles.Length != 0)
            ScheduleUpload(ref frame, pass.Id, realization, residentTiles);

        EnsureImported(ref frame, realization);
        foreach (GraphPassId upload in realization.UploadPasses)
            frame.OrderAfter(pass.Id, upload);
        _ = pass.Read(realization.GraphView, sync, TextureLayout.ShaderResource);
        return new TextureUse(realization.View!, realization.Shape.Descriptor);
    }

    private Realization Create(EngineTexture asset)
    {
        TextureShape shape = TextureShape.From(asset);
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
            TextureUsages.Sampled | TextureUsages.CopyDestination,
            permittedFormats,
            asset.Name ?? "Engine texture realization");
        RhiTexture resource = _backend.CreateTexture(_device, description);
        try
        {
            TextureSrv view = _backend.CreateTextureSrv(
                _device,
                new TextureSrvDesc(
                    resource,
                    shape.FullRange,
                    shape.SampledFormat,
                    shape.SampledDimension,
                    asset.Name is null ? "Engine texture SRV" : asset.Name + " SRV"));
            return new Realization(shape, resource, view);
        }
        catch
        {
            resource.Dispose();
            throw;
        }
    }

    private void PrepareMipZero(
        EngineTexture asset,
        RhiTexture destination,
        in TextureShape shape,
        out PreparedTile[] prepared)
    {
        TextureMipTile[] tiles = (asset.MipTiles ?? [])
            .Where(static tile => tile.MipLevel == 0)
            .ToArray();
        if (tiles.Length == 0)
            throw new InvalidDataException("A texture realization requires mip 0 payload tiles.");

        prepared = new PreparedTile[tiles.Length];
        for (int index = 0; index < tiles.Length; index++)
        {
            TextureMipTile tile = tiles[index];
            using ResidentChunkLease lease = asset.AcquireMipTileAsync(
                    tile.MipLevel,
                    tile.ArrayLayer,
                    tile.Face,
                    tile.DepthSlice,
                    tile.TileX,
                    tile.TileY)
                .AsTask()
                .GetAwaiter()
                .GetResult();
            prepared[index] = PrepareTile(destination, shape, tile, lease.Memory.Span);
        }
    }

    private void PrepareResidentTiles(
        EngineTexture asset,
        Realization realization,
        out PreparedTile[] prepared)
    {
        var result = new List<PreparedTile>();
        foreach (TextureMipTile tile in asset.MipTiles ?? [])
        {
            if (realization.UploadedChunks.Contains(tile.ChunkKey))
                continue;
            if (!asset.TryAcquireResidentMipTile(
                    tile.MipLevel,
                    tile.ArrayLayer,
                    tile.Face,
                    tile.DepthSlice,
                    tile.TileX,
                    tile.TileY,
                    out ResidentChunkLease lease))
            {
                continue;
            }
            using (lease)
                result.Add(PrepareTile(
                    realization.Resource!,
                    realization.Shape,
                    tile,
                    lease.Memory.Span));
        }
        prepared = result.ToArray();
    }

    private PreparedTile PrepareTile(
        RhiTexture destination,
        in TextureShape shape,
        TextureMipTile tile,
        ReadOnlySpan<byte> source)
    {
        uint arrayLayer = shape.ResolveArrayLayer(tile.ArrayLayer, tile.Face);
        uint z = shape.Dimension == TextureDimension.Texture3D ? tile.DepthSlice : 0;
        TextureAspects copyAspect = SelectCopyAspect(shape.Aspects);
        var copy = new BufferTextureCopy(
            null!,
            0,
            0,
            0,
            destination,
            tile.MipLevel,
            arrayLayer,
            copyAspect,
            tile.TileX,
            tile.TileY,
            z,
            tile.Width,
            tile.Height,
            1);
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
            TextureUsages.Sampled | TextureUsages.CopyDestination,
            permittedFormats);
        TextureCopyFootprint footprint = _backend.GetTextureCopyFootprint(
            _device,
            description,
            copy,
            0);
        return new PreparedTile(
            Repack(source, tile, footprint),
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

    private static byte[] Repack(
        ReadOnlySpan<byte> source,
        TextureMipTile tile,
        in TextureCopyFootprint footprint)
    {
        ulong sourceRowPitch = tile.RowPitch == 0 ? footprint.RowSize : tile.RowPitch;
        if (sourceRowPitch < footprint.RowSize)
            throw new InvalidDataException("A texture tile row pitch is smaller than the RHI copy row size.");
        ulong requiredSource = checked(
            footprint.RowCount == 0
                ? 0
                : ((ulong)(footprint.RowCount - 1) * sourceRowPitch) + footprint.RowSize);
        if (requiredSource > checked((ulong)source.Length))
            throw new InvalidDataException("A texture tile payload is shorter than its declared storage layout.");

        byte[] result = new byte[checked((int)footprint.TotalSize)];
        for (uint row = 0; row < footprint.RowCount; row++)
        {
            int sourceOffset = checked((int)((ulong)row * sourceRowPitch));
            int destinationOffset = checked((int)(
                footprint.Offset + ((ulong)row * footprint.RowPitch)));
            source.Slice(sourceOffset, checked((int)footprint.RowSize)).CopyTo(
                result.AsSpan(destinationOffset, checked((int)footprint.RowSize)));
        }
        return result;
    }

    private static TextureAspects SelectCopyAspect(TextureAspects aspects)
    {
        if ((aspects & TextureAspects.Color) != 0)
            return TextureAspects.Color;
        if ((aspects & TextureAspects.Depth) != 0)
            return TextureAspects.Depth;
        if ((aspects & TextureAspects.Stencil) != 0)
            return TextureAspects.Stencil;
        throw new InvalidDataException("A texture has no copyable aspect.");
    }

    private static void EnsureImported(ref RenderGraphFrame frame, Realization realization)
    {
        if (realization.FrameIdentity == frame.FrameIdentity)
            return;

        realization.FrameIdentity = frame.FrameIdentity;
        realization.UploadPasses.Clear();
        TextureBoundaryState boundary = realization.Initialized
            ? new TextureBoundaryState(
                realization.Shape.FullRange,
                PipelineSync.AllShading,
                ResourceAccess.ShaderResource,
                TextureLayout.ShaderResource,
                ResourceContentState.Defined)
            : new TextureBoundaryState(
                realization.Shape.FullRange,
                realization.Resource!.InitialSync,
                realization.Resource.InitialAccess,
                realization.Resource.InitialLayout,
                ResourceContentState.Undefined);
        realization.GraphResource = frame.Import(realization.Resource!, [boundary]);
        realization.GraphView = frame.CreateTextureSrv(
            realization.GraphResource,
            realization.Shape.FullRange,
            realization.Shape.SampledFormat,
            realization.Shape.SampledDimension,
            "Engine texture realization SRV");
    }

    private static void ScheduleUpload(
        ref RenderGraphFrame frame,
        GraphPassId consumer,
        Realization realization,
        ReadOnlySpan<PreparedTile> tiles)
    {
        EnsureImported(ref frame, realization);
        foreach (ref readonly PreparedTile tile in tiles)
        {
            GraphBufferId upload = frame.Upload(
                tile.Bytes,
                BufferUsages.CopySource,
                "Texture mip tile upload");
            var state = new TextureUploadPass(
                upload,
                realization.GraphResource,
                tile.Footprint.Offset,
                tile.Footprint.RowPitch,
                tile.Footprint.RowCount,
                tile.MipLevel,
                tile.ArrayLayer,
                tile.Aspect,
                tile.X,
                tile.Y,
                tile.Z,
                tile.Width,
                tile.Height);
            GraphPassId copy = frame.AddCopyPass(
                "Upload engine texture mip tile",
                PassQueueSelection.AnyOfType(QueueType.Graphics),
                state,
                new PassOptions(Culling: PassCullingMode.NeverCull),
                static (ref PassDefinition definition, ref TextureUploadPass value) =>
                {
                    _ = definition.Read(
                        value.Upload,
                        BufferRange.Whole,
                        PipelineSync.Copy,
                        ResourceAccess.CopySource);
                    _ = definition.Write(
                        value.Destination,
                        new TextureSubresourceRange(
                            value.MipLevel,
                            1,
                            value.ArrayLayer,
                            1,
                            value.Aspect),
                        PipelineSync.Copy,
                        ResourceAccess.CopyDestination,
                        TextureLayout.CopyDestination,
                        WriteCoverage.Partial,
                        ResourceContentState.Defined);
                },
                static (ref CopyPassCommandScope commands, in TextureUploadPass value) =>
                {
                    commands.CopyBufferToTexture(new BufferTextureCopy(
                        commands.GetBuffer(value.Upload),
                        value.BufferOffset,
                        value.BufferRowPitch,
                        value.BufferImageHeight,
                        commands.GetTexture(value.Destination),
                        value.MipLevel,
                        value.ArrayLayer,
                        value.Aspect,
                        value.X,
                        value.Y,
                        value.Z,
                        value.Width,
                        value.Height,
                        1));
                });
            realization.UploadPasses.Add(copy);
            realization.UploadedChunks.Add(tile.ChunkKey);
            frame.OrderAfter(consumer, copy);
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        foreach (Realization realization in _realizations.Values)
            realization.Dispose();
        _realizations.Clear();
    }

    private sealed class Realization : IDisposable
    {
        internal Realization(TextureShape shape, RhiTexture resource, TextureSrv view)
        {
            Shape = shape;
            Resource = resource;
            View = view;
        }

        internal TextureShape Shape { get; }
        internal RhiTexture? Resource { get; private set; }
        internal TextureSrv? View { get; private set; }
        internal ulong Revision { get; set; }
        internal bool Initialized { get; set; }
        internal ulong FrameIdentity { get; set; }
        internal GraphTextureId GraphResource { get; set; }
        internal GraphTextureSrvId GraphView { get; set; }
        internal List<GraphPassId> UploadPasses { get; } = [];
        internal HashSet<ulong> UploadedChunks { get; } = [];

        internal void Detach()
        {
            Resource = null;
            View = null;
        }

        public void Dispose()
        {
            View?.Dispose();
            Resource?.Dispose();
            View = null;
            Resource = null;
        }
    }

    private readonly record struct PreparedTile(
        byte[] Bytes,
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
        GraphBufferId Upload,
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
}

internal readonly record struct TextureUse(TextureSrv View, DescriptorSlotDesc Descriptor);

internal readonly record struct TextureShape(
    TextureDimension Dimension,
    uint Width,
    uint Height,
    uint Depth,
    uint MipLevelCount,
    uint ResourceArrayLayerCount,
    Format Format,
    Format SampledFormat,
    TextureViewDimension SampledDimension,
    TextureAspects Aspects)
{
    internal DescriptorSlotDesc Descriptor => new(
        ResourceBindingType.TextureSrv,
        SampledFormat,
        TextureDimension: SampledDimension,
        Aspects: Aspects);

    internal TextureSubresourceRange FullRange =>
        new(0, MipLevelCount, 0, ResourceArrayLayerCount, Aspects);

    internal uint ResolveArrayLayer(uint arrayLayer, uint face) =>
        SampledDimension is TextureViewDimension.Cube or TextureViewDimension.CubeArray
            ? checked((arrayLayer * 6) + face)
            : arrayLayer;

    internal static TextureShape From(EngineTexture asset)
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
            aspects);
    }
}
