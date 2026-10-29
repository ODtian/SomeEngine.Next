using SomeEngine.RenderGraph;

namespace SomeEngine.Render.Cluster.Pipeline;

public sealed partial class ClusterPipelineSystem
{
    private GraphRegionId _shadowRequestRegion;
    private GraphRegionId _transparencyRequestRegion;
    private bool _allowSharedDepthRequests;
    private bool _primaryDepthMaterialsSupported;

    /// <summary>Whether the installed Forward materials share the primary-depth request pass.</summary>
    public bool UsesPrimaryDepthReceiverRequests => _installedShadeStages?.SharedDepthRequests == true;

    /// <summary>Selects shared primary-depth demand at the next frame-boundary reconciliation.</summary>
    public void SetPrimaryDepthReceiverRequests(bool enabled) => _allowSharedDepthRequests = enabled;

    /// <summary>Places material position evaluation and direct page requests before shadow allocation.</summary>
    public void ConfigureShadowRequests(RenderGraphEdit edit, GraphRegionId region)
    {
        _shadowRequestRegion = edit.AddRegion("Cluster shadow page requests", region);
        _transparencyRequestRegion = edit.AddRegion("Cluster transparent shadow page requests", region);
    }
}
