using SomeEngine.Graphics;
using SomeEngine.RenderGraph;
using SomeEngine.Typography;

namespace SomeEngine.Elements.Render;

/// <summary>Graph atlas pages and the extracted pixel regions uploaded to them.</summary>
internal sealed class GlyphAtlasTextures(IGraphicsBackend backend, Device device,
    global::SomeEngine.RenderGraph.RenderGraph graph) : IDisposable
{
    private readonly List<GlyphTexturePage> _pages = [];

    internal void Reconcile(in RenderGlyphAtlas atlas)
    {
        bool changed = _pages.Count != atlas.Pages.Length;
        for (int index = 0; index < Math.Min(_pages.Count, atlas.Pages.Length); index++)
            changed |= _pages[index].Width != atlas.Pages.Span[index].Width || _pages[index].Height != atlas.Pages.Span[index].Height;
        if (!changed) return;
        using RenderGraphEdit edit = graph.BeginEdit();
        for (int index = 0; index < atlas.Pages.Length; index++)
        {
            GlyphPagePixels pixels = atlas.Pages.Span[index];
            if (index < _pages.Count && _pages[index].Width == pixels.Width && _pages[index].Height == pixels.Height) continue;
            if (index < _pages.Count) edit.Remove(_pages[index].Texture);
            GraphTextureId texture = edit.CreatePersistentTexture(new TextureDesc(TextureDimension.Texture2D,
                (uint)pixels.Width, (uint)pixels.Height, 1, 1, 1, 1, Format.R8UNorm,
                TextureUsages.Sampled | TextureUsages.CopyDestination, label: "Glyph atlas page"));
            var page = new GlyphTexturePage(texture, pixels.Width, pixels.Height);
            if (index < _pages.Count) _pages[index] = page; else _pages.Add(page);
        }
        while (_pages.Count > atlas.Pages.Length)
        {
            edit.Remove(_pages[^1].Texture);
            _pages.RemoveAt(_pages.Count - 1);
        }
        edit.Commit();
    }

    internal GlyphAtlasFrame UpdateFrame(ref RenderGraphFrame frame, in RenderGlyphAtlas atlas)
    {
        var views = new GraphTextureSrvId[atlas.Pages.Length];
        var uploads = new List<GraphPassId>();
        var changes = new List<GlyphTexturePage>();
        for (int index = 0; index < atlas.Pages.Length; index++)
        {
            GlyphPagePixels pixels = atlas.Pages.Span[index];
            GlyphTexturePage page = _pages[index];
            views[index] = frame.CreateTextureSrv(page.Texture);
            if (page.HasPixels && page.Version == atlas.Version) continue;
            var regions = new List<AtlasDirtyRegion>();
            if (!page.HasPixels || unchecked(page.Version + 1) != atlas.Version)
                regions.Add(new(index, 0, 0, pixels.Width, pixels.Height));
            else
                foreach (AtlasDirtyRegion region in atlas.DirtyRegions.Span)
                    if (region.PageIndex == index) regions.Add(region);
            GraphPassId previous = default;
            foreach (AtlasDirtyRegion region in regions)
            {
                GraphPassId upload = UploadRegion(ref frame, page, pixels, region);
                if (previous != default) frame.OrderAfter(upload, previous);
                previous = upload;
                uploads.Add(upload);
            }
            changes.Add(page);
        }
        if (changes.Count != 0) frame.ObserveExecution(new AtlasSubmission(changes.ToArray(), atlas.Version));
        return new(views, uploads.ToArray(), atlas.DistanceSpread);
    }

    private GraphPassId UploadRegion(ref RenderGraphFrame frame, GlyphTexturePage page,
        GlyphPagePixels pixels, AtlasDirtyRegion region)
    {
        var request = new BufferTextureCopy(null!, 0, 0, 0, graph.GetTexture(page.Texture),
            0, 0, TextureAspects.Color, (uint)region.X, (uint)region.Y, 0,
            (uint)region.Width, (uint)region.Height, 1);
        var description = new TextureDesc(TextureDimension.Texture2D, (uint)page.Width, (uint)page.Height,
            1, 1, 1, 1, Format.R8UNorm, TextureUsages.Sampled | TextureUsages.CopyDestination);
        TextureCopyFootprint footprint = backend.GetTextureCopyFootprint(device, description, request);
        GraphBufferSlice upload = frame.Upload(checked((int)footprint.TotalSize), BufferUsages.CopySource,
            (Pixels: pixels, Region: region, Footprint: footprint), static (bytes, data) =>
            {
                bytes.Clear();
                for (int row = 0; row < data.Region.Height; row++)
                    data.Pixels.Pixels.Span.Slice((data.Region.Y + row) * data.Pixels.Width + data.Region.X, data.Region.Width)
                        .CopyTo(bytes.Slice((int)data.Footprint.Offset + row * (int)data.Footprint.RowPitch, data.Region.Width));
            });
        var copy = new AtlasRegionCopy(upload, page.Texture, region,
            upload.Range.Offset + footprint.Offset, footprint.RowPitch,
            region.X == 0 && region.Y == 0 && region.Width == pixels.Width && region.Height == pixels.Height);
        return frame.AddCopyPass("Upload glyph atlas region", PassQueueSelection.AnyOfType(QueueType.Graphics),
            copy, default,
            static (ref PassDefinition pass, ref AtlasRegionCopy data) =>
            {
                pass.Read(data.Upload.Buffer, data.Upload.Range, PipelineSync.Copy, ResourceAccess.CopySource);
                pass.Write(data.Texture, new TextureSubresourceRange(0, 1, 0, 1, TextureAspects.Color),
                    PipelineSync.Copy, ResourceAccess.CopyDestination, TextureLayout.CopyDestination,
                    data.CompletePage ? WriteCoverage.Complete : WriteCoverage.Partial, ResourceContentState.Defined);
            },
            static (ref CopyPassCommandScope commands, in AtlasRegionCopy data) =>
            {
                AtlasDirtyRegion rectangle = data.Region;
                commands.CopyBufferToTexture(new(commands.GetBuffer(data.Upload.Buffer), data.Offset,
                    data.RowPitch, (uint)rectangle.Height, commands.GetTexture(data.Texture),
                    0, 0, TextureAspects.Color, (uint)rectangle.X, (uint)rectangle.Y, 0,
                    (uint)rectangle.Width, (uint)rectangle.Height, 1));
            });
    }

    private sealed class AtlasSubmission(GlyphTexturePage[] pages, uint version) : IRenderGraphFrameExecutionObserver
    {
        public void OnFrameSubmitted(in RenderGraphSubmission submission)
        {
            foreach (GlyphTexturePage page in pages) { page.Version = version; page.HasPixels = true; }
        }
    }

    public void Dispose()
    {
        if (!graph.IsDisposed && !graph.IsLost)
        {
            using RenderGraphEdit edit = graph.BeginEdit();
            foreach (GlyphTexturePage page in _pages) edit.Remove(page.Texture);
            edit.Commit();
        }
        _pages.Clear();
    }

    private readonly record struct AtlasRegionCopy(GraphBufferSlice Upload, GraphTextureId Texture,
        AtlasDirtyRegion Region, ulong Offset, uint RowPitch, bool CompletePage);
}

internal readonly record struct GlyphAtlasFrame(GraphTextureSrvId[] Pages, GraphPassId[] Uploads, int DistanceSpread);

internal sealed class GlyphTexturePage(GraphTextureId texture, int width, int height)
{
    internal GraphTextureId Texture { get; } = texture;
    internal int Width { get; } = width;
    internal int Height { get; } = height;
    internal uint Version { get; set; }
    internal bool HasPixels { get; set; }
}
