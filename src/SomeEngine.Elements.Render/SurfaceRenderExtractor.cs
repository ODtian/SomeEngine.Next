using SomeEngine.Core.Math;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Hierarchy;
using SomeEngine.ECS.Queries;
using SomeEngine.Layout;
using SomeEngine.Render.Systems;
using SomeEngine.Views;

namespace SomeEngine.Elements.Render;

/// <summary>Collects surfaces for each canvas through the shared extraction stage.</summary>
public sealed class SurfaceRenderExtractor : IRenderExtractionSystem
{
    private readonly HashSet<Entity> _changedTreeNodes = [];
    private readonly HashSet<Entity> _changedSurfaceNodes = [];
    private readonly Dictionary<Entity, CanvasSurfaces> _canvasSurfaces = [];
    private readonly Dictionary<Entity, Dictionary<Entity, SurfaceValue>> _surfaceValuesByCanvas = [];

    public void DeclareReads(RenderExtractionQuery query)
    {
        query.ReadOptional<Canvas>();
        query.ReadOptional<Surface>();
        query.ReadOptional<LayoutRect>();
        query.ReadOptional<Parent<ElementTree>>();
        query.ReadOptional<Children<ElementTree>>();
        query.Write<RenderCanvas>();
    }

    public void Reset()
    {
        _changedTreeNodes.Clear();
        _changedSurfaceNodes.Clear();
        _canvasSurfaces.Clear();
        _surfaceValuesByCanvas.Clear();
    }

    public void Collect(QueryChunkView chunk)
    {
        if (chunk.Has<Canvas>() || chunk.Has<Surface>() || chunk.Has<LayoutRect>() ||
            chunk.Has<Parent<ElementTree>>() || chunk.Has<Children<ElementTree>>())
            foreach (Entity entity in chunk.Entities) _changedTreeNodes.Add(entity);
    }

    public bool CollectChanges(QueryChunkView chunk, RenderExtractionContext context)
    {
        bool surfaceChanged = chunk.Has<Surface>() && chunk.Has<LayoutRect>() &&
            chunk.HasChangedSinceLastSystemVersion<Surface>();
        bool rectangleChanged = chunk.Has<Surface>() && chunk.Has<LayoutRect>() &&
            chunk.HasChangedSinceLastSystemVersion<LayoutRect>();
        bool canvasChanged = chunk.Has<Canvas>() && chunk.HasChangedSinceLastSystemVersion<Canvas>();
        bool parentChanged = chunk.Has<Parent<ElementTree>>() &&
            chunk.HasChangedSinceLastSystemVersion<Parent<ElementTree>>();
        bool childrenChanged = chunk.Has<Children<ElementTree>>() &&
            chunk.HasChangedSinceLastSystemVersion<Children<ElementTree>>();
        if (!surfaceChanged && !rectangleChanged && !canvasChanged && !parentChanged && !childrenChanged)
            return false;

        for (int row = 0; row < chunk.Count; row++)
        {
            Entity entity = chunk.Entities[row];
            if ((canvasChanged && chunk.RowChangedSinceLastSystemVersion<Canvas>(row)) ||
                (parentChanged && chunk.RowChangedSinceLastSystemVersion<Parent<ElementTree>>(row)) ||
                (childrenChanged && chunk.RowChangedSinceLastSystemVersion<Children<ElementTree>>(row)))
                _changedTreeNodes.Add(entity);
            if ((surfaceChanged && chunk.RowChangedSinceLastSystemVersion<Surface>(row)) ||
                (rectangleChanged && chunk.RowChangedSinceLastSystemVersion<LayoutRect>(row)))
                _changedSurfaceNodes.Add(entity);
        }
        // Source collection resolves the canvas and supplies the detached drawing values.
        return false;
    }

    public bool CompleteSourceCollection(World sourceWorld, bool fullCollection)
    {
        foreach (Entity node in _changedTreeNodes)
        {
            Entity canvas = FindCanvas(sourceWorld, node);
            if (canvas != Entity.Null && !_canvasSurfaces.ContainsKey(canvas))
                _canvasSurfaces.Add(canvas, CollectCanvas(sourceWorld, canvas));
        }
        foreach (Entity node in _changedSurfaceNodes)
        {
            Entity canvas = FindCanvas(sourceWorld, node);
            if (canvas == Entity.Null || _canvasSurfaces.ContainsKey(canvas)) continue;
            if (!_surfaceValuesByCanvas.TryGetValue(canvas, out var surfaces))
                _surfaceValuesByCanvas.Add(canvas, surfaces = []);
            surfaces.Add(node, ReadSurface(sourceWorld, node));
        }
        return _canvasSurfaces.Count != 0 || _surfaceValuesByCanvas.Count != 0;
    }

    private static Entity FindCanvas(World world, Entity node)
    {
        while (node != Entity.Null)
        {
            if (world.Has<Canvas>(node)) return node;
            if (!world.Has<Parent<ElementTree>>(node)) return Entity.Null;
            node = world.Read<Parent<ElementTree>>(node).Value;
        }
        return Entity.Null;
    }

    private static CanvasSurfaces CollectCanvas(World world, Entity canvasEntity)
    {
        Canvas canvas = world.Read<Canvas>(canvasEntity);
        var clip = new Rect(0, 0, canvas.Size.Width / canvas.Scale, canvas.Size.Height / canvas.Scale);
        var surfaces = new List<SurfaceInstance>();
        var nodes = new Stack<Entity>();
        nodes.Push(canvasEntity);
        int drawOrder = 0;
        while (nodes.TryPop(out Entity node))
        {
            if (node != canvasEntity && world.Has<Canvas>(node)) continue;
            if (world.Has<Surface>(node) && world.Has<LayoutRect>(node))
            {
                SurfaceValue value = ReadSurface(world, node);
                surfaces.Add(new(node, value.Rectangle, value.Fill, value.CornerRadius,
                    value.BorderColor, value.BorderWidth, clip, drawOrder, 0));
            }
            drawOrder++;
            var children = Hierarchy<ElementTree>.GetChildren(world, node);
            for (int index = children.Count - 1; index >= 0; index--) nodes.Push(children[index]);
        }
        return new(new RenderCanvas(canvas.Target, canvas.Size, canvas.Scale), surfaces);
    }

    private static SurfaceValue ReadSurface(World world, Entity node)
    {
        Surface surface = world.Read<Surface>(node);
        return new(world.Read<LayoutRect>(node).Value, surface.Fill, surface.CornerRadius,
            surface.BorderColor, surface.BorderWidth);
    }

    public void Apply(RenderExtractionContext context)
    {
        foreach ((Entity source, CanvasSurfaces canvas) in _canvasSurfaces)
        {
            Entity entity = context.RetainMirror(source);
            context.Upsert(entity, canvas.Canvas);
            if (!context.World.HasBuffer<SurfaceInstance>(entity)) context.World.AddBuffer<SurfaceInstance>(entity);
            UpdateInstances(context.World, entity, canvas.Instances);
        }
        foreach (RenderMirror mirror in context.Mirrors)
        {
            if (_canvasSurfaces.ContainsKey(mirror.Source)) continue;
            context.RemoveIfExists<RenderCanvas>(mirror.RenderEntity);
            if (context.World.HasBuffer<SurfaceInstance>(mirror.RenderEntity))
                context.World.RemoveBuffer<SurfaceInstance>(mirror.RenderEntity);
        }
    }

    public void ApplyChanges(RenderExtractionContext context)
    {
        foreach ((Entity source, CanvasSurfaces canvas) in _canvasSurfaces)
        {
            Entity entity = context.RequireMirror(source);
            context.UpdateExisting(entity, canvas.Canvas);
            UpdateInstances(context.World, entity, canvas.Instances);
        }
        foreach ((Entity source, Dictionary<Entity, SurfaceValue> values) in _surfaceValuesByCanvas)
        {
            Entity entity = context.RequireMirror(source);
            context.World.ExecuteBufferWrite<SurfaceInstance>(entity, buffer =>
            {
                for (int index = 0; index < buffer.Count; index++)
                {
                    SurfaceInstance before = buffer.Read(index);
                    if (!values.TryGetValue(before.Node, out SurfaceValue value)) continue;
                    SurfaceInstance after = before with
                    {
                        Rectangle = value.Rectangle, Fill = value.Fill, CornerRadius = value.CornerRadius,
                        BorderColor = value.BorderColor, BorderWidth = value.BorderWidth,
                    };
                    if (before != after) buffer[index] = after with { Version = unchecked(before.Version + 1) };
                }
            });
        }
    }

    private static void UpdateInstances(RenderWorld world, Entity canvas, List<SurfaceInstance> instances)
    {
        var previous = new Dictionary<Entity, SurfaceInstance>();
        world.ExecuteBufferRead<SurfaceInstance>(canvas, buffer =>
        {
            foreach (SurfaceInstance instance in buffer.AsSpan()) previous.Add(instance.Node, instance);
        });
        for (int index = 0; index < instances.Count; index++)
        {
            SurfaceInstance value = instances[index];
            if (previous.TryGetValue(value.Node, out SurfaceInstance before))
            {
                value = value with { Version = before.Version };
                if (value != before) value = value with { Version = unchecked(before.Version + 1) };
            }
            else value = value with { Version = 1 };
            instances[index] = value;
        }
        world.ExecuteBufferWrite<SurfaceInstance>(canvas, buffer =>
        {
            for (int index = 0; index < instances.Count; index++)
            {
                SurfaceInstance value = instances[index];
                if (index >= buffer.Count) buffer.Add(value);
                else if (buffer.Read(index) != value) buffer[index] = value;
            }
            while (buffer.Count > instances.Count) buffer.SwapRemoveAt(buffer.Count - 1);
        });
    }

    private readonly record struct CanvasSurfaces(RenderCanvas Canvas, List<SurfaceInstance> Instances);
    private readonly record struct SurfaceValue(Rect Rectangle, Color Fill, float CornerRadius, Color BorderColor, float BorderWidth);
}
