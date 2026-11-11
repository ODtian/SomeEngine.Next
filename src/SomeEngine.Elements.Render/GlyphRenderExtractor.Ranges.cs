using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Hierarchy;
using SomeEngine.ECS.Systems;
using SomeEngine.Layout;
using SomeEngine.Render.Systems;
using SomeEngine.Views;

namespace SomeEngine.Elements.Render;

public sealed partial class GlyphRenderExtractor : IRenderExtractionRangeSystem
{
    public IRenderExtractionCollector CreateRangeCollector() => new GlyphChanges();

    public bool MergeRangeCollector(IRenderExtractionCollector collector, RenderExtractionContext context)
    {
        var values = (GlyphChanges)collector;
        _changes.TextNodes.UnionWith(values.TextNodes);
        _changes.TreeNodes.UnionWith(values.TreeNodes);
        _canvasEntities.UnionWith(values.Canvases);
        return false;
    }

    private sealed class GlyphChanges : IRenderExtractionCollector
    {
        internal readonly HashSet<Entity> Canvases = [];
        internal readonly HashSet<Entity> TextNodes = [];
        internal readonly HashSet<Entity> TreeNodes = [];

        public void Reset()
        {
            Canvases.Clear();
            TextNodes.Clear();
            TreeNodes.Clear();
        }

        public void Dispose() => Reset();

        public void Collect(QueryChunk packet, ulong lastSystemVersion, bool fullCollection)
        {
            if (fullCollection) Collect(packet);
            else CollectChanges(packet, lastSystemVersion);
        }

        public void Collect(QueryChunk chunk)
        {
            for (int row = 0; row < chunk.Count; row++)
            {
                Entity entity = chunk.Entities[row];
                if (chunk.Has<Canvas>(row)) Canvases.Add(entity);
                if (chunk.Has<Canvas>(row) || chunk.Has<ShapedText>(row) || chunk.Has<LayoutRect>(row) ||
                    chunk.Has<Parent<ElementTree>>(row) || chunk.Has<Children<ElementTree>>(row))
                    TreeNodes.Add(entity);
            }
        }

        private bool CollectChanges(QueryChunk chunk, ulong lastSystemVersion)
        {
            if (chunk.Has<Canvas>())
                for (int row = 0; row < chunk.Count; row++)
                    if (chunk.Has<Canvas>(row)) Canvases.Add(chunk.Entities[row]);
            bool text = chunk.Has<ShapedText>() && chunk.Has<LayoutRect>();
            bool shapeChanged = text && chunk.WasWritten<ShapedText>(lastSystemVersion);
            bool rectChanged = text && chunk.WasWritten<LayoutRect>(lastSystemVersion);
            bool boxChanged = text && chunk.Has<Box>() && chunk.WasWritten<Box>(lastSystemVersion);
            bool transformChanged = text && chunk.Has<ElementTransform>() &&
                chunk.WasWritten<ElementTransform>(lastSystemVersion);
            bool offsetChanged = text && chunk.Has<PresentationOffset>() &&
                chunk.WasWritten<PresentationOffset>(lastSystemVersion);
            bool viewportChanged = chunk.Has<ScrollOffset>() && chunk.Has<LayoutRect>() &&
                chunk.WasWritten<LayoutRect>(lastSystemVersion);
            bool canvasChanged = chunk.Has<Canvas>() && chunk.WasWritten<Canvas>(lastSystemVersion);
            bool parentChanged = chunk.Has<Parent<ElementTree>>() && chunk.WasWritten<Parent<ElementTree>>(lastSystemVersion);
            bool childrenChanged = chunk.Has<Children<ElementTree>>() && chunk.WasWritten<Children<ElementTree>>(lastSystemVersion);
            for (int row = 0; row < chunk.Count; row++)
            {
                Entity node = chunk.Entities[row];
                if ((canvasChanged && chunk.WasWritten<Canvas>(row, lastSystemVersion)) ||
                    (viewportChanged && chunk.WasWritten<LayoutRect>(row, lastSystemVersion)) ||
                    (parentChanged && chunk.WasWritten<Parent<ElementTree>>(row, lastSystemVersion)) ||
                    (childrenChanged && chunk.WasWritten<Children<ElementTree>>(row, lastSystemVersion)))
                    TreeNodes.Add(node);
                if (chunk.Has<ShapedText>(row) && chunk.Has<LayoutRect>(row) &&
                    ((shapeChanged && chunk.WasWritten<ShapedText>(row, lastSystemVersion)) ||
                    (rectChanged && chunk.WasWritten<LayoutRect>(row, lastSystemVersion)) ||
                    (boxChanged && chunk.WasWritten<Box>(row, lastSystemVersion)) ||
                    (transformChanged && chunk.WasWritten<ElementTransform>(row, lastSystemVersion)) ||
                    (offsetChanged && chunk.WasWritten<PresentationOffset>(row, lastSystemVersion))))
                    TextNodes.Add(node);
            }
            return false;
        }
    }
}