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

        public void Collect(ReadOnlyQueryPacket packet, uint lastSystemVersion, bool fullCollection)
        {
            if (fullCollection) Collect(packet);
            else CollectChanges(packet, lastSystemVersion);
        }

        private void Collect(ReadOnlyQueryPacket chunk)
        {
            if (chunk.Has<Canvas>())
                foreach (Entity canvas in chunk.Entities) Canvases.Add(canvas);
            if (chunk.Has<Canvas>() || chunk.Has<ShapedText>() || chunk.Has<LayoutRect>() ||
                chunk.Has<Parent<ElementTree>>() || chunk.Has<Children<ElementTree>>())
                foreach (Entity entity in chunk.Entities) TreeNodes.Add(entity);
        }

        private bool CollectChanges(ReadOnlyQueryPacket chunk, uint lastSystemVersion)
        {
            if (chunk.Has<Canvas>())
                foreach (Entity canvas in chunk.Entities) Canvases.Add(canvas);
            bool text = chunk.Has<ShapedText>() && chunk.Has<LayoutRect>();
            bool shapeChanged = text && chunk.ChangedSince<ShapedText>(lastSystemVersion);
            bool rectChanged = text && chunk.ChangedSince<LayoutRect>(lastSystemVersion);
            bool boxChanged = text && chunk.Has<Box>() && chunk.ChangedSince<Box>(lastSystemVersion);
            bool transformChanged = text && chunk.Has<ElementTransform>() &&
                chunk.ChangedSince<ElementTransform>(lastSystemVersion);
            bool offsetChanged = text && chunk.Has<PresentationOffset>() &&
                chunk.ChangedSince<PresentationOffset>(lastSystemVersion);
            bool viewportChanged = chunk.Has<ScrollOffset>() && chunk.Has<LayoutRect>() &&
                chunk.ChangedSince<LayoutRect>(lastSystemVersion);
            bool canvasChanged = chunk.Has<Canvas>() && chunk.ChangedSince<Canvas>(lastSystemVersion);
            bool parentChanged = chunk.Has<Parent<ElementTree>>() && chunk.ChangedSince<Parent<ElementTree>>(lastSystemVersion);
            bool childrenChanged = chunk.Has<Children<ElementTree>>() && chunk.ChangedSince<Children<ElementTree>>(lastSystemVersion);
            for (int row = 0; row < chunk.Count; row++)
            {
                Entity node = chunk.Entities[row];
                if ((canvasChanged && chunk.RowChangedSince<Canvas>(row, lastSystemVersion)) ||
                    (viewportChanged && chunk.RowChangedSince<LayoutRect>(row, lastSystemVersion)) ||
                    (parentChanged && chunk.RowChangedSince<Parent<ElementTree>>(row, lastSystemVersion)) ||
                    (childrenChanged && chunk.RowChangedSince<Children<ElementTree>>(row, lastSystemVersion)))
                    TreeNodes.Add(node);
                if ((shapeChanged && chunk.RowChangedSince<ShapedText>(row, lastSystemVersion)) ||
                    (rectChanged && chunk.RowChangedSince<LayoutRect>(row, lastSystemVersion)) ||
                    (boxChanged && chunk.RowChangedSince<Box>(row, lastSystemVersion)) ||
                    (transformChanged && chunk.RowChangedSince<ElementTransform>(row, lastSystemVersion)) ||
                    (offsetChanged && chunk.RowChangedSince<PresentationOffset>(row, lastSystemVersion)))
                    TextNodes.Add(node);
            }
            return false;
        }
    }
}