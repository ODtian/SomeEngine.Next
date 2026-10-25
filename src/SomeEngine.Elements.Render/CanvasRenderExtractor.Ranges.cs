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

        public void Collect(ReadOnlyQueryPacket packet, uint lastSystemVersion, bool fullCollection)
        {
            if (fullCollection) Collect(packet);
            else CollectChanges(packet, lastSystemVersion);
        }

        private void Collect(ReadOnlyQueryPacket chunk)
        {
            if (chunk.Has<Canvas>() || chunk.Has<Surface>() || chunk.Has<LayoutRect>() ||
                chunk.Has<Parent<ElementTree>>() || chunk.Has<Children<ElementTree>>())
                foreach (Entity entity in chunk.Entities) TreeNodes.Add(entity);
        }

        private bool CollectChanges(ReadOnlyQueryPacket chunk, uint lastSystemVersion)
        {
            bool surfaceChanged = chunk.Has<Surface>() && chunk.Has<LayoutRect>() &&
                chunk.ChangedSince<Surface>(lastSystemVersion);
            bool rectangleChanged = chunk.Has<Surface>() && chunk.Has<LayoutRect>() &&
                chunk.ChangedSince<LayoutRect>(lastSystemVersion);
            bool viewportChanged = chunk.Has<ScrollOffset>() && chunk.Has<LayoutRect>() &&
                chunk.ChangedSince<LayoutRect>(lastSystemVersion);
            bool canvasChanged = chunk.Has<Canvas>() && chunk.ChangedSince<Canvas>(lastSystemVersion);
            bool parentChanged = chunk.Has<Parent<ElementTree>>() &&
                chunk.ChangedSince<Parent<ElementTree>>(lastSystemVersion);
            bool childrenChanged = chunk.Has<Children<ElementTree>>() &&
                chunk.ChangedSince<Children<ElementTree>>(lastSystemVersion);
            bool transformChanged = chunk.Has<WorldTransform>() &&
                chunk.ChangedSince<WorldTransform>(lastSystemVersion);
            bool imageChanged = chunk.Has<Image>() && chunk.Has<LayoutRect>() &&
                (chunk.ChangedSince<Image>(lastSystemVersion) ||
                 chunk.ChangedSince<LayoutRect>(lastSystemVersion) ||
                 (chunk.Has<ImageTexture>() && chunk.ChangedSince<ImageTexture>(lastSystemVersion)) ||
                 (chunk.Has<Box>() && chunk.ChangedSince<Box>(lastSystemVersion)));
            bool presentationChanged = chunk.Has<LayoutRect>() &&
                ((chunk.Has<ElementTransform>() && chunk.ChangedSince<ElementTransform>(lastSystemVersion)) ||
                 (chunk.Has<PresentationOffset>() && chunk.ChangedSince<PresentationOffset>(lastSystemVersion)));
            if (!surfaceChanged && !rectangleChanged && !viewportChanged && !canvasChanged &&
                !parentChanged && !childrenChanged && !imageChanged && !transformChanged && !presentationChanged)
                return false;

            for (int row = 0; row < chunk.Count; row++)
            {
                Entity entity = chunk.Entities[row];
                bool presentation = presentationChanged &&
                    ((chunk.Has<ElementTransform>() && chunk.RowChangedSince<ElementTransform>(row, lastSystemVersion)) ||
                     (chunk.Has<PresentationOffset>() && chunk.RowChangedSince<PresentationOffset>(row, lastSystemVersion)));
                if (transformChanged && chunk.RowChangedSince<WorldTransform>(row, lastSystemVersion))
                    Targets.Add(entity);
                if ((canvasChanged && chunk.RowChangedSince<Canvas>(row, lastSystemVersion)) ||
                    (viewportChanged && chunk.RowChangedSince<LayoutRect>(row, lastSystemVersion)) ||
                    (parentChanged && chunk.RowChangedSince<Parent<ElementTree>>(row, lastSystemVersion)) ||
                    (childrenChanged && chunk.RowChangedSince<Children<ElementTree>>(row, lastSystemVersion)))
                    TreeNodes.Add(entity);
                if ((surfaceChanged && chunk.RowChangedSince<Surface>(row, lastSystemVersion)) ||
                    (rectangleChanged && chunk.RowChangedSince<LayoutRect>(row, lastSystemVersion)) ||
                    (presentation && chunk.Has<Surface>()))
                    SurfaceNodes.Add(entity);
                if ((imageChanged && (chunk.RowChangedSince<Image>(row, lastSystemVersion) ||
                    chunk.RowChangedSince<LayoutRect>(row, lastSystemVersion) ||
                    (chunk.Has<ImageTexture>() && chunk.RowChangedSince<ImageTexture>(row, lastSystemVersion)) ||
                    (chunk.Has<Box>() && chunk.RowChangedSince<Box>(row, lastSystemVersion)))) ||
                    (presentation && chunk.Has<Image>()))
                    ImageNodes.Add(entity);
            }
            // Source collection resolves the canvas and supplies the detached drawing values.
            return false;
        }
    }
}