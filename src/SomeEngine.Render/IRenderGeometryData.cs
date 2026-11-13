using System.Numerics;
using SomeEngine.ECS.Entities;
using SomeEngine.Render.Instances;
using SomeEngine.Render.Components;
using SomeEngine.Render.Systems;
using SomeEngine.RenderGraph;
using SomeEngine.Resources;

namespace SomeEngine.Render;

public interface IRenderGeometryData
{
    RenderGeometryResources Geometry { get; }
    RenderWorld World { get; }
    IRenderInstanceBatchSource<RenderInstanceSingleGroup> Instances { get; }
    RenderInstanceBatch? CurrentInstances { get; }
    int CurrentInstanceCount { get; }
    bool HasFiniteGeometryBounds { get; }
    WorldOrigin PreviousBoundsOrigin { get; }
    bool HasFirstPersonWorldMeshes();
    void ConfigureRenderContext(in RenderRasterContext context);
    bool CollectChangedInstances(ulong afterRevision, List<uint> rows, List<uint> streamingRows, out ulong revision);
    ReadOnlySpan<DepthCacheClass> CollectInstanceCacheInputs(List<Vector4> lodBounds, out uint version);
    void CollectInstanceEntities(Span<Entity> entities, uint afterVersion, List<uint> changedRows);
}
