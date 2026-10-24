using SomeEngine.RenderGraph;

namespace SomeEngine.Render.Cluster.Pipeline;

public sealed partial class ClusterPipelineSystem
{
    private GraphRegionId _surfaceReceiverRegion;
    private bool _evaluateSurfaceReceivers = true;
    private bool _primaryDepthMaterialsSupported;
    private GraphRegionId _transparencyRequestRegion;

    /// <summary>Places transparent receiver requests before shadow page allocation.</summary>
    public void ConfigureTransparencyRequests(RenderGraphEdit edit, GraphRegionId region)
    {
        _transparencyRequestRegion = edit.AddRegion("Cluster transparent receiver requests", region);
    }

    /// <summary>Whether the installed shade stages evaluate material receivers before lighting.</summary>
    public bool HasEarlySurfaceReceiverEvaluation => _installedShadeStages?.HasReceiverEvaluation ??
        throw new InvalidOperationException("ReconcileGraph must install Cluster before querying receiver evaluation.");

    /// <summary>Places receiver stages between primary visibility and light-dependent shading.</summary>
    /// <param name="evaluateMaterials">Whether Forward consumers require material receiver evaluation. Deferred always evaluates receivers.</param>
    public void ConfigureSurfaceReceivers(RenderGraphEdit edit, GraphRegionId region, bool evaluateMaterials = true)
    {
        if (_graphInstalled) throw new InvalidOperationException("Surface receiver stages must be configured before graph installation.");
        if (_surfaceReceiverRegion != default) edit.Remove(_surfaceReceiverRegion);
        _surfaceReceiverRegion = edit.AddRegion("Cluster pipeline receivers", region);
        _evaluateSurfaceReceivers = evaluateMaterials;
    }

    /// <summary>Changes Forward receiver demand at the next frame-boundary ReconcileGraph call.</summary>
    /// <remarks>Callers using depth-only requests may disable evaluation only while all their receiver consumers support it.</remarks>
    public void SetSurfaceReceiverEvaluation(bool evaluateMaterials)
    {
        if (_surfaceReceiverRegion == default)
            throw new InvalidOperationException("Surface receiver placement must be configured before changing evaluation demand.");
        _evaluateSurfaceReceivers = evaluateMaterials;
    }
}
