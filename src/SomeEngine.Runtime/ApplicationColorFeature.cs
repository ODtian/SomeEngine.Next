using System.Numerics;
using SomeEngine.Graphics;
using SomeEngine.RenderGraph;
using SomeEngine.Render.Cluster.Pipeline;
using SomeEngine.Render.Lighting;

namespace SomeEngine.Runtime;

public readonly record struct ApplicationRendering(IGraphicsBackend Backend, Device Device,
    global::SomeEngine.RenderGraph.RenderGraph Graph, int Width, int Height, Matrix4x4 Projection = default,
    GraphRegionId Region = default)
{
    public ClusterPipelineOutputs Geometry { get; init; }
    public ClusterPipelineSystem? ClusterPipeline { get; init; }
    public RenderLightSet? Lights { get; init; }
    public bool CameraCut => CameraCutSource?.Invoke() ?? false;
    internal Func<bool>? CameraCutSource { get; init; }
    public ClusterStencil? Stencil => StencilSource?.Invoke();
    internal Func<ClusterStencil>? StencilSource { get; init; }
}

/// <summary>A persistent color feature consuming the renderer's color and depth.</summary>
public interface IApplicationColorFeature : IDisposable
{
    GraphPassId CompletePass { get; }
    void Reconcile(in ClusterPipelineOutputs geometry) { }
    void OrderAfter(ref RenderGraphEdit graph, GraphPassId colorReady);
    GraphTextureId Record(ref RenderGraphFrame graph, GraphTextureId color, GraphTextureId depth);
}
