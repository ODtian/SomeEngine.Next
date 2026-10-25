using System.Numerics;
using SomeEngine.Core.Diagnostics;
using SomeEngine.Core.ECS.Components;
using SomeEngine.Core.Math;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Hierarchy;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Systems;
using SomeEngine.Layout;
using SomeEngine.Render.Systems;
using SomeEngine.Views;
using SomeEngine.Job;

namespace SomeEngine.Elements.Render;

/// <summary>Collects each canvas and its ordered drawing data through the shared extraction stage.</summary>
public partial class CanvasRenderExtractor : IRenderExtractionSystem
{
    private readonly CanvasChanges _changes = new();
    private readonly Dictionary<Entity, CanvasInstances> _canvasInstances = [];
    private readonly Dictionary<Entity, Dictionary<Entity, SurfaceValue>> _surfaceValuesByCanvas = [];
    private readonly Dictionary<Entity, Dictionary<Entity, ImageInstance>> _imageValuesByCanvas = [];
    private readonly Dictionary<Entity, Entity> _worldTargets = [];
    private readonly Dictionary<Entity, RenderCanvas> _canvasChanges = [];
    private readonly List<DrawingData> _drawings = [];

    /// <summary>Computes detached visual instances with chunk jobs.</summary>
    public bool Parallel { get; set; } = true;

    public void DeclareReads(RenderExtractionQuery query)
    {
        query.ReadOptional<Canvas>();
        query.ReadOptional<WorldTransform>();
        query.ReadOptional<Surface>();
        query.ReadOptional<LayoutRect>();
        query.ReadOptional<ElementTransform>();
        query.ReadOptional<PresentationOffset>();
        query.ReadOptional<ScrollOffset>();
        query.ReadOptional<Image>();
        query.ReadOptional<ImageTexture>();
        query.ReadOptional<Box>();
        query.ReadOptional<Parent<ElementTree>>();
        query.ReadOptional<Children<ElementTree>>();
        query.Write<RenderCanvas>();
    }

    public void Reset()
    {
        _canvasInstances.Clear();
        _surfaceValuesByCanvas.Clear();
        _changes.Reset();
        _imageValuesByCanvas.Clear();
        _canvasChanges.Clear();
        _drawings.Clear();
    }

    public void Collect(QueryChunkView chunk)
    {
        _changes.Collect(ReadOnlyQueryPacketJobs.Borrow(chunk), chunk.LastSystemVersion, fullCollection: true);
    }

    public bool CollectChanges(QueryChunkView chunk, RenderExtractionContext context)
    {
        _changes.Collect(ReadOnlyQueryPacketJobs.Borrow(chunk), chunk.LastSystemVersion, fullCollection: false);
        return false;
    }

    public bool CompleteSourceCollection(World sourceWorld, bool fullCollection)
    {
        if (fullCollection) _worldTargets.Clear();
        foreach (Entity node in _changes.TreeNodes)
        {
            Entity canvas = CanvasTree.FindCanvas(sourceWorld, node);
            if (canvas != Entity.Null && !_canvasInstances.ContainsKey(canvas))
                _canvasInstances.Add(canvas, CollectCanvas(sourceWorld, canvas));
        }
        foreach (Entity node in _changes.SurfaceNodes)
        {
            Entity canvas = CanvasTree.FindCanvas(sourceWorld, node);
            if (canvas == Entity.Null || _canvasInstances.ContainsKey(canvas)) continue;
            _drawings.Add(new(canvas, node) { Surface = ReadSurface(sourceWorld, node), HasSurface = true });
        }
        foreach (Entity node in _changes.ImageNodes)
        {
            Entity canvas = CanvasTree.FindCanvas(sourceWorld, node);
            if (canvas == Entity.Null || _canvasInstances.ContainsKey(canvas)) continue;
            _drawings.Add(new(canvas, node) { Image = ReadImage(sourceWorld, node), HasImage = true });
        }
        foreach ((Entity canvas, Entity target) in _worldTargets)
            if (_changes.Targets.Contains(target) && !_canvasInstances.ContainsKey(canvas))
                _canvasChanges.Add(canvas, ReadCanvas(sourceWorld, canvas));
        ChunkJobs.Run(new DrawingJob(_drawings), _drawings.Count, Parallel);
        foreach (DrawingData drawing in _drawings)
        {
            if (_canvasInstances.TryGetValue(drawing.Canvas, out CanvasInstances canvas))
            {
                if (drawing.HasSurface) canvas.Surfaces.Add(drawing.SurfaceInstance);
                if (drawing.HasImage) canvas.Images.Add(drawing.ImageInstance);
            }
            else
            {
                if (drawing.HasSurface)
                {
                    if (!_surfaceValuesByCanvas.TryGetValue(drawing.Canvas, out var surfaces))
                        _surfaceValuesByCanvas.Add(drawing.Canvas, surfaces = []);
                    surfaces.Add(drawing.Node, drawing.SurfaceValue);
                }
                if (drawing.HasImage)
                {
                    if (!_imageValuesByCanvas.TryGetValue(drawing.Canvas, out var images))
                        _imageValuesByCanvas.Add(drawing.Canvas, images = []);
                    images.Add(drawing.Node, drawing.ImageInstance);
                }
            }
        }
        _drawings.Clear();
        return _canvasInstances.Count != 0 || _surfaceValuesByCanvas.Count != 0 ||
            _imageValuesByCanvas.Count != 0 || _canvasChanges.Count != 0;
    }

    private CanvasInstances CollectCanvas(World world, Entity canvasEntity)
    {
        Canvas canvas = world.Read<Canvas>(canvasEntity);
        var surfaces = new List<SurfaceInstance>();
        var images = new List<ImageInstance>();
        var tree = new CanvasTree(world, canvasEntity);
        while (tree.TryRead(out CanvasNode entry))
        {
            Entity node = entry.Node;
            bool surface = world.Has<Surface>(node) && world.Has<LayoutRect>(node);
            bool image = world.Has<Image>(node) && world.Has<LayoutRect>(node);
            if (surface || image)
                _drawings.Add(new(canvasEntity, node)
                {
                    HasSurface = surface, Surface = surface ? ReadSurface(world, node) : default,
                    HasImage = image, Image = image ? ReadImage(world, node) : default,
                    Clip = entry.Clip, Order = entry.DrawOrder,
                });
        }
        if (canvas.Target.Kind == CanvasTargetKind.WorldSpace)
            _worldTargets[canvasEntity] = canvas.Target.Entity;
        else _worldTargets.Remove(canvasEntity);
        return new(ReadCanvas(world, canvasEntity), surfaces, images);
    }

    private static RenderCanvas ReadCanvas(World world, Entity entity)
    {
        Canvas canvas = world.Read<Canvas>(entity);
        Matrix4x4 transform = Matrix4x4.Identity;
        if (canvas.Target.Kind == CanvasTargetKind.WorldSpace)
        {
            DevelopmentCheck.Require(world.IsAlive(canvas.Target.Entity) && world.Has<WorldTransform>(canvas.Target.Entity),
                "A world canvas target requires WorldTransform before extraction.");
            transform = world.Read<WorldTransform>(canvas.Target.Entity).Qvvs.ToMatrix();
        }
        return new RenderCanvas(canvas.Target, canvas.Size, canvas.Scale)
        {
            Transform = transform, DepthTest = canvas.DepthTest,
        };
    }

    private static SurfaceData ReadSurface(World world, Entity node)
    {
        Surface surface = world.Read<Surface>(node);
        Rect rectangle = world.Read<LayoutRect>(node).Value;
        return new(rectangle, surface,
            world.Has<ElementTransform>(node) ? world.Read<ElementTransform>(node) : ElementTransform.Identity,
            world.Has<PresentationOffset>(node) ? world.Read<PresentationOffset>(node) : PresentationOffset.Identity);
    }

    private static ImageData ReadImage(World world, Entity node)
    {
        Image image = world.Read<Image>(node);
        ImageTexture texture = world.Has<ImageTexture>(node) ? world.Read<ImageTexture>(node) :
            new(null, new Size(1, 1), 0);
        Rect rectangle = world.Read<LayoutRect>(node).Value;
        Thickness padding = world.Has<Box>(node) ? world.Read<Box>(node).Padding : default;
        return new(image, texture, rectangle, padding,
            world.Has<ElementTransform>(node) ? world.Read<ElementTransform>(node) : ElementTransform.Identity,
            world.Has<PresentationOffset>(node) ? world.Read<PresentationOffset>(node) : PresentationOffset.Identity);
    }

    private static ImageInstance CreateImage(Entity node, ImageData data)
    {
        Image image = data.Image;
        ImageTexture texture = data.Texture;
        Rect rectangle = data.Rectangle;
        Thickness padding = data.Padding;
        var presentation = ElementPresentation.Calculate(rectangle, data.Transform, data.Offset);
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
        return new(node, texture.Value, rectangle, uv, default, 0, 0, texture.Revision)
        {
            Matrix = presentation.Matrix, Alpha = presentation.Alpha,
        };
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
        foreach ((Entity source, RenderCanvas canvas) in _canvasChanges)
            context.UpdateExisting(context.RequireMirror(source), canvas);
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
                        Shadow = value.Shadow,
                        Matrix = value.Matrix, Alpha = value.Alpha,
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
    private readonly record struct SurfaceValue(Rect Rectangle, Color Fill, float CornerRadius,
        Color BorderColor, float BorderWidth, DropShadow Shadow, Matrix3x2 Matrix, float Alpha);

    private readonly record struct SurfaceData(Rect Rectangle, Surface Surface, ElementTransform Transform, PresentationOffset Offset);
    private readonly record struct ImageData(Image Image, ImageTexture Texture, Rect Rectangle, Thickness Padding,
        ElementTransform Transform, PresentationOffset Offset);

    private sealed class DrawingData(Entity canvas, Entity node)
    {
        internal readonly Entity Canvas = canvas;
        internal readonly Entity Node = node;
        internal bool HasSurface, HasImage;
        internal SurfaceData Surface;
        internal ImageData Image;
        internal Rect Clip;
        internal int Order;
        internal SurfaceValue SurfaceValue;
        internal SurfaceInstance SurfaceInstance;
        internal ImageInstance ImageInstance;
    }

    private readonly struct DrawingJob(List<DrawingData> drawings) : IJobChunk
    {
        public void Execute(JobChunk chunk)
        {
            for (int index = chunk.Start; index < chunk.Start + chunk.Length; index++)
            {
                DrawingData drawing = drawings[index];
                if (drawing.HasSurface)
                {
                    SurfaceData data = drawing.Surface;
                    Surface surface = data.Surface;
                    var presentation = ElementPresentation.Calculate(data.Rectangle, data.Transform, data.Offset);
                    drawing.SurfaceValue = new(data.Rectangle, surface.Fill, surface.CornerRadius,
                        surface.BorderColor, surface.BorderWidth, surface.Shadow, presentation.Matrix, presentation.Alpha);
                    drawing.SurfaceInstance = new(drawing.Node, data.Rectangle, surface.Fill, surface.CornerRadius,
                        surface.BorderColor, surface.BorderWidth, drawing.Clip, drawing.Order, 0)
                    { Shadow = surface.Shadow, Matrix = presentation.Matrix, Alpha = presentation.Alpha };
                }
                if (drawing.HasImage)
                    drawing.ImageInstance = CreateImage(drawing.Node, drawing.Image) with
                        { ClipRectangle = drawing.Clip, DrawOrder = drawing.Order };
            }
        }
    }
}
