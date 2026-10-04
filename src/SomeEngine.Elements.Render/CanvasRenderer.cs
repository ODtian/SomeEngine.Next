using System.Numerics;
using SomeEngine.Core.Diagnostics;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS;
using SomeEngine.ECS.Queries;
using SomeEngine.Graphics;
using SomeEngine.Layout;
using SomeEngine.Render.Systems;
using SomeEngine.RenderGraph;
using Buffer = SomeEngine.Graphics.Buffer;

namespace SomeEngine.Elements.Render;

/// <summary>Maintains one persistent raster pass per extracted screen canvas.</summary>
public sealed class CanvasRenderer : IDisposable, IRenderGraphFrameExecutionObserver
{
    private readonly global::SomeEngine.RenderGraph.RenderGraph _graph;
    private readonly RenderWorld _world;
    private readonly SurfaceDrawing _drawing;
    private readonly QueryHandle _query;
    private readonly Dictionary<Entity, CanvasBuffers> _canvases = [];
    private readonly List<CanvasData> _canvasData = [];
    private readonly HashSet<Entity> _canvasEntities = [];
    private GraphTextureId _target;
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

    /// <summary>Explicitly adds/removes screen canvas passes or reconnects their color target.</summary>
    public void ReconcileGraph(GraphTextureId screenTarget)
    {
        DevelopmentCheck.Require(!_disposed, "Canvas drawing must be active.");
        _canvasData.Clear();
        _canvasEntities.Clear();
        CanvasRenderer receiver = this;
        _world.ExecuteQuery(_query, _lastVersion, ref receiver, static (rows, ref renderer) =>
        {
            foreach (QueryChunkView chunk in rows.Chunks)
            {
                ReadOnlySpan<RenderCanvas> values = chunk.Read<RenderCanvas>();
                for (int row = 0; row < chunk.Count; row++)
                {
                    if (values[row].Target.Kind != CanvasTargetKind.Screen) continue;
                    Entity entity = chunk.Entities[row];
                    renderer._canvasEntities.Add(entity);
                    renderer._canvasData.Add(new(entity, values[row], chunk.RowBufferChangedSinceLastSystemVersion<SurfaceInstance>(row)));
                }
            }
        });
        bool targetChanged = _target != screenTarget;
        bool editRequired = targetChanged && _canvases.Count != 0;
        foreach (Entity entity in _canvases.Keys) editRequired |= !_canvasEntities.Contains(entity);
        foreach (CanvasData data in _canvasData) editRequired |= !_canvases.ContainsKey(data.Entity);
        if (editRequired)
        {
            var additions = new Dictionary<Entity, CanvasBuffers>();
            var removals = new List<Entity>();
            using (RenderGraphEdit edit = _graph.BeginEdit())
            {
                foreach ((Entity entity, CanvasBuffers buffers) in _canvases)
                    if (targetChanged || !_canvasEntities.Contains(entity))
                    {
                        RemovePass(edit, buffers);
                        removals.Add(entity);
                    }
                foreach (CanvasData data in _canvasData)
                    if (targetChanged || !_canvases.ContainsKey(data.Entity))
                        additions.Add(data.Entity, _drawing.AddPass(edit, screenTarget));
                edit.Commit();
            }
            foreach (Entity entity in removals)
            {
                if (_canvases[entity].Instances is { } buffer) _graph.RetireAfterSubmittedFrames(buffer);
                _canvases.Remove(entity);
            }
            foreach ((Entity entity, CanvasBuffers buffers) in additions) _canvases.Add(entity, buffers);
        }
        _target = screenTarget;
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
            Vector4 canvas = new(buffers.Canvas.Size.Width, buffers.Canvas.Size.Height, buffers.Canvas.Scale, 0);
            _drawing.SetCanvasData(ref frame, buffers);
            frame.SetPassDataIfChanged(buffers.Pass, buffers.Data with
            {
                Width = (int)buffers.Canvas.Size.Width,
                Height = (int)buffers.Canvas.Size.Height,
                Count = buffers.Count,
            });
            frame.SetPassEnabled(buffers.Pass, buffers.Count > 0 && canvas.X > 0 && canvas.Y > 0);
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
    }

    private readonly record struct CanvasData(Entity Entity, RenderCanvas Canvas, bool InstancesChanged);
}

internal sealed class CanvasBuffers(GraphPassId pass, GraphBufferInput input,
    GraphBufferSrvId instancesView, GraphBufferId canvasBuffer, GraphBufferCbvId canvasView,
    GraphColorAttachmentViewId targetView, SurfacePassData data)
{
    internal GraphPassId Pass { get; } = pass;
    internal GraphBufferInput Input { get; } = input;
    internal GraphBufferSrvId InstancesView { get; } = instancesView;
    internal GraphBufferId CanvasBuffer { get; } = canvasBuffer;
    internal GraphBufferCbvId CanvasView { get; } = canvasView;
    internal GraphColorAttachmentViewId TargetView { get; } = targetView;
    internal SurfacePassData Data { get; } = data;
    internal RenderCanvas Canvas { get; set; }
    internal Buffer? Instances { get; set; }
    internal BufferBoundaryState[] Boundaries { get; set; } = [];
    internal int Count { get; set; }
    internal bool InstancesChanged { get; set; } = true;
}
