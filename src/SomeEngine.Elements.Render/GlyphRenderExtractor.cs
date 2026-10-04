using SomeEngine.Core.Diagnostics;
using SomeEngine.Core.Math;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Hierarchy;
using SomeEngine.ECS.Queries;
using SomeEngine.Layout;
using SomeEngine.Render.Systems;
using SomeEngine.Typography;
using SomeEngine.Views;
using SomeEngine.Job;
using System.Numerics;

namespace SomeEngine.Elements.Render;

/// <summary>Extracts shaped glyphs and atlas pixels through the shared render update boundary.</summary>
public sealed class GlyphRenderExtractor(Entity atlasSource) : IRenderExtractionSystem
{
    private readonly HashSet<Entity> _changedTreeNodes = [];
    private readonly HashSet<Entity> _changedTextNodes = [];
    private readonly HashSet<Entity> _canvasEntities = [];
    private readonly Dictionary<Entity, List<GlyphInstance>> _canvasGlyphs = [];
    private readonly Dictionary<Entity, Dictionary<Entity, List<GlyphInstance>>> _textGlyphs = [];
    private GlyphAtlas? _atlas;
    private RenderGlyphAtlas _pixels = new(default, default, 8, 0);
    private Entity _atlasMirror;
    private bool _atlasChanged;
    private readonly List<TextDrawing> _drawings = [];

    /// <summary>Computes detached glyph instances with chunk jobs.</summary>
    public bool Parallel { get; set; } = true;

    public void DeclareReads(RenderExtractionQuery query)
    {
        query.ReadOptional<TextAtlas>();
        query.ReadOptional<Canvas>();
        query.ReadOptional<ShapedText>();
        query.ReadOptional<LayoutRect>();
        query.ReadOptional<ElementTransform>();
        query.ReadOptional<PresentationOffset>();
        query.ReadOptional<ScrollOffset>();
        query.ReadOptional<Box>();
        query.ReadOptional<Parent<ElementTree>>();
        query.ReadOptional<Children<ElementTree>>();
        query.Write<CanvasGlyphAtlas>();
        query.Write<RenderGlyphAtlas>();
    }

    public void Reset()
    {
        _changedTreeNodes.Clear();
        _changedTextNodes.Clear();
        _canvasGlyphs.Clear();
        _textGlyphs.Clear();
        _atlasChanged = false;
        _drawings.Clear();
    }

    public void Collect(QueryChunkView chunk)
    {
        if (chunk.Has<Canvas>())
            foreach (Entity canvas in chunk.Entities) _canvasEntities.Add(canvas);
        if (chunk.Has<Canvas>() || chunk.Has<ShapedText>() || chunk.Has<LayoutRect>() ||
            chunk.Has<Parent<ElementTree>>() || chunk.Has<Children<ElementTree>>())
            foreach (Entity entity in chunk.Entities) _changedTreeNodes.Add(entity);
    }

    public bool CollectChanges(QueryChunkView chunk, RenderExtractionContext context)
    {
        if (chunk.Has<Canvas>())
            foreach (Entity canvas in chunk.Entities) _canvasEntities.Add(canvas);
        bool text = chunk.Has<ShapedText>() && chunk.Has<LayoutRect>();
        bool shapeChanged = text && chunk.HasChangedSinceLastSystemVersion<ShapedText>();
        bool rectChanged = text && chunk.HasChangedSinceLastSystemVersion<LayoutRect>();
        bool boxChanged = text && chunk.Has<Box>() && chunk.HasChangedSinceLastSystemVersion<Box>();
        bool transformChanged = text && chunk.Has<ElementTransform>() &&
            chunk.HasChangedSinceLastSystemVersion<ElementTransform>();
        bool offsetChanged = text && chunk.Has<PresentationOffset>() &&
            chunk.HasChangedSinceLastSystemVersion<PresentationOffset>();
        bool viewportChanged = chunk.Has<ScrollOffset>() && chunk.Has<LayoutRect>() &&
            chunk.HasChangedSinceLastSystemVersion<LayoutRect>();
        bool canvasChanged = chunk.Has<Canvas>() && chunk.HasChangedSinceLastSystemVersion<Canvas>();
        bool parentChanged = chunk.Has<Parent<ElementTree>>() && chunk.HasChangedSinceLastSystemVersion<Parent<ElementTree>>();
        bool childrenChanged = chunk.Has<Children<ElementTree>>() && chunk.HasChangedSinceLastSystemVersion<Children<ElementTree>>();
        for (int row = 0; row < chunk.Count; row++)
        {
            Entity node = chunk.Entities[row];
            if ((canvasChanged && chunk.RowChangedSinceLastSystemVersion<Canvas>(row)) ||
                (viewportChanged && chunk.RowChangedSinceLastSystemVersion<LayoutRect>(row)) ||
                (parentChanged && chunk.RowChangedSinceLastSystemVersion<Parent<ElementTree>>(row)) ||
                (childrenChanged && chunk.RowChangedSinceLastSystemVersion<Children<ElementTree>>(row)))
                _changedTreeNodes.Add(node);
            if ((shapeChanged && chunk.RowChangedSinceLastSystemVersion<ShapedText>(row)) ||
                (rectChanged && chunk.RowChangedSinceLastSystemVersion<LayoutRect>(row)) ||
                (boxChanged && chunk.RowChangedSinceLastSystemVersion<Box>(row)) ||
                (transformChanged && chunk.RowChangedSinceLastSystemVersion<ElementTransform>(row)) ||
                (offsetChanged && chunk.RowChangedSinceLastSystemVersion<PresentationOffset>(row)))
                _changedTextNodes.Add(node);
        }
        return false;
    }

    public bool CompleteSourceCollection(World sourceWorld, bool fullCollection)
    {
        DevelopmentCheck.Require(sourceWorld.IsAlive(atlasSource) && sourceWorld.Has<TextAtlas>(atlasSource),
            "Glyph extraction requires a TextAtlas source entity.");
        GlyphAtlas atlas = sourceWorld.Read<TextAtlas>(atlasSource).Atlas;
        DevelopmentCheck.Require(atlas is not null, "TextAtlas requires a glyph atlas.");
        bool atlasReplaced = !ReferenceEquals(_atlas, atlas);
        _atlas = atlas;
        _canvasEntities.RemoveWhere(canvas => !sourceWorld.IsAlive(canvas) || !sourceWorld.Has<Canvas>(canvas));
        if (atlasReplaced) _changedTreeNodes.UnionWith(_canvasEntities);
        foreach (Entity node in _changedTreeNodes)
        {
            Entity canvas = CanvasTree.FindCanvas(sourceWorld, node);
            if (canvas != Entity.Null && !_canvasGlyphs.ContainsKey(canvas))
                _canvasGlyphs.Add(canvas, CollectCanvas(sourceWorld, canvas));
        }
        foreach (Entity node in _changedTextNodes)
        {
            Entity canvas = CanvasTree.FindCanvas(sourceWorld, node);
            if (canvas == Entity.Null || _canvasGlyphs.ContainsKey(canvas)) continue;
            CanvasNode entry = new CanvasTree(sourceWorld, canvas).ReadNode(node);
            ReadGlyphs(sourceWorld, canvas, node, entry.Clip, entry.DrawOrder);
        }
        ChunkJobs.Run(new GlyphJob(_drawings), _drawings.Count, Parallel);
        foreach (TextDrawing drawing in _drawings)
            if (_canvasGlyphs.TryGetValue(drawing.Canvas, out List<GlyphInstance>? glyphs))
                glyphs.AddRange(drawing.Values);
            else
            {
                if (!_textGlyphs.TryGetValue(drawing.Canvas, out var texts)) _textGlyphs.Add(drawing.Canvas, texts = []);
                texts.Add(drawing.Node, drawing.Values);
            }
        _drawings.Clear();
        CopyAtlasPixels(atlasReplaced);
        return _canvasGlyphs.Count != 0 || _textGlyphs.Count != 0 || _atlasChanged;
    }

    public bool CompleteCollection(RenderExtractionContext context, bool fullCollection)
    {
        // Keep the detached pixels available when an extraction transaction is retried.
        _atlasChanged |= !context.World.IsAlive(_atlasMirror) ||
            !context.World.Has<RenderGlyphAtlas>(_atlasMirror) ||
            context.World.Read<RenderGlyphAtlas>(_atlasMirror) != _pixels;
        return _atlasChanged;
    }

    private void CopyAtlasPixels(bool atlasReplaced)
    {
        ReadOnlySpan<AtlasDirtyRegion> dirty = _atlas!.DirtyRegions;
        if (dirty.IsEmpty && !atlasReplaced) return;
        ReadOnlySpan<AtlasPage> pages = _atlas.Pages;
        var copied = new GlyphPagePixels[pages.Length];
        if (!atlasReplaced) _pixels.Pages.Span.CopyTo(copied);
        var regions = atlasReplaced ? new AtlasDirtyRegion[pages.Length] : dirty.ToArray();
        for (int index = 0; index < pages.Length; index++)
        {
            AtlasPage page = pages[index];
            if (atlasReplaced)
                regions[index] = new(index, 0, 0, page.Width, page.Height);
            bool changed = atlasReplaced || index >= _pixels.Pages.Length;
            foreach (AtlasDirtyRegion region in dirty) changed |= region.PageIndex == index;
            if (changed) copied[index] = new(page.Width, page.Height, page.Pixels.ToArray());
        }
        _pixels = new(copied, regions, _atlas.DistanceSpread, unchecked(_pixels.Version + 1));
        _atlas.ClearDirtyRegions();
        _atlasChanged = true;
    }

    private List<GlyphInstance> CollectCanvas(World world, Entity canvas)
    {
        var instances = new List<GlyphInstance>();
        var tree = new CanvasTree(world, canvas);
        while (tree.TryRead(out CanvasNode entry))
        {
            Entity node = entry.Node;
            if (world.Has<ShapedText>(node) && world.Has<LayoutRect>(node))
                ReadGlyphs(world, canvas, node, entry.Clip, entry.DrawOrder);
        }
        return instances;
    }

    private void ReadGlyphs(World world, Entity canvas, Entity node, Rect clip, int order)
    {
        ShapedText text = world.Read<ShapedText>(node);
        Rect rectangle = world.Read<LayoutRect>(node).Value;
        var padding = world.Has<Box>(node) ? world.Read<Box>(node).Padding : default;
        float scale = text.Style.Size / _atlas!.FontSize;
        ReadOnlySpan<Glyph> glyphs = text.Shape.Glyphs;
        var locations = new GlyphLocation[glyphs.Length];
        // First use determines atlas placement, so resolve in source order before the jobs run.
        for (int index = 0; index < glyphs.Length; index++)
            locations[index] = _atlas.Get(glyphs[index].Font, glyphs[index].Index);
        _drawings.Add(new TextDrawing(canvas, node, text.Shape, locations, rectangle, padding, scale,
            text.Style.Color, clip, order,
            world.Has<ElementTransform>(node) ? world.Read<ElementTransform>(node) : ElementTransform.Identity,
            world.Has<PresentationOffset>(node) ? world.Read<PresentationOffset>(node) : PresentationOffset.Identity));
    }

    private sealed class TextDrawing(Entity canvas, Entity node, TextShape shape, GlyphLocation[] locations,
        Rect rectangle, Thickness padding, float scale, Color color, Rect clip, int order,
        ElementTransform transform, PresentationOffset offset)
    {
        internal readonly Entity Canvas = canvas;
        internal readonly Entity Node = node;
        internal readonly List<GlyphInstance> Values = new(locations.Length);

        internal void CreateGlyphs()
        {
            var presentation = ElementPresentation.Calculate(rectangle, transform, offset);
            ReadOnlySpan<Glyph> glyphs = shape.Glyphs;
            for (int index = 0; index < glyphs.Length; index++)
            {
                ref readonly Glyph glyph = ref glyphs[index];
                GlyphLocation location = locations[index];
                AtlasDirtyRegion region = location.Region;
                if (region.PageIndex < 0) continue;
                Values.Add(new(Node, index,
                    new Rect(rectangle.X + padding.Left + glyph.Position.X + location.Offset.X * scale,
                        rectangle.Y + padding.Top + glyph.Position.Y + location.Offset.Y * scale,
                        region.Width * scale, region.Height * scale),
                    location.UV0, location.UV1, color, clip, region.PageIndex, order, 0)
                { Matrix = presentation.Matrix, Alpha = presentation.Alpha });
            }
        }
    }

    private readonly struct GlyphJob(List<TextDrawing> drawings) : IJobChunk
    {
        public void Execute(JobChunk chunk)
        {
            for (int index = chunk.Start; index < chunk.Start + chunk.Length; index++) drawings[index].CreateGlyphs();
        }
    }

    public void Apply(RenderExtractionContext context)
    {
        _atlasMirror = context.RetainMirror(atlasSource);
        context.Upsert(_atlasMirror, _pixels);
        foreach ((Entity source, List<GlyphInstance> values) in _canvasGlyphs)
        {
            Entity canvas = context.RetainMirror(source);
            context.Upsert(canvas, new CanvasGlyphAtlas(_atlasMirror));
            if (!context.World.HasBuffer<GlyphInstance>(canvas)) context.World.AddBuffer<GlyphInstance>(canvas);
            UpdateInstances(context.World, canvas, values);
        }
        foreach (RenderMirror mirror in context.Mirrors)
        {
            if (_canvasGlyphs.ContainsKey(mirror.Source)) continue;
            context.RemoveIfExists<CanvasGlyphAtlas>(mirror.RenderEntity);
            if (context.World.HasBuffer<GlyphInstance>(mirror.RenderEntity))
                context.World.RemoveBuffer<GlyphInstance>(mirror.RenderEntity);
        }
    }

    public void ApplyChanges(RenderExtractionContext context)
    {
        if (_atlasChanged) context.UpdateExisting(context.RequireMirror(atlasSource), _pixels);
        foreach ((Entity source, List<GlyphInstance> values) in _canvasGlyphs)
            UpdateInstances(context.World, context.RequireMirror(source), values);
        foreach ((Entity source, Dictionary<Entity, List<GlyphInstance>> texts) in _textGlyphs)
        {
            Entity canvas = context.RequireMirror(source);
            var values = new List<GlyphInstance>();
            context.World.ExecuteBufferRead<GlyphInstance>(canvas, buffer =>
            {
                foreach (GlyphInstance before in buffer.AsSpan())
                    if (!texts.ContainsKey(before.Node)) values.Add(before);
            });
            foreach (List<GlyphInstance> text in texts.Values) values.AddRange(text);
            values.Sort(static (left, right) => left.DrawOrder != right.DrawOrder
                ? left.DrawOrder.CompareTo(right.DrawOrder) : left.GlyphIndex.CompareTo(right.GlyphIndex));
            UpdateInstances(context.World, canvas, values);
        }
    }

    private static void UpdateInstances(RenderWorld world, Entity canvas, List<GlyphInstance> values)
    {
        var previous = new Dictionary<(Entity, int), GlyphInstance>();
        world.ExecuteBufferRead<GlyphInstance>(canvas, buffer =>
        {
            foreach (GlyphInstance glyph in buffer.AsSpan()) previous.Add((glyph.Node, glyph.GlyphIndex), glyph);
        });
        for (int index = 0; index < values.Count; index++)
        {
            GlyphInstance value = values[index];
            if (previous.TryGetValue((value.Node, value.GlyphIndex), out GlyphInstance before))
            {
                value = value with { Version = before.Version };
                if (value != before) value = value with { Version = unchecked(before.Version + 1) };
            }
            else value = value with { Version = 1 };
            values[index] = value;
        }
        world.ExecuteBufferWrite<GlyphInstance>(canvas, buffer =>
        {
            for (int index = 0; index < values.Count; index++)
                if (index >= buffer.Count) buffer.Add(values[index]);
                else if (buffer.Read(index) != values[index]) buffer[index] = values[index];
            while (buffer.Count > values.Count) buffer.SwapRemoveAt(buffer.Count - 1);
        });
    }
}
