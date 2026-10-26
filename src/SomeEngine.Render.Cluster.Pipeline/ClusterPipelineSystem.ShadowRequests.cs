using SomeEngine.RenderGraph;

namespace SomeEngine.Render.Cluster.Pipeline;

public sealed partial class ClusterPipelineSystem
{
    private GraphRegionId _shadowRequestRegion;
    private GraphRegionId _transparencyRequestRegion;

    /// <summary>Places material position evaluation and direct page requests before shadow allocation.</summary>
    public void ConfigureShadowRequests(RenderGraphEdit edit, GraphRegionId region)
    {
        _shadowRequestRegion = edit.AddRegion("Cluster shadow page requests", region);
        _transparencyRequestRegion = edit.AddRegion("Cluster transparent shadow page requests", region);
    }
}
