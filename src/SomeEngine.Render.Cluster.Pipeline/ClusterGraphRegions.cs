using SomeEngine.RenderGraph;

namespace SomeEngine.Render.Cluster.Pipeline;

/// <summary>The application supplies the positions used by one Cluster pipeline.</summary>
public readonly record struct ClusterGraphRegions(
    GraphRegionId PrimaryGeometry,
    GraphRegionId TargetGeometry,
    GraphRegionId Shading,
    GraphRegionId Feedback);
