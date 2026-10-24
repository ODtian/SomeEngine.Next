using System.Numerics;
using System.Runtime.InteropServices;
using SlangShaderSharp;
using SomeEngine.Core.Diagnostics;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using SomeEngine.Graphics;
using SomeEngine.Layout;
using SomeEngine.Render;
using SomeEngine.Render.Components;
using SomeEngine.Render.Systems;
using SomeEngine.RenderGraph;
using Buffer = SomeEngine.Graphics.Buffer;
using Texture = SomeEngine.Resources.Texture;

namespace SomeEngine.Elements.Render;

/// <summary>Maintains one persistent raster pass per extracted screen or world canvas.</summary>
public sealed class CanvasRenderer : IDisposable, IRenderGraphFrameExecutionObserver
{
    private readonly global::SomeEngine.RenderGraph.RenderGraph _graph;
    private readonly RenderWorld _world;
    private readonly SurfaceDrawing _surfaces;
    private readonly ImageDrawing? _images;
    private readonly GlyphDrawing? _glyphs;
    private readonly IGraphicsBackend _backend;
    private readonly Device _device;
    private readonly Dictionary<Entity, GlyphAtlasTextures> _atlases = [];
    private readonly RenderDevice? _renderDevice;
    private readonly QueryHandle _query;
    private readonly Dictionary<Entity, CanvasBuffers> _canvases = [];
    private readonly List<CanvasData> _canvasData = [];
    private readonly HashSet<Entity> _canvasEntities = [];
    private Entity _view;
    private uint _lastVersion;
    private bool _disposed;

    public CanvasRenderer(IGraphicsBackend backend, Device device,
        global::SomeEngine.RenderGraph.RenderGraph graph, RenderWorld world,
        SurfaceMaterial material, Format outputFormat)
        : this(backend, device, graph, world, material, outputFormat, null, null, null) { }

    public CanvasRenderer(IGraphicsBackend backend, Device device,
        global::SomeEngine.RenderGraph.RenderGraph graph, RenderWorld world,
        SurfaceMaterial material, Format outputFormat, GlyphsMaterial? glyphs)
        : this(backend, device, graph, world, material, outputFormat, glyphs, null, null) { }

    public CanvasRenderer(IGraphicsBackend backend, Device device,
        global::SomeEngine.RenderGraph.RenderGraph graph, RenderWorld world,
        SurfaceMaterial material, Format outputFormat, ImageMaterial? images, RenderDevice? renderDevice)
        : this(backend, device, graph, world, material, outputFormat, null, images, renderDevice) { }

    public CanvasRenderer(IGraphicsBackend backend, Device device,
        global::SomeEngine.RenderGraph.RenderGraph graph, RenderWorld world,
        SurfaceMaterial material, Format outputFormat, GlyphsMaterial? glyphs,
        ImageMaterial? images, RenderDevice? renderDevice)
    {
        DevelopmentCheck.Require(ReferenceEquals(graph.Device, device), "Canvas drawing uses the graph's device.");
        DevelopmentCheck.Require(images is null || renderDevice is not null && ReferenceEquals(renderDevice.Device, device),
            "Image drawing requires a RenderDevice for the graph's device.");
        _graph = graph;
        _world = world;
        _backend = backend;
        _device = device;
        _renderDevice = renderDevice;
        _surfaces = new SurfaceDrawing(backend, device, material, outputFormat);
        try
        {
            if (images is not null) _images = new ImageDrawing(backend, device, images, outputFormat);
            if (glyphs is not null) _glyphs = new GlyphDrawing(backend, device, glyphs, outputFormat);
            _query = world.Query(new QueryDefinitionBuilder().Read<RenderCanvas>()
                .OptionalBuffer<SurfaceInstance>(QueryAccess.Read).OptionalBuffer<ImageInstance>(QueryAccess.Read)
                .OptionalBuffer<GlyphInstance>(QueryAccess.Read).Optional<CanvasGlyphAtlas>(QueryAccess.Read));
        }
        catch
        {
            _images?.Dispose();
            _glyphs?.Dispose();
            _surfaces.Dispose();
            throw;
        }
    }

    /// <summary>Reconciles screen canvas drawing with the supplied color target.</summary>
    public void ReconcileGraph(GraphTextureId screenTarget) => ReconcileGraph(screenTarget, default);

    /// <summary>Explicitly maintains canvas passes, targets and depth testing.</summary>
    public void ReconcileGraph(GraphTextureId screenTarget, WorldCanvasTarget worldTarget)
    {
        DevelopmentCheck.Require(!_disposed, "Canvas drawing must be active.");
        _canvasData.Clear();
        _canvasEntities.Clear();
        var receiver = (Renderer: this, Screen: screenTarget, World: worldTarget);
        _world.ExecuteQuery(_query, _lastVersion, ref receiver, static (rows, ref data) =>
        {
            CanvasRenderer renderer = data.Renderer;
            foreach (QueryChunkView chunk in rows.Chunks)
            {
                ReadOnlySpan<RenderCanvas> values = chunk.Read<RenderCanvas>();
                for (int row = 0; row < chunk.Count; row++)
                {
                    RenderCanvas canvas = values[row];
                    if (canvas.Target.Kind == CanvasTargetKind.RenderTexture) continue;
                    bool worldSpace = canvas.Target.Kind == CanvasTargetKind.WorldSpace;
                    GraphTextureId color = worldSpace ? data.World.Color : data.Screen;
                    GraphTextureId depth = worldSpace && canvas.DepthTest ? data.World.Depth : default;
                    DevelopmentCheck.Require(color != default, "Canvas drawing requires its color target.");
                    DevelopmentCheck.Require(!worldSpace || !canvas.DepthTest || depth != default,
                        "World canvas depth testing requires the scene depth target.");
                    Entity entity = chunk.Entities[row];
                    bool surfaces = chunk.HasBuffer<SurfaceInstance>();
                    bool images = chunk.HasBuffer<ImageInstance>();
                    Entity atlas = chunk.Has<CanvasGlyphAtlas>() ? chunk.Read<CanvasGlyphAtlas>()[row].Atlas : Entity.Null;
                    bool glyphs = chunk.HasBuffer<GlyphInstance>() && atlas != Entity.Null;
                    renderer._canvasEntities.Add(entity);
                    renderer._canvasData.Add(new(entity, canvas, surfaces, images,
                        surfaces && chunk.RowBufferChangedSinceLastSystemVersion<SurfaceInstance>(row),
                        images && chunk.RowBufferChangedSinceLastSystemVersion<ImageInstance>(row),
                        glyphs, glyphs && chunk.RowBufferChangedSinceLastSystemVersion<GlyphInstance>(row), atlas,
                        new(color, depth, depth == default ? default : data.World.DepthFormat,
                            depth == default ? default : data.World.DepthComparison, worldSpace)));
                }
            }
        });
        bool editRequired = false;
        foreach (Entity entity in _canvases.Keys) editRequired |= !_canvasEntities.Contains(entity);
        foreach (CanvasData data in _canvasData)
            editRequired |= !_canvases.TryGetValue(data.Entity, out CanvasBuffers? buffers) || buffers.Target != data.Target;
        if (editRequired)
        {
            var additions = new Dictionary<Entity, CanvasBuffers>();
            var removals = new List<Entity>();
            using (RenderGraphEdit edit = _graph.BeginEdit())
            {
                foreach ((Entity entity, CanvasBuffers buffers) in _canvases)
                    if (!_canvasEntities.Contains(entity))
                    {
                        RemovePass(edit, buffers);
                        removals.Add(entity);
                    }
                foreach (CanvasData data in _canvasData)
                    if (!_canvases.TryGetValue(data.Entity, out CanvasBuffers? buffers) || buffers.Target != data.Target)
                    {
                        if (buffers is not null)
                        {
                            RemovePass(edit, buffers);
                            removals.Add(data.Entity);
                        }
                        additions.Add(data.Entity, AddPass(edit, data.Target));
                    }
                CanvasBuffers[] passes = _canvases.Where(pair => !removals.Contains(pair.Key))
                    .Select(pair => pair.Value).Concat(additions.Values).ToArray();
                foreach (CanvasBuffers screen in passes)
                    if (!screen.Target.WorldSpace)
                        foreach (CanvasBuffers world in passes)
                            if (world.Target.WorldSpace && world.Target.Color == screen.Target.Color)
                                edit.OrderAfter(screen.Pass, world.Pass);
                edit.Commit();
            }
            foreach (Entity entity in removals)
            {
                RetireBuffers(_canvases[entity]);
                _canvases.Remove(entity);
            }
            foreach ((Entity entity, CanvasBuffers buffers) in additions) _canvases.Add(entity, buffers);
        }
        _view = worldTarget.View;
        foreach (CanvasData data in _canvasData)
        {
            CanvasBuffers buffers = _canvases[data.Entity];
            buffers.Canvas = data.Canvas;
            buffers.SurfacesChanged |= data.SurfacesChanged || buffers.HasSurfaces != data.HasSurfaces;
            buffers.ImagesChanged |= data.ImagesChanged || buffers.HasImages != data.HasImages;
            buffers.HasSurfaces = data.HasSurfaces;
            buffers.HasImages = data.HasImages;
            buffers.GlyphsChanged |= data.GlyphsChanged || buffers.HasGlyphs != data.HasGlyphs || buffers.Atlas != data.Atlas;
            buffers.HasGlyphs = data.HasGlyphs;
            buffers.Atlas = data.Atlas;
        }
        var referencedAtlases = new HashSet<Entity>(_canvasData.Select(data => data.Atlas));
        foreach (Entity atlas in _atlases.Keys.ToArray())
            if (!referencedAtlases.Contains(atlas))
            {
                _graph.RetireAfterSubmittedFrames(_atlases[atlas]);
                _atlases.Remove(atlas);
            }
        _lastVersion = _world.CurrentTick;
    }

    /// <summary>Returns the canvas pass for application ordering and diagnostics.</summary>
    public bool TryGetPass(Entity canvas, out GraphPassId pass)
    {
        bool found = _canvases.TryGetValue(canvas, out CanvasBuffers? buffers);
        pass = found ? buffers!.Pass : default;
        return found;
    }

    /// <summary>Binds canvas values and changed drawing data within an existing graph frame.</summary>
    public void UpdateFrame(ref RenderGraphFrame frame)
    {
        DevelopmentCheck.Require(!_disposed, "Canvas drawing must be active.");
        frame.ObserveExecution(this);
        var atlasFrames = new Dictionary<Entity, GlyphAtlasFrame>();
        if (_glyphs is not null)
            foreach (CanvasBuffers buffers in _canvases.Values)
            {
                if (buffers.Atlas == Entity.Null || atlasFrames.ContainsKey(buffers.Atlas)) continue;
                if (!_atlases.TryGetValue(buffers.Atlas, out GlyphAtlasTextures? atlas))
                    _atlases.Add(buffers.Atlas, atlas = new GlyphAtlasTextures(_backend, _device, _graph));
                atlasFrames.Add(buffers.Atlas, atlas.UpdateFrame(ref frame, _world.Read<RenderGlyphAtlas>(buffers.Atlas)));
            }
        Span<Vector4> canvas = stackalloc Vector4[16];
        foreach ((Entity entity, CanvasBuffers buffers) in _canvases)
        {
            if (buffers.SurfacesChanged || buffers.ImagesChanged || buffers.GlyphsChanged) UpdateInstances(entity, buffers);
            frame.SetBufferInput(buffers.SurfaceInput, new GraphBufferSlice(
                frame.Import(buffers.Surfaces!.Buffer, buffers.Surfaces.Boundaries), BufferRange.Whole));
            if (_images is not null)
                frame.SetBufferInput(buffers.ImageInput!, new GraphBufferSlice(
                    frame.Import(buffers.Images!.Buffer, buffers.Images.Boundaries), BufferRange.Whole));
            if (_glyphs is not null)
                frame.SetBufferInput(buffers.GlyphInput!, new GraphBufferSlice(
                    frame.Import(buffers.Glyphs!.Buffer, buffers.Glyphs.Boundaries), BufferRange.Whole));
            GlyphAtlasFrame glyphAtlas = _glyphs is not null && buffers.Atlas != Entity.Null
                ? atlasFrames[buffers.Atlas] : new([], [], 0);
            foreach (GraphPassId upload in glyphAtlas.Uploads) frame.OrderAfter(buffers.Pass, upload);
            canvas.Clear();
            canvas[0] = new(buffers.Canvas.Size.Width, buffers.Canvas.Size.Height, buffers.Canvas.Scale, glyphAtlas.DistanceSpread);
            int width = (int)buffers.Canvas.Size.Width;
            int height = (int)buffers.Canvas.Size.Height;
            Matrix4x4 transform;
            if (buffers.Target.WorldSpace)
            {
                DevelopmentCheck.Require(_world.Has<RenderView>(_view), "World canvas drawing requires a RenderWorld view.");
                RenderView view = _world.Read<RenderView>(_view);
                width = (int)view.ViewportWidth;
                height = (int)view.ViewportHeight;
                float scale = buffers.Canvas.Scale;
                transform = Matrix4x4.CreateScale(scale, -scale, 1) * buffers.Canvas.Transform * view.View * view.Projection;
            }
            else transform = Matrix4x4.CreateScale(2 * buffers.Canvas.Scale / width, -2 * buffers.Canvas.Scale / height, 1)
                * Matrix4x4.CreateTranslation(-1, 1, 0);
            MemoryMarshal.Cast<Vector4, Matrix4x4>(canvas[1..5])[0] = transform;
            frame.SetBufferData(buffers.CanvasBuffer, MemoryMarshal.AsBytes(canvas));
            GraphTextureSrvId[] textures = PrepareTextures(ref frame, buffers);
            frame.SetPassData(buffers.Pass, buffers.Data with
            {
                Width = width, Height = height,
                Draws = buffers.Draws, Textures = textures, SurfaceBuffer = buffers.Surfaces, ImageBuffer = buffers.Images,
                GlyphBuffer = buffers.Glyphs, AtlasPages = glyphAtlas.Pages,
            });
            PassDefinition pass = frame.GetPass(buffers.Pass);
            foreach (GraphTextureSrvId texture in textures) pass.Read(texture, PipelineSync.PixelShading);
            foreach (GraphTextureSrvId page in glyphAtlas.Pages) pass.Read(page, PipelineSync.PixelShading);
            frame.SetPassEnabled(buffers.Pass, buffers.Draws.Length > 0 && width > 0 && height > 0);
        }
    }

    private void UpdateInstances(Entity entity, CanvasBuffers buffers)
    {
        SurfaceInstance[] surfaces = [];
        ImageInstance[] images = [];
        GlyphInstance[] glyphs = [];
        if (buffers.HasSurfaces)
            _world.ExecuteBufferRead<SurfaceInstance>(entity, values => surfaces = values.AsSpan().ToArray());
        if (buffers.HasImages)
            _world.ExecuteBufferRead<ImageInstance>(entity, values => images = values.AsSpan().ToArray());
        if (buffers.HasGlyphs)
            _world.ExecuteBufferRead<GlyphInstance>(entity, values => glyphs = values.AsSpan().ToArray());
        DevelopmentCheck.Require(images.Length == 0 || _images is not null,
            "A canvas with images requires an Image material and RenderDevice.");
        DevelopmentCheck.Require(glyphs.Length == 0 || _glyphs is not null,
            "A canvas with glyphs requires a Glyphs material.");
        if (buffers.SurfacesChanged)
        {
            InstanceBuffer replacement = _surfaces.CreateInstances(surfaces);
            if (buffers.Surfaces is { } previous) _graph.RetireAfterSubmittedFrames(previous);
            buffers.Surfaces = replacement;

        }
        if (buffers.ImagesChanged && _images is not null)
        {
            InstanceBuffer replacement = _images.CreateInstances(images);
            if (buffers.Images is { } previous) _graph.RetireAfterSubmittedFrames(previous);
            buffers.Images = replacement;

            buffers.Textures = images.Select(value => value.Texture).ToArray();
        }
        if (buffers.GlyphsChanged && _glyphs is not null)
        {
            InstanceBuffer replacement = _glyphs.CreateInstances(glyphs);
            if (buffers.Glyphs is { } previous) _graph.RetireAfterSubmittedFrames(previous);
            buffers.Glyphs = replacement;
        }
        var draws = new List<CanvasDraw>(surfaces.Length + images.Length + glyphs.Length);
        for (int index = 0; index < surfaces.Length; index++) draws.Add(new(CanvasVisual.Surface, index, surfaces[index].DrawOrder));
        for (int index = 0; index < images.Length; index++) draws.Add(new(CanvasVisual.Image, index, images[index].DrawOrder));
        for (int index = 0; index < glyphs.Length; index++)
            draws.Add(new(CanvasVisual.Glyph, index, glyphs[index].DrawOrder, glyphs[index].PageIndex));
        draws.Sort(static (left, right) =>
        {
            int order = left.DrawOrder.CompareTo(right.DrawOrder);
            if (order == 0) order = left.Visual.CompareTo(right.Visual);
            return order == 0 ? left.Index.CompareTo(right.Index) : order;
        });
        buffers.Draws = draws.ToArray();
        buffers.SurfacesChanged = buffers.ImagesChanged = buffers.GlyphsChanged = false;
    }

    private GraphTextureSrvId[] PrepareTextures(ref RenderGraphFrame frame, CanvasBuffers buffers)
    {
        if (buffers.Textures.Length == 0) return [];
        var views = new GraphTextureSrvId[buffers.Textures.Length];
        var textures = new Dictionary<Texture, GraphTextureSrvId>();
        GraphTextureSrvId missing = default;
        for (int index = 0; index < views.Length; index++)
        {
            Texture? texture = buffers.Textures[index];
            if (texture is null && missing != default) { views[index] = missing; continue; }
            if (texture is not null && textures.TryGetValue(texture, out GraphTextureSrvId found))
            { views[index] = found; continue; }
            SomeEngine.Graphics.Texture resource = texture is null ?
                _renderDevice!.PrepareMissingTexture(ref frame, buffers.Pass) :
                _renderDevice!.PrepareTexture(ref frame, buffers.Pass, texture);
            TextureSrv view = texture is null ? _renderDevice!.MissingTextureSrv : _renderDevice!.GetTextureSrv(texture);
            TextureSrvDesc description = view.Description;
            views[index] = frame.CreateTextureSrv(frame.GetImported(resource), description.Range,
                description.Format, description.Dimension, "Canvas image texture");
            if (texture is null) missing = views[index];
            else textures.Add(texture, views[index]);
        }
        return views;
    }

    private CanvasBuffers AddPass(RenderGraphEdit edit, CanvasPassTarget target)
    {
        GraphBufferInput surfaces = edit.CreateBufferInput(BufferUsages.ShaderRead);
        GraphBufferSrvId surfaceView = edit.CreateBufferSrv(surfaces, label: "Surface instances");
        GraphBufferInput? images = _images is null ? null : edit.CreateBufferInput(BufferUsages.ShaderRead);
        GraphBufferSrvId imageView = images is null ? default : edit.CreateBufferSrv(images, label: "Image instances");
        GraphBufferInput? glyphs = _glyphs is null ? null : edit.CreateBufferInput(BufferUsages.ShaderRead);
        GraphBufferSrvId glyphView = glyphs is null ? default : edit.CreateBufferSrv(glyphs, label: "Glyph instances");
        GraphBufferId canvas = edit.DeclareExternalBuffer(new BufferDesc(256, BufferUsages.Constant,
            "Canvas size and scale"), MemoryType.Upload);
        GraphBufferCbvId canvasView = edit.CreateBufferCbv(canvas);
        GraphColorAttachmentViewId targetView = edit.CreateColorAttachmentView(target.Color, label: "Canvas color");
        GraphDepthStencilViewId depthView = target.Depth == default ? default : edit.CreateDepthStencilView(target.Depth,
            new TextureSubresourceRange(0, 1, 0, 1, TextureAspects.Depth), target.DepthFormat,
            TextureViewDimension.Texture2D, readOnlyDepth: true, label: "Canvas scene depth");
        var data = new CanvasPassData(_surfaces, _images, _glyphs,
            _surfaces.GetPipeline(target), _images?.GetPipeline(target), _glyphs?.GetPipeline(target),
            surfaceView, imageView, glyphView, canvas, canvasView, targetView, depthView,
            null, null, null, [], [], [], 0, 0);
        GraphPassId pass = edit.AddRasterFramePass("Canvas drawing", PassQueueSelection.AnyOfType(QueueType.Graphics),
            data, default, CanvasPassData.Declare, CanvasPassData.Draw);
        return new CanvasBuffers(pass, surfaces, images, glyphs, target, data);
    }

    void IRenderGraphFrameExecutionObserver.OnRenderGraphFrameFinished(RenderGraphFrameResult result)
    {
        foreach (CanvasBuffers buffers in _canvases.Values)
        {
            if (buffers.Surfaces is { } surfaces) surfaces.Boundaries = ReadBoundaries(result, surfaces.Buffer, surfaces.Boundaries);
            if (buffers.Images is { } images) images.Boundaries = ReadBoundaries(result, images.Buffer, images.Boundaries);
            if (buffers.Glyphs is { } glyphs) glyphs.Boundaries = ReadBoundaries(result, glyphs.Buffer, glyphs.Boundaries);
        }
    }

    private static BufferBoundaryState[] ReadBoundaries(RenderGraphFrameResult result, Buffer buffer, BufferBoundaryState[] before)
    {
        ReadOnlySpan<BufferBoundaryState> states = result.GetBufferStates(buffer);
        if (states.IsEmpty) return before;
        if (before.Length != states.Length) before = new BufferBoundaryState[states.Length];
        states.CopyTo(before);
        return before;
    }

    public void Dispose()
    {
        if (_disposed) return;
        if (_canvases.Count != 0)
        {
            using RenderGraphEdit edit = _graph.BeginEdit();
            foreach (CanvasBuffers buffers in _canvases.Values) RemovePass(edit, buffers);
            edit.Commit();
        }
        foreach (CanvasBuffers buffers in _canvases.Values) RetireBuffers(buffers);
        _canvases.Clear();
        _world.ReleaseQuery(_query);
        _graph.RetireAfterSubmittedFrames(_surfaces);
        if (_images is not null) _graph.RetireAfterSubmittedFrames(_images);
        if (_glyphs is not null) _graph.RetireAfterSubmittedFrames(_glyphs);
        foreach (GlyphAtlasTextures atlas in _atlases.Values) _graph.RetireAfterSubmittedFrames(atlas);
        _atlases.Clear();
        _disposed = true;
    }

    private void RetireBuffers(CanvasBuffers buffers)
    {
        if (buffers.Surfaces is { } surfaces) _graph.RetireAfterSubmittedFrames(surfaces);
        if (buffers.Images is { } images) _graph.RetireAfterSubmittedFrames(images);
        if (buffers.Glyphs is { } glyphs) _graph.RetireAfterSubmittedFrames(glyphs);
    }

    private static void RemovePass(RenderGraphEdit edit, CanvasBuffers buffers)
    {
        edit.Remove(buffers.Pass);
        edit.Remove(buffers.Data.Surfaces);
        if (buffers.ImageInput is not null) edit.Remove(buffers.Data.Images);
        if (buffers.GlyphInput is not null) edit.Remove(buffers.Data.Glyphs);
        edit.Remove(buffers.Data.Canvas);
        edit.Remove(buffers.CanvasBuffer);
        edit.Remove(buffers.Data.Target);
        if (buffers.Data.Depth != default) edit.Remove(buffers.Data.Depth);
    }

    private readonly record struct CanvasData(Entity Entity, RenderCanvas Canvas, bool HasSurfaces, bool HasImages,
        bool SurfacesChanged, bool ImagesChanged, bool HasGlyphs, bool GlyphsChanged, Entity Atlas, CanvasPassTarget Target);
}

internal sealed class CanvasBuffers(GraphPassId pass, GraphBufferInput surfaces, GraphBufferInput? images,
    GraphBufferInput? glyphs, CanvasPassTarget target, CanvasPassData data)
{
    internal GraphPassId Pass { get; } = pass;
    internal GraphBufferInput SurfaceInput { get; } = surfaces;
    internal GraphBufferInput? ImageInput { get; } = images;
    internal GraphBufferInput? GlyphInput { get; } = glyphs;
    internal GraphBufferId CanvasBuffer => Data.CanvasBuffer;
    internal CanvasPassData Data { get; } = data;
    internal CanvasPassTarget Target { get; } = target;
    internal RenderCanvas Canvas { get; set; }
    internal InstanceBuffer? Surfaces { get; set; }
    internal InstanceBuffer? Images { get; set; }
    internal InstanceBuffer? Glyphs { get; set; }
    internal Entity Atlas { get; set; }


    internal Texture?[] Textures { get; set; } = [];
    internal CanvasDraw[] Draws { get; set; } = [];
    internal bool HasSurfaces { get; set; }
    internal bool HasImages { get; set; }
    internal bool HasGlyphs { get; set; }
    internal bool SurfacesChanged { get; set; } = true;
    internal bool ImagesChanged { get; set; } = true;
    internal bool GlyphsChanged { get; set; } = true;
}

internal enum CanvasVisual : byte { Surface, Glyph, Image }
internal readonly record struct CanvasDraw(CanvasVisual Visual, int Index, int DrawOrder, int PageIndex = -1);

internal readonly record struct CanvasPassData(SurfaceDrawing SurfaceDrawing, ImageDrawing? ImageDrawing,
    GlyphDrawing? GlyphDrawing, Pipeline SurfacePipeline, Pipeline? ImagePipeline, Pipeline? GlyphPipeline,
    GraphBufferSrvId Surfaces, GraphBufferSrvId Images, GraphBufferSrvId Glyphs, GraphBufferId CanvasBuffer, GraphBufferCbvId Canvas,
    GraphColorAttachmentViewId Target, GraphDepthStencilViewId Depth, InstanceBuffer? SurfaceBuffer, InstanceBuffer? ImageBuffer,
    InstanceBuffer? GlyphBuffer, CanvasDraw[] Draws, GraphTextureSrvId[] Textures, GraphTextureSrvId[] AtlasPages, int Width, int Height)
{
    internal static void Declare(ref PassDefinition pass, ref CanvasPassData data)
    {
        pass.SetPipeline(data.SurfacePipeline);
        pass.Read(data.Surfaces, PipelineSync.VertexShading | PipelineSync.PixelShading);
        if (data.ImageDrawing is not null) pass.Read(data.Images, PipelineSync.VertexShading | PipelineSync.PixelShading);
        if (data.GlyphDrawing is not null) pass.Read(data.Glyphs, PipelineSync.VertexShading | PipelineSync.PixelShading);
        pass.Read(data.Canvas, PipelineSync.VertexShading | PipelineSync.PixelShading);
        pass.ColorAttachment(0, data.Target, LoadType.Load, StoreType.Store, WriteCoverage.Partial, default);
        if (data.Depth != default)
            pass.DepthStencilAttachment(data.Depth, LoadType.Load, StoreType.Store, WriteCoverage.Partial, 0,
                LoadType.Discard, StoreType.Discard, WriteCoverage.Partial, 0);
    }

    internal static void Draw(ref RasterPassCommandScope commands, in CanvasPassData data)
    {
        commands.SetViewports([new Viewport(0, 0, data.Width, data.Height)]);
        commands.SetScissors([new ScissorRect(0, 0, data.Width, data.Height)]);
        ResourceBinding canvas = ResourceBinding.ConstantBuffer(commands.GetBuffer(data.CanvasBuffer), BufferRange.Whole);
        ResourceBinding[] surfaces = [ResourceBinding.Null(ResourceBindingType.BufferSrv), canvas];
        ResourceBinding[] images = data.ImageDrawing is null ? [] :
            [ResourceBinding.Null(ResourceBindingType.BufferSrv), canvas,
                ResourceBinding.Null(ResourceBindingType.TextureSrv), ResourceBinding.SampledWith(data.ImageDrawing.Sampler)];
        ResourceBinding[] glyphs = data.GlyphDrawing is null ? [] :
            [ResourceBinding.Null(ResourceBindingType.BufferSrv), canvas,
                ResourceBinding.Null(ResourceBindingType.TextureSrv), ResourceBinding.SampledWith(data.GlyphDrawing.Sampler)];
        for (int index = 0; index < data.Draws.Length;)
        {
            CanvasDraw draw = data.Draws[index];
            int count = 1;
            while (index + count < data.Draws.Length)
            {
                CanvasDraw next = data.Draws[index + count];
                if (next.Visual != draw.Visual || next.Index != draw.Index + count ||
                    draw.Visual == CanvasVisual.Image && data.Textures[next.Index] != data.Textures[draw.Index] ||
                    draw.Visual == CanvasVisual.Glyph && next.PageIndex != draw.PageIndex) break;
                count++;
            }
            if (draw.Visual == CanvasVisual.Image)
            {
                ImageDrawing image = data.ImageDrawing!;
                commands.SetPipeline(data.ImagePipeline!);
                images[0] = ResourceBinding.ReadOnlyBuffer(data.ImageBuffer!.Views[draw.Index]);
                images[2] = ResourceBinding.SampledTexture(commands.GetTextureSrv(data.Textures[draw.Index]));
                commands.SetTransientParameterBindings(new ParameterBlockBindings(image.Parameters, images, default));
            }
            else if (draw.Visual == CanvasVisual.Glyph)
            {
                GlyphDrawing glyph = data.GlyphDrawing!;
                commands.SetPipeline(data.GlyphPipeline!);
                glyphs[0] = ResourceBinding.ReadOnlyBuffer(data.GlyphBuffer!.Views[draw.Index]);
                glyphs[2] = ResourceBinding.SampledTexture(commands.GetTextureSrv(data.AtlasPages[draw.PageIndex]));
                commands.SetTransientParameterBindings(new ParameterBlockBindings(glyph.Parameters, glyphs, default));
            }
            else
            {
                commands.SetPipeline(data.SurfacePipeline);
                surfaces[0] = ResourceBinding.ReadOnlyBuffer(data.SurfaceBuffer!.Views[draw.Index]);
                commands.SetTransientParameterBindings(new ParameterBlockBindings(data.SurfaceDrawing.Parameters, surfaces, default));
            }
            commands.Draw(new DrawArguments(6, (uint)count, 0, 0));
            index += count;
        }
    }
}
