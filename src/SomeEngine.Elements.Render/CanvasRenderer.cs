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
    {
        DevelopmentCheck.Require(ReferenceEquals(graph.Device, device), "Canvas drawing uses the graph's device.");
        _graph = graph;
        _world = world;
        _drawing = new SurfaceDrawing(backend, device, material, outputFormat);
        _query = world.Query(new QueryDefinitionBuilder().Read<RenderCanvas>().ReadBuffer<SurfaceInstance>());
    }

    /// <summary>Explicitly maintains canvas passes, their targets, and depth testing.</summary>
    public void ReconcileGraph(GraphTextureId screenTarget, WorldCanvasTarget worldTarget = default)
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
                    renderer.Renderer._canvasData.Add(new(entity, canvas,
                        chunk.RowBufferChangedSinceLastSystemVersion<SurfaceInstance>(row),
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
                        additions.Add(data.Entity, _drawing.AddPass(edit, data.Target));
                    }
                // Shared color targets compose world canvases before the screen overlay.
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
        foreach ((Entity entity, CanvasBuffers buffers) in _canvases)
        {
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
                    });
                buffers.InstancesChanged = false;
            }
            Buffer buffer = buffers.Instances!;
            frame.SetBufferInput(buffers.Input, new GraphBufferSlice(frame.Import(buffer,
                buffers.Boundaries), BufferRange.Whole));
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
            });
            frame.SetPassEnabled(buffers.Pass, buffers.Count > 0 && width > 0 && height > 0);
        }
    }

    void IRenderGraphFrameExecutionObserver.OnRenderGraphFrameFinished(RenderGraphFrameResult result)
    {
        foreach (CanvasBuffers buffers in _canvases.Values)
        {
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
            if (buffers.Instances is { } buffer) _graph.RetireAfterSubmittedFrames(buffer);
        _canvases.Clear();
        _world.ReleaseQuery(_query);
        _graph.RetireAfterSubmittedFrames(_drawing);
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
    }

    private readonly record struct CanvasData(Entity Entity, RenderCanvas Canvas, bool InstancesChanged, CanvasPassTarget Target);
}

internal sealed class CanvasBuffers(GraphPassId pass, GraphBufferInput input,
    GraphBufferSrvId instancesView, GraphBufferId canvasBuffer, GraphBufferCbvId canvasView,
    GraphColorAttachmentViewId targetView, GraphDepthStencilViewId depthView, CanvasPassTarget target, SurfacePassData data)
{
    internal GraphPassId Pass { get; } = pass;
    internal GraphBufferInput Input { get; } = input;
    internal GraphBufferSrvId InstancesView { get; } = instancesView;
    internal GraphBufferId CanvasBuffer { get; } = canvasBuffer;
    internal GraphBufferCbvId CanvasView { get; } = canvasView;
    internal GraphColorAttachmentViewId TargetView { get; } = targetView;
    internal GraphDepthStencilViewId DepthView { get; } = depthView;
    internal CanvasPassTarget Target { get; } = target;
    internal SurfacePassData Data { get; } = data;
    internal RenderCanvas Canvas { get; set; }
    internal Buffer? Instances { get; set; }
    internal BufferBoundaryState[] Boundaries { get; set; } = [];
    internal int Count { get; set; }
    internal bool InstancesChanged { get; set; } = true;
}
