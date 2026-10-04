using System.Numerics;
using SomeEngine.Core.Diagnostics;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS;
using SomeEngine.ECS.Queries;
using SomeEngine.Graphics;
using SomeEngine.Layout;
using SomeEngine.Render.Components;
using SomeEngine.Render.Systems;
using SomeEngine.RenderGraph;
using Buffer = SomeEngine.Graphics.Buffer;

namespace SomeEngine.Elements.Render;

/// <summary>Maintains one persistent raster pass per extracted screen or world canvas.</summary>
public sealed class CanvasRenderer : IDisposable, IRenderGraphFrameExecutionObserver
{
    private readonly global::SomeEngine.RenderGraph.RenderGraph _graph;
    private readonly RenderWorld _world;
    private readonly SurfaceDrawing _drawing;
    private readonly GlyphDrawing? _glyphDrawing;
    private readonly IGraphicsBackend _backend;
    private readonly Device _device;
    private readonly Dictionary<Entity, GlyphAtlasTextures> _atlases = [];
    private readonly QueryHandle _query;
    private readonly Dictionary<Entity, CanvasBuffers> _canvases = [];
    private readonly List<CanvasData> _canvasData = [];
    private readonly HashSet<Entity> _canvasEntities = [];
    private Entity _view;
    private uint _lastVersion;
    private bool _disposed;

    public CanvasRenderer(IGraphicsBackend backend, Device device,
        global::SomeEngine.RenderGraph.RenderGraph graph, RenderWorld world,
        SurfaceMaterial material, Format outputFormat) : this(backend, device, graph, world, material, outputFormat, null) { }

    public CanvasRenderer(IGraphicsBackend backend, Device device,
        global::SomeEngine.RenderGraph.RenderGraph graph, RenderWorld world,
        SurfaceMaterial material, Format outputFormat, GlyphsMaterial? glyphs)
    {
        DevelopmentCheck.Require(ReferenceEquals(graph.Device, device), "Canvas drawing uses the graph's device.");
        _graph = graph;
        _world = world;
        _backend = backend;
        _device = device;
        _drawing = new SurfaceDrawing(backend, device, material, outputFormat);
        if (glyphs is not null) _glyphDrawing = new GlyphDrawing(backend, device, glyphs, outputFormat);
        _query = world.Query(new QueryDefinitionBuilder().Read<RenderCanvas>().ReadBuffer<SurfaceInstance>()
            .OptionalBuffer<GlyphInstance>(QueryAccess.Read).Optional<CanvasGlyphAtlas>(QueryAccess.Read));
    }

    /// <summary>Explicitly adds/removes screen canvas passes or reconnects their color target.</summary>
    public void ReconcileGraph(GraphTextureId screenTarget) => ReconcileGraph(screenTarget, default);

    public void ReconcileGraph(GraphTextureId screenTarget, WorldCanvasTarget worldTarget)
    {
        DevelopmentCheck.Require(!_disposed, "Canvas drawing must be active.");
        _canvasData.Clear();
        _canvasEntities.Clear();
        var receiver = (Renderer: this, Screen: screenTarget, World: worldTarget);
        _world.ExecuteQuery(_query, _lastVersion, ref receiver, static (rows, ref renderer) =>
        {
            foreach (QueryChunkView chunk in rows.Chunks)
            {
                ReadOnlySpan<RenderCanvas> values = chunk.Read<RenderCanvas>();
                for (int row = 0; row < chunk.Count; row++)
                {
                    RenderCanvas canvas = values[row];
                    if (canvas.Target.Kind == CanvasTargetKind.RenderTexture) continue;
                    bool worldSpace = canvas.Target.Kind == CanvasTargetKind.WorldSpace;
                    GraphTextureId color = worldSpace ? renderer.World.Color : renderer.Screen;
                    GraphTextureId depth = worldSpace && canvas.DepthTest ? renderer.World.Depth : default;
                    DevelopmentCheck.Require(color != default, "Canvas drawing requires its color target.");
                    DevelopmentCheck.Require(!worldSpace || !canvas.DepthTest || depth != default,
                        "World canvas depth testing requires the scene depth target.");
                    Entity entity = chunk.Entities[row];
                    renderer.Renderer._canvasEntities.Add(entity);
                    bool text = chunk.HasBuffer<GlyphInstance>() && chunk.Has<CanvasGlyphAtlas>();
                    renderer.Renderer._canvasData.Add(new(entity, canvas,
                        chunk.RowBufferChangedSinceLastSystemVersion<SurfaceInstance>(row),
                        text && chunk.RowBufferChangedSinceLastSystemVersion<GlyphInstance>(row),
                        text ? chunk.Read<CanvasGlyphAtlas>()[row].Atlas : Entity.Null,
                        new(color, depth, depth == default ? default : renderer.World.DepthFormat,
                            depth == default ? default : renderer.World.DepthComparison, worldSpace)));
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
                        additions.Add(data.Entity, _drawing.AddPass(edit, data.Target, _glyphDrawing is not null));
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
                if (_canvases[entity].Instances is { } buffer) _graph.RetireAfterSubmittedFrames(buffer);
                if (_canvases[entity].GlyphInstances is { } glyphBuffer) _graph.RetireAfterSubmittedFrames(glyphBuffer);
                _canvases.Remove(entity);
            }
            foreach ((Entity entity, CanvasBuffers buffers) in additions) _canvases.Add(entity, buffers);
        }
        _view = worldTarget.View;
        foreach (CanvasData data in _canvasData)
        {
            CanvasBuffers buffers = _canvases[data.Entity];
            buffers.Canvas = data.Canvas;
            buffers.InstancesChanged |= data.InstancesChanged;
            buffers.GlyphsChanged |= data.GlyphsChanged || buffers.Atlas != data.Atlas;
            buffers.Atlas = data.Atlas;
            if (data.Atlas == Entity.Null && buffers.GlyphInstances is { } glyphBuffer)
            {
                _graph.RetireAfterSubmittedFrames(glyphBuffer);
                buffers.GlyphInstances = null;
                buffers.GlyphOrders = [];
            }
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

    /// <summary>Binds canvas values and updates changed instance buffers within an existing graph frame.</summary>
    public void UpdateFrame(ref RenderGraphFrame frame)
    {
        DevelopmentCheck.Require(!_disposed, "Canvas drawing must be active.");
        frame.ObserveExecution(this);
        var atlasFrames = new Dictionary<Entity, GlyphAtlasFrame>();
        if (_glyphDrawing is not null)
            foreach (CanvasBuffers buffers in _canvases.Values)
            {
                if (buffers.Atlas == Entity.Null || atlasFrames.ContainsKey(buffers.Atlas)) continue;
                if (!_atlases.TryGetValue(buffers.Atlas, out GlyphAtlasTextures? atlas))
                    _atlases.Add(buffers.Atlas, atlas = new GlyphAtlasTextures(_backend, _device, _graph));
                atlasFrames.Add(buffers.Atlas, atlas.UpdateFrame(ref frame, _world.Read<RenderGlyphAtlas>(buffers.Atlas)));
            }
        foreach ((Entity entity, CanvasBuffers buffers) in _canvases)
        {
            bool orderChanged = buffers.InstancesChanged || buffers.GlyphsChanged;
            if (buffers.InstancesChanged)
            {
                CanvasBuffers state = buffers;
                _world.ExecuteBufferRead<SurfaceInstance, CanvasBuffers>(entity, ref state,
                    (BufferView<SurfaceInstance> instances, ref CanvasBuffers data) =>
                    {
                        Buffer replacement = _drawing.CreateInstances(instances.AsSpan());
                        if (data.Instances is { } previous) _graph.RetireAfterSubmittedFrames(previous);
                        data.Instances = replacement;
                        data.Boundaries = [new(BufferRange.Whole, replacement.InitialSync,
                            replacement.InitialAccess, ResourceContentState.Defined)];
                        data.Count = instances.Count;
                        data.SurfaceOrders = instances.AsSpan().ToArray().Select(value => value.DrawOrder).ToArray();
                    });
                buffers.InstancesChanged = false;
            }
            Buffer buffer = buffers.Instances!;
            GraphBufferId surfaceBuffer = frame.Import(buffer, buffers.Boundaries);
            frame.SetBufferInput(buffers.Input, new GraphBufferSlice(surfaceBuffer, BufferRange.Whole));
            GlyphPassData? glyphData = null;
            CanvasDraw[]? drawData = null;
            if (_glyphDrawing is not null && buffers.Atlas != Entity.Null)
            {
                if (buffers.GlyphsChanged)
                {
                    GlyphInstance[] values = [];
                    _world.ExecuteBufferRead<GlyphInstance>(entity, glyphs => values = glyphs.AsSpan().ToArray());
                    Buffer replacement = _glyphDrawing.CreateInstances(values);
                    if (buffers.GlyphInstances is { } previous) _graph.RetireAfterSubmittedFrames(previous);
                    buffers.GlyphInstances = replacement;
                    buffers.GlyphBoundaries = [new(BufferRange.Whole, replacement.InitialSync,
                        replacement.InitialAccess, ResourceContentState.Defined)];
                    buffers.GlyphOrders = values.Select(value => (value.DrawOrder, value.PageIndex)).ToArray();
                    buffers.GlyphsChanged = false;
                }
                GlyphAtlasFrame atlas = atlasFrames[buffers.Atlas];
                Buffer glyphBuffer = buffers.GlyphInstances!;
                GraphBufferId glyphSource = frame.Import(glyphBuffer, buffers.GlyphBoundaries);
                frame.SetBufferInput(buffers.GlyphInput!, new GraphBufferSlice(glyphSource, BufferRange.Whole));
                foreach (GraphPassId upload in atlas.Uploads) frame.OrderAfter(buffers.Pass, upload);
                glyphData = new(_glyphDrawing.GetPipeline(buffers.Target), _glyphDrawing.Parameters, _glyphDrawing.Sampler,
                    buffers.GlyphView, buffers.CanvasBuffer, atlas.Pages, atlas.DistanceSpread);
                PassDefinition pass = frame.GetPass(buffers.Pass);
                pass.Read(buffers.GlyphView, PipelineSync.VertexShading | PipelineSync.PixelShading);
                foreach (GraphTextureSrvId page in atlas.Pages) pass.Read(page, PipelineSync.PixelShading);
                buffers.DistanceSpread = atlas.DistanceSpread;
                if (orderChanged) buffers.Draws = BuildDraws(buffers.SurfaceOrders, buffers.GlyphOrders);
                drawData = new CanvasDraw[buffers.Draws.Length];
                for (int index = 0; index < drawData.Length; index++)
                {
                    CanvasDraw draw = buffers.Draws[index];
                    GraphBufferSrvId view = draw.PageIndex < 0
                        ? _drawing.CreateDrawView(ref frame, surfaceBuffer, draw)
                        : _glyphDrawing.CreateDrawView(ref frame, glyphSource, draw);
                    pass.Read(view, PipelineSync.VertexShading | PipelineSync.PixelShading);
                    drawData[index] = draw with { Instances = view };
                }
            }
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
            _drawing.SetCanvasData(ref frame, buffers, transform);
            frame.SetPassDataIfChanged(buffers.Pass, buffers.Data with
            {
                Width = width,
                Height = height,
                Count = buffers.Count,
                CanvasBuffer = buffers.CanvasBuffer,
                Glyphs = glyphData,
                Draws = drawData,
            });
            frame.SetPassEnabled(buffers.Pass, (buffers.Count > 0 || glyphData is not null && buffers.GlyphOrders.Length > 0)
                && width > 0 && height > 0);
        }
    }

    void IRenderGraphFrameExecutionObserver.OnRenderGraphFrameFinished(RenderGraphFrameResult result)
    {
        foreach (CanvasBuffers buffers in _canvases.Values)
        {
            if (buffers.GlyphInstances is { } glyphs)
            {
                ReadOnlySpan<BufferBoundaryState> glyphStates = result.GetBufferStates(glyphs);
                if (!glyphStates.IsEmpty) buffers.GlyphBoundaries = glyphStates.ToArray();
            }
            if (buffers.Instances is not { } buffer) continue;
            ReadOnlySpan<BufferBoundaryState> states = result.GetBufferStates(buffer);
            if (states.IsEmpty) continue;
            if (buffers.Boundaries.Length != states.Length) buffers.Boundaries = new BufferBoundaryState[states.Length];
            states.CopyTo(buffers.Boundaries);
        }
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
        foreach (CanvasBuffers buffers in _canvases.Values)
        {
            if (buffers.Instances is { } buffer) _graph.RetireAfterSubmittedFrames(buffer);
            if (buffers.GlyphInstances is { } glyphs) _graph.RetireAfterSubmittedFrames(glyphs);
        }
        _canvases.Clear();
        _world.ReleaseQuery(_query);
        _graph.RetireAfterSubmittedFrames(_drawing);
        if (_glyphDrawing is not null) _graph.RetireAfterSubmittedFrames(_glyphDrawing);
        foreach (GlyphAtlasTextures atlas in _atlases.Values) _graph.RetireAfterSubmittedFrames(atlas);
        _atlases.Clear();
        _disposed = true;
    }

    private static void RemovePass(RenderGraphEdit edit, CanvasBuffers buffers)
    {
        edit.Remove(buffers.Pass);
        edit.Remove(buffers.InstancesView);
        edit.Remove(buffers.CanvasView);
        edit.Remove(buffers.CanvasBuffer);
        edit.Remove(buffers.TargetView);
        if (buffers.DepthView != default) edit.Remove(buffers.DepthView);
        if (buffers.GlyphInput is not null) edit.Remove(buffers.GlyphView);
    }

    private static CanvasDraw[] BuildDraws(int[] surfaces, (int Order, int Page)[] glyphs)
    {
        var draws = new List<CanvasDraw>();
        int surface = 0, glyph = 0;
        while (surface < surfaces.Length || glyph < glyphs.Length)
        {
            bool drawSurface = surface < surfaces.Length && (glyph >= glyphs.Length || surfaces[surface] <= glyphs[glyph].Order);
            int first = drawSurface ? surface++ : glyph++;
            int page = drawSurface ? -1 : glyphs[first].Page;
            if (draws.Count > 0 && draws[^1].PageIndex == page && draws[^1].First + draws[^1].Count == first)
                draws[^1] = draws[^1] with { Count = draws[^1].Count + 1 };
            else draws.Add(new(first, 1, page));
        }
        return draws.ToArray();
    }

    private readonly record struct CanvasData(Entity Entity, RenderCanvas Canvas, bool InstancesChanged,
        bool GlyphsChanged, Entity Atlas, CanvasPassTarget Target);
}

internal sealed class CanvasBuffers(GraphPassId pass, GraphBufferInput input,
    GraphBufferSrvId instancesView, GraphBufferId canvasBuffer, GraphBufferCbvId canvasView,
    GraphColorAttachmentViewId targetView, GraphDepthStencilViewId depthView, CanvasPassTarget target, CanvasPassData data)
{
    internal GraphPassId Pass { get; } = pass;
    internal GraphBufferInput Input { get; } = input;
    internal GraphBufferSrvId InstancesView { get; } = instancesView;
    internal GraphBufferId CanvasBuffer { get; } = canvasBuffer;
    internal GraphBufferCbvId CanvasView { get; } = canvasView;
    internal GraphColorAttachmentViewId TargetView { get; } = targetView;
    internal GraphDepthStencilViewId DepthView { get; } = depthView;
    internal CanvasPassTarget Target { get; } = target;
    internal CanvasPassData Data { get; } = data;
    internal RenderCanvas Canvas { get; set; }
    internal Buffer? Instances { get; set; }
    internal BufferBoundaryState[] Boundaries { get; set; } = [];
    internal int Count { get; set; }
    internal bool InstancesChanged { get; set; } = true;
    internal GraphBufferInput? GlyphInput { get; set; }
    internal GraphBufferSrvId GlyphView { get; set; }
    internal Entity Atlas { get; set; }
    internal Buffer? GlyphInstances { get; set; }
    internal BufferBoundaryState[] GlyphBoundaries { get; set; } = [];
    internal bool GlyphsChanged { get; set; } = true;
    internal int DistanceSpread { get; set; }
    internal int[] SurfaceOrders { get; set; } = [];
    internal (int Order, int Page)[] GlyphOrders { get; set; } = [];
    internal CanvasDraw[] Draws { get; set; } = [];
}
