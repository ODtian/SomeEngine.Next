using SomeEngine.Graphics;
using SomeEngine.RenderGraph;
using SomeEngine.Typography;
using Buffer = SomeEngine.Graphics.Buffer;

namespace SomeEngine.Elements.Render;

/// <summary>Retains GPU atlas pages and uploads the rectangles received through extraction.</summary>
internal sealed class GlyphAtlasTextures(IGraphicsBackend backend, Device device,
    global::SomeEngine.RenderGraph.RenderGraph graph) : IDisposable, IRenderGraphFrameExecutionObserver
{
    private readonly List<GlyphTexturePage> _pages = [];

    internal GlyphAtlasFrame UpdateFrame(ref RenderGraphFrame frame, in RenderGlyphAtlas atlas)
    {
        frame.ObserveExecution(this);
        var views = new GraphTextureSrvId[atlas.Pages.Length];
        var uploads = new List<GraphPassId>();
        for (int index = 0; index < atlas.Pages.Length; index++)
        {
            GlyphPagePixels pixels = atlas.Pages.Span[index];
            if (index < _pages.Count && (_pages[index].Width != pixels.Width || _pages[index].Height != pixels.Height))
            {
                graph.RetireAfterSubmittedFrames(_pages[index].Texture);
                _pages[index] = CreatePage(pixels);
            }
            else if (index >= _pages.Count) _pages.Add(CreatePage(pixels));
            GlyphTexturePage page = _pages[index];
            page.Upload = default;
            GraphTextureId texture = frame.Import(page.Texture, page.Boundaries);
            views[index] = frame.CreateTextureSrv(texture);
            if (page.HasPixels && page.Version == atlas.Version) continue;
            var regions = new List<AtlasDirtyRegion>();
            if (!page.HasPixels || unchecked(page.Version + 1) != atlas.Version)
                regions.Add(new(index, 0, 0, pixels.Width, pixels.Height));
            else
                foreach (AtlasDirtyRegion region in atlas.DirtyRegions.Span)
                    if (region.PageIndex == index) regions.Add(region);
            if (regions.Count == 0) { page.Version = atlas.Version; continue; }
            foreach (AtlasDirtyRegion region in regions)
            {
                GraphPassId upload = UploadRegion(ref frame, texture, page, pixels, region);
                if (page.Upload != default) frame.OrderAfter(upload, page.Upload);
                page.Upload = upload;
                uploads.Add(upload);
            }
            page.UploadVersion = atlas.Version;
        }
        while (_pages.Count > atlas.Pages.Length)
        {
            graph.RetireAfterSubmittedFrames(_pages[^1].Texture);
            _pages.RemoveAt(_pages.Count - 1);
        }
        return new(views, uploads.ToArray(), atlas.DistanceSpread);
    }

    private GlyphTexturePage CreatePage(GlyphPagePixels pixels)
    {
        var description = new TextureDesc(TextureDimension.Texture2D,
            (uint)pixels.Width, (uint)pixels.Height, 1, 1, 1, 1, Format.R8UNorm,
            TextureUsages.Sampled | TextureUsages.CopyDestination, label: "Glyph atlas page");
        return new(backend.CreateTexture(device, description), pixels.Width, pixels.Height);
    }

    private GraphPassId UploadRegion(ref RenderGraphFrame frame, GraphTextureId texture,
        GlyphTexturePage page, GlyphPagePixels pixels, AtlasDirtyRegion region)
    {
        var request = new BufferTextureCopy(null!, 0, 0, 0, page.Texture,
            0, 0, TextureAspects.Color, (uint)region.X, (uint)region.Y, 0,
            (uint)region.Width, (uint)region.Height, 1);
        var description = new TextureDesc(TextureDimension.Texture2D, (uint)page.Width, (uint)page.Height,
            1, 1, 1, 1, Format.R8UNorm, TextureUsages.Sampled | TextureUsages.CopyDestination);
        TextureCopyFootprint footprint = backend.GetTextureCopyFootprint(device, description, request);
        Buffer upload = backend.CreateBuffer(device, new BufferDesc(footprint.TotalSize,
            BufferUsages.CopySource, "Glyph atlas region"), MemoryType.Upload);
        try
        {
            using (MappedBuffer mapped = backend.Map(upload, MapType.Write, BufferRange.Whole))
            {
                mapped.Bytes.Clear();
                for (int row = 0; row < region.Height; row++)
                    pixels.Pixels.Span.Slice((region.Y + row) * pixels.Width + region.X, region.Width)
                        .CopyTo(mapped.Bytes.Slice((int)footprint.Offset + row * (int)footprint.RowPitch, region.Width));
                mapped.Flush(new BufferRange(0, footprint.TotalSize));
            }
            GraphBufferId buffer = frame.Import(upload,
                [new(BufferRange.Whole, upload.InitialSync, upload.InitialAccess, ResourceContentState.Defined)]);
            var data = new AtlasRegionCopy(buffer, texture, region, footprint.Offset, footprint.RowPitch,
                region.X == 0 && region.Y == 0 && region.Width == pixels.Width && region.Height == pixels.Height);
            GraphPassId pass = frame.AddCopyPass("Upload glyph atlas region", PassQueueSelection.AnyOfType(QueueType.Graphics),
                data, default,
                static (ref PassDefinition pass, ref AtlasRegionCopy data) =>
                {
                    pass.Read(data.Buffer, BufferRange.Whole, PipelineSync.Copy, ResourceAccess.CopySource);
                    pass.Write(data.Texture, new TextureSubresourceRange(0, 1, 0, 1, TextureAspects.Color),
                        PipelineSync.Copy, ResourceAccess.CopyDestination, TextureLayout.CopyDestination,
                        data.CompletePage ? WriteCoverage.Complete : WriteCoverage.Partial, ResourceContentState.Defined);
                },
                static (ref CopyPassCommandScope commands, in AtlasRegionCopy data) =>
                {
                    AtlasDirtyRegion rectangle = data.Region;
                    commands.CopyBufferToTexture(new(commands.GetBuffer(data.Buffer), data.Offset,
                        data.RowPitch, (uint)rectangle.Height, commands.GetTexture(data.Texture),
                        0, 0, TextureAspects.Color, (uint)rectangle.X, (uint)rectangle.Y, 0,
                        (uint)rectangle.Width, (uint)rectangle.Height, 1));
                });
            frame.RetireAfterFrame(upload);
            return pass;
        }
        catch { upload.Dispose(); throw; }
    }

    void IRenderGraphFrameExecutionObserver.OnRenderGraphFrameFinished(RenderGraphFrameResult result)
    {
        foreach (GlyphTexturePage page in _pages)
        {
            ReadOnlySpan<TextureBoundaryState> states = result.GetTextureStates(page.Texture);
            if (!states.IsEmpty) page.Boundaries = states.ToArray();
            if (page.Upload != default && result.TryGetPassCompletion(page.Upload, out _))
            {
                page.Version = page.UploadVersion;
                page.HasPixels = true;
            }
        }
    }

    public void Dispose()
    {
        foreach (GlyphTexturePage page in _pages) page.Texture.Dispose();
        _pages.Clear();
    }

    private readonly record struct AtlasRegionCopy(GraphBufferId Buffer, GraphTextureId Texture,
        AtlasDirtyRegion Region, ulong Offset, uint RowPitch, bool CompletePage);
}

internal readonly record struct GlyphAtlasFrame(GraphTextureSrvId[] Pages, GraphPassId[] Uploads, int DistanceSpread);

internal sealed class GlyphTexturePage(Texture texture, int width, int height)
{
    internal Texture Texture { get; } = texture;
    internal int Width { get; } = width;
    internal int Height { get; } = height;
    internal TextureBoundaryState[] Boundaries { get; set; } =
        [new(new TextureSubresourceRange(0, 1, 0, 1, TextureAspects.Color), texture.InitialSync,
            texture.InitialAccess, texture.InitialLayout, ResourceContentState.Undefined)];
    internal uint Version { get; set; }
    internal bool HasPixels { get; set; }
    internal uint UploadVersion { get; set; }
    internal GraphPassId Upload { get; set; }
}
