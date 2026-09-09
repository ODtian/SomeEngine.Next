using SomeEngine.RenderGraph;

namespace SomeEngine.Render.Cluster.Pipeline;

/// <summary>Supplies source view templates to the feature that creates Cluster raster work.</summary>
public delegate void ClusterInvocationSourceBinder(
    ref RenderGraphFrame graph,
    GraphBufferSlice sourceViews);
