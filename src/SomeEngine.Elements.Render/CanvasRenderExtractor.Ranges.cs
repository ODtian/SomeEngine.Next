using SomeEngine.ECS;
using SomeEngine.Core.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Hierarchy;
using SomeEngine.ECS.Systems;
using SomeEngine.Layout;
using SomeEngine.Render.Systems;
using SomeEngine.Views;

namespace SomeEngine.Elements.Render;

public partial class CanvasRenderExtractor : IRenderExtractionRangeSystem
{
    public IRenderExtractionCollector CreateRangeCollector() => new CanvasChanges();

    public bool MergeRangeCollector(IRenderExtractionCollector collector, RenderExtractionContext context)
    {
        var values = (CanvasChanges)collector;
        _changes.ImageNodes.UnionWith(values.ImageNodes);
        _changes.SurfaceNodes.UnionWith(values.SurfaceNodes);
        _changes.Targets.UnionWith(values.Targets);
        _changes.TreeNodes.UnionWith(values.TreeNodes);
        return false;
    }

    private sealed class CanvasChanges : IRenderExtractionCollector
    {
        internal readonly HashSet<Entity> ImageNodes = [];
        internal readonly HashSet<Entity> SurfaceNodes = [];
        internal readonly HashSet<Entity> Targets = [];
        internal readonly HashSet<Entity> TreeNodes = [];

        public void Reset()
        {
            ImageNodes.Clear();
            SurfaceNodes.Clear();
            Targets.Clear();
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
                if (chunk.Has<Canvas>(row) || chunk.Has<Surface>(row) || chunk.Has<LayoutRect>(row) ||
                    chunk.Has<Parent<ElementTree>>(row) || chunk.Has<Children<ElementTree>>(row))
                    TreeNodes.Add(chunk.Entities[row]);
        }

        private bool CollectChanges(QueryChunk chunk, ulong lastSystemVersion)
        {
            bool surfaceChanged = chunk.Has<Surface>() && chunk.Has<LayoutRect>() &&
                chunk.WasWritten<Surface>(lastSystemVersion);
            bool rectangleChanged = chunk.Has<Surface>() && chunk.Has<LayoutRect>() &&
                chunk.WasWritten<LayoutRect>(lastSystemVersion);
            bool viewportChanged = chunk.Has<ScrollOffset>() && chunk.Has<LayoutRect>() &&
                chunk.WasWritten<LayoutRect>(lastSystemVersion);
            bool canvasChanged = chunk.Has<Canvas>() && chunk.WasWritten<Canvas>(lastSystemVersion);
            bool parentChanged = chunk.Has<Parent<ElementTree>>() &&
                chunk.WasWritten<Parent<ElementTree>>(lastSystemVersion);
            bool childrenChanged = chunk.Has<Children<ElementTree>>() &&
                chunk.WasWritten<Children<ElementTree>>(lastSystemVersion);
            bool transformChanged = chunk.Has<WorldTransform>() &&
                chunk.WasWritten<WorldTransform>(lastSystemVersion);
            bool imageChanged = chunk.Has<Image>() && chunk.Has<LayoutRect>() &&
                (chunk.WasWritten<Image>(lastSystemVersion) ||
                 chunk.WasWritten<LayoutRect>(lastSystemVersion) ||
                 (chunk.Has<ImageTexture>() && chunk.WasWritten<ImageTexture>(lastSystemVersion)) ||
                 (chunk.Has<Box>() && chunk.WasWritten<Box>(lastSystemVersion)));
            bool presentationChanged = chunk.Has<LayoutRect>() &&
                ((chunk.Has<ElementTransform>() && chunk.WasWritten<ElementTransform>(lastSystemVersion)) ||
                 (chunk.Has<PresentationOffset>() && chunk.WasWritten<PresentationOffset>(lastSystemVersion)));
            if (!surfaceChanged && !rectangleChanged && !viewportChanged && !canvasChanged &&
                !parentChanged && !childrenChanged && !imageChanged && !transformChanged && !presentationChanged)
                return false;

            for (int row = 0; row < chunk.Count; row++)
            {
                Entity entity = chunk.Entities[row];
                bool presentation = presentationChanged &&
                    ((chunk.Has<ElementTransform>() && chunk.WasWritten<ElementTransform>(row, lastSystemVersion)) ||
                     (chunk.Has<PresentationOffset>() && chunk.WasWritten<PresentationOffset>(row, lastSystemVersion)));
                if (transformChanged && chunk.WasWritten<WorldTransform>(row, lastSystemVersion))
                    Targets.Add(entity);
                if ((canvasChanged && chunk.WasWritten<Canvas>(row, lastSystemVersion)) ||
                    (viewportChanged && chunk.WasWritten<LayoutRect>(row, lastSystemVersion)) ||
                    (parentChanged && chunk.WasWritten<Parent<ElementTree>>(row, lastSystemVersion)) ||
                    (childrenChanged && chunk.WasWritten<Children<ElementTree>>(row, lastSystemVersion)))
                    TreeNodes.Add(entity);
                if (chunk.Has<Surface>(row) && chunk.Has<LayoutRect>(row) &&
                    ((surfaceChanged && chunk.WasWritten<Surface>(row, lastSystemVersion)) ||
                     (rectangleChanged && chunk.WasWritten<LayoutRect>(row, lastSystemVersion)) || presentation))
                    SurfaceNodes.Add(entity);
                if (chunk.Has<Image>(row) && chunk.Has<LayoutRect>(row) &&
                    ((imageChanged && (chunk.WasWritten<Image>(row, lastSystemVersion) ||
                    chunk.WasWritten<LayoutRect>(row, lastSystemVersion) ||
                    (chunk.Has<ImageTexture>() && chunk.WasWritten<ImageTexture>(row, lastSystemVersion)) ||
                    (chunk.Has<Box>() && chunk.WasWritten<Box>(row, lastSystemVersion)))) ||
                    presentation))
                    ImageNodes.Add(entity);
            }
            // Source collection resolves the canvas and supplies the detached drawing values.
            return false;
        }
    }
}