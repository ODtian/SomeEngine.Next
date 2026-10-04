using SomeEngine.Core.Math;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Hierarchy;
using SomeEngine.ECS.Queries;
using SomeEngine.Layout;
using SomeEngine.Render.Systems;
using SomeEngine.Views;

namespace SomeEngine.Elements.Render;

/// <summary>Collects each canvas and its ordered drawing data through the shared extraction stage.</summary>
public class CanvasRenderExtractor : IRenderExtractionSystem
{
    private readonly HashSet<Entity> _changedTreeNodes = [];
    private readonly HashSet<Entity> _changedSurfaceNodes = [];
    private readonly Dictionary<Entity, CanvasInstances> _canvasInstances = [];
    private readonly Dictionary<Entity, Dictionary<Entity, SurfaceValue>> _surfaceValuesByCanvas = [];
    private readonly HashSet<Entity> _changedImageNodes = [];
    private readonly Dictionary<Entity, Dictionary<Entity, ImageInstance>> _imageValuesByCanvas = [];

    public void DeclareReads(RenderExtractionQuery query)
    {
        query.ReadOptional<Canvas>();
        query.ReadOptional<Surface>();
        query.ReadOptional<LayoutRect>();
        query.ReadOptional<Image>();
        query.ReadOptional<ImageTexture>();
        query.ReadOptional<Box>();
        query.ReadOptional<Parent<ElementTree>>();
        query.ReadOptional<Children<ElementTree>>();
        query.Write<RenderCanvas>();
    }

    public void Reset()
    {
        _changedTreeNodes.Clear();
        _changedSurfaceNodes.Clear();
        _canvasInstances.Clear();
        _surfaceValuesByCanvas.Clear();
        _changedImageNodes.Clear();
        _imageValuesByCanvas.Clear();
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
        bool imageChanged = chunk.Has<Image>() && chunk.Has<LayoutRect>() &&
            (chunk.HasChangedSinceLastSystemVersion<Image>() ||
             chunk.HasChangedSinceLastSystemVersion<LayoutRect>() ||
             (chunk.Has<ImageTexture>() && chunk.HasChangedSinceLastSystemVersion<ImageTexture>()) ||
             (chunk.Has<Box>() && chunk.HasChangedSinceLastSystemVersion<Box>()));
        if (!surfaceChanged && !rectangleChanged && !canvasChanged && !parentChanged && !childrenChanged && !imageChanged)
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
            if (imageChanged && (chunk.RowChangedSinceLastSystemVersion<Image>(row) ||
                chunk.RowChangedSinceLastSystemVersion<LayoutRect>(row) ||
                (chunk.Has<ImageTexture>() && chunk.RowChangedSinceLastSystemVersion<ImageTexture>(row)) ||
                (chunk.Has<Box>() && chunk.RowChangedSinceLastSystemVersion<Box>(row))))
                _changedImageNodes.Add(entity);
        }
        // Source collection resolves the canvas and supplies the detached drawing values.
        return false;
    }

    public bool CompleteSourceCollection(World sourceWorld, bool fullCollection)
    {
        foreach (Entity node in _changedTreeNodes)
        {
            Entity canvas = FindCanvas(sourceWorld, node);
            if (canvas != Entity.Null && !_canvasInstances.ContainsKey(canvas))
                _canvasInstances.Add(canvas, CollectCanvas(sourceWorld, canvas));
        }
        foreach (Entity node in _changedSurfaceNodes)
        {
            Entity canvas = FindCanvas(sourceWorld, node);
            if (canvas == Entity.Null || _canvasInstances.ContainsKey(canvas)) continue;
            if (!_surfaceValuesByCanvas.TryGetValue(canvas, out var surfaces))
                _surfaceValuesByCanvas.Add(canvas, surfaces = []);
            surfaces.Add(node, ReadSurface(sourceWorld, node));
        }
        foreach (Entity node in _changedImageNodes)
        {
            Entity canvas = FindCanvas(sourceWorld, node);
            if (canvas == Entity.Null || _canvasInstances.ContainsKey(canvas)) continue;
            if (!_imageValuesByCanvas.TryGetValue(canvas, out var images))
                _imageValuesByCanvas.Add(canvas, images = []);
            images.Add(node, ReadImage(sourceWorld, node));
        }
        return _canvasInstances.Count != 0 || _surfaceValuesByCanvas.Count != 0 || _imageValuesByCanvas.Count != 0;
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

    private static CanvasInstances CollectCanvas(World world, Entity canvasEntity)
    {
        Canvas canvas = world.Read<Canvas>(canvasEntity);
        var clip = new Rect(0, 0, canvas.Size.Width / canvas.Scale, canvas.Size.Height / canvas.Scale);
        var surfaces = new List<SurfaceInstance>();
        var images = new List<ImageInstance>();
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
            if (world.Has<Image>(node) && world.Has<LayoutRect>(node))
                images.Add(ReadImage(world, node) with { ClipRectangle = clip, DrawOrder = drawOrder });
            drawOrder++;
            var children = Hierarchy<ElementTree>.GetChildren(world, node);
            for (int index = children.Count - 1; index >= 0; index--) nodes.Push(children[index]);
        }
        return new(new RenderCanvas(canvas.Target, canvas.Size, canvas.Scale), surfaces, images);
    }

    private static SurfaceValue ReadSurface(World world, Entity node)
    {
        Surface surface = world.Read<Surface>(node);
        return new(world.Read<LayoutRect>(node).Value, surface.Fill, surface.CornerRadius,
            surface.BorderColor, surface.BorderWidth);
    }

    private static ImageInstance ReadImage(World world, Entity node)
    {
        Image image = world.Read<Image>(node);
        ImageTexture texture = world.Has<ImageTexture>(node) ? world.Read<ImageTexture>(node) :
            new(null, new Size(1, 1), 0);
        Rect rectangle = world.Read<LayoutRect>(node).Value;
        Thickness padding = world.Has<Box>(node) ? world.Read<Box>(node).Padding : default;
        rectangle = new(rectangle.X + padding.Left, rectangle.Y + padding.Top,
            MathF.Max(0, rectangle.Width - padding.Left - padding.Right),
            MathF.Max(0, rectangle.Height - padding.Top - padding.Bottom));
        Rect uv = new(0, 0, 1, 1);
        if (rectangle.Width > 0 && rectangle.Height > 0 && image.Fit != ImageFit.Stretch)
        {
            float scale = image.Fit == ImageFit.Contain ?
                MathF.Min(rectangle.Width / texture.Size.Width, rectangle.Height / texture.Size.Height) :
                MathF.Max(rectangle.Width / texture.Size.Width, rectangle.Height / texture.Size.Height);
            float width = texture.Size.Width * scale;
            float height = texture.Size.Height * scale;
            if (image.Fit == ImageFit.Contain)
                rectangle = new(rectangle.X + (rectangle.Width - width) * 0.5f,
                    rectangle.Y + (rectangle.Height - height) * 0.5f, width, height);
            else
                uv = new((1 - rectangle.Width / width) * 0.5f, (1 - rectangle.Height / height) * 0.5f,
                    rectangle.Width / width, rectangle.Height / height);
        }
        return new(node, texture.Value, rectangle, uv, default, 0, 0, texture.Revision);
    }

    public void Apply(RenderExtractionContext context)
    {
        foreach ((Entity source, CanvasInstances canvas) in _canvasInstances)
        {
            Entity entity = context.RetainMirror(source);
            context.Upsert(entity, canvas.Canvas);
            if (!context.World.HasBuffer<SurfaceInstance>(entity)) context.World.AddBuffer<SurfaceInstance>(entity);
            UpdateSurfaceInstances(context.World, entity, canvas.Surfaces);
            if (!context.World.HasBuffer<ImageInstance>(entity)) context.World.AddBuffer<ImageInstance>(entity);
            UpdateImageInstances(context.World, entity, canvas.Images);
        }
        foreach (RenderMirror mirror in context.Mirrors)
        {
            if (_canvasInstances.ContainsKey(mirror.Source)) continue;
            context.RemoveIfExists<RenderCanvas>(mirror.RenderEntity);
            if (context.World.HasBuffer<SurfaceInstance>(mirror.RenderEntity))
                context.World.RemoveBuffer<SurfaceInstance>(mirror.RenderEntity);
            if (context.World.HasBuffer<ImageInstance>(mirror.RenderEntity))
                context.World.RemoveBuffer<ImageInstance>(mirror.RenderEntity);
        }
    }

    public void ApplyChanges(RenderExtractionContext context)
    {
        foreach ((Entity source, CanvasInstances canvas) in _canvasInstances)
        {
            Entity entity = context.RequireMirror(source);
            context.UpdateExisting(entity, canvas.Canvas);
            UpdateSurfaceInstances(context.World, entity, canvas.Surfaces);
            UpdateImageInstances(context.World, entity, canvas.Images);
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
        foreach ((Entity source, Dictionary<Entity, ImageInstance> values) in _imageValuesByCanvas)
        {
            Entity entity = context.RequireMirror(source);
            context.World.ExecuteBufferWrite<ImageInstance>(entity, buffer =>
            {
                for (int index = 0; index < buffer.Count; index++)
                {
                    ImageInstance before = buffer.Read(index);
                    if (!values.TryGetValue(before.Node, out ImageInstance value)) continue;
                    ImageInstance after = value with
                    {
                        ClipRectangle = before.ClipRectangle, DrawOrder = before.DrawOrder, Version = before.Version,
                    };
                    if (before != after) buffer[index] = after with { Version = unchecked(before.Version + 1) };
                }
            });
        }
    }

    private static void UpdateSurfaceInstances(RenderWorld world, Entity canvas, List<SurfaceInstance> instances)
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

    private static void UpdateImageInstances(RenderWorld world, Entity canvas, List<ImageInstance> instances)
    {
        var previous = new Dictionary<Entity, ImageInstance>();
        world.ExecuteBufferRead<ImageInstance>(canvas, buffer =>
        {
            foreach (ImageInstance instance in buffer.AsSpan()) previous.Add(instance.Node, instance);
        });
        for (int index = 0; index < instances.Count; index++)
        {
            ImageInstance value = instances[index];
            if (previous.TryGetValue(value.Node, out ImageInstance before))
            {
                value = value with { Version = before.Version };
                if (value != before) value = value with { Version = unchecked(before.Version + 1) };
            }
            else value = value with { Version = 1 };
            instances[index] = value;
        }
        world.ExecuteBufferWrite<ImageInstance>(canvas, buffer =>
        {
            for (int index = 0; index < instances.Count; index++)
            {
                ImageInstance value = instances[index];
                if (index >= buffer.Count) buffer.Add(value);
                else if (buffer.Read(index) != value) buffer[index] = value;
            }
            while (buffer.Count > instances.Count) buffer.SwapRemoveAt(buffer.Count - 1);
        });
    }

    private readonly record struct CanvasInstances(RenderCanvas Canvas, List<SurfaceInstance> Surfaces, List<ImageInstance> Images);
    private readonly record struct SurfaceValue(Rect Rectangle, Color Fill, float CornerRadius, Color BorderColor, float BorderWidth);
}
