using SomeEngine.RenderGraph;

namespace SomeEngine.Render.Cluster.Pipeline;

public sealed partial class ClusterPipelineSystem
{
    private GraphRegionId _surfaceReceiverRegion;

    /// <summary>Places user receiver evaluation between primary visibility and light-dependent shading.</summary>
    public void ConfigureSurfaceReceivers(RenderGraphEdit edit, GraphRegionId region)
    {
        if (_graphInstalled) throw new InvalidOperationException("Surface receiver stages must be configured before graph installation.");
        if (_surfaceReceiverRegion != default) edit.Remove(_surfaceReceiverRegion);
        _surfaceReceiverRegion = edit.AddRegion("Cluster pipeline receivers", region);
    }
}
