using SomeEngine.Render.Cluster.Pipeline;
using SomeEngine.Render.Features.Lighting;
using SomeEngine.Render.Features.Shadows;
using SomeEngine.Render.Features.Temporal;
using SomeEngine.Render.Systems;
using SomeEngine.RenderGraph;

namespace SomeEngine.Runtime;

/// <summary>The fixed-size main-view resources replaced together when the window extent changes.</summary>
internal sealed class ApplicationFrameOutput(
    global::SomeEngine.RenderGraph.RenderGraph graph,
    ClusterPipelineOutputDescription description,
    LightingFeature lighting,
    ClusterPipelineSystem pipeline,
    VirtualShadowFeature shadows,
    RenderFrameSystems systems,
    GraphRegionId colorRegion) : IDisposable
{
    internal ClusterPipelineOutputDescription Description { get; } = description;
    internal int Width => Description.Width;
    internal int Height => Description.Height;
    internal LightingFeature Lighting { get; } = lighting;
    internal ClusterPipelineSystem Pipeline { get; } = pipeline;
    internal VirtualShadowFeature Shadows { get; } = shadows;
    internal RenderFrameSystems Systems { get; } = systems;
    internal GraphRegionId ColorRegion { get; } = colorRegion;
    internal IApplicationColorFeature? ColorFeature { get; set; }
    internal TemporalFeature? Temporal { get; set; }
    internal RuntimePresentationPipeline? Presentation { get; set; }
    internal GraphReadbackId PresentationReadback { get; set; }

    /// <summary>Called after the new output has taken over every consumer in the retained graph.</summary>
    internal void UninstallGraph()
    {
        Presentation?.UninstallGraph();
        Temporal?.UninstallGraph();
        ColorFeature?.Dispose();
        ColorFeature = null;
        using (RenderGraphEdit edit = graph.BeginEdit())
        {
            edit.Remove(ColorRegion);
            edit.Commit();
        }
        Pipeline.UninstallGraph();
        Shadows.UninstallGraph();
        Lighting.UninstallGraph();
    }

    public void Dispose()
    {
        Presentation?.Dispose();
        Temporal?.Dispose();
        ColorFeature?.Dispose();
        Systems.Dispose();
        Shadows.Dispose();
        Lighting.Dispose();
    }
}
