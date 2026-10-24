using SomeEngine.Graphics;
using SomeEngine.Render.Cluster.Pipeline;
using SomeEngine.RenderGraph;

namespace SomeEngine.Render.Features.Shadows;

public sealed partial class VirtualShadowFeature
{
    private GraphPassId _depthMarkPass;
    private GraphBufferInput _depthReceiverViews = null!;
    private bool _primaryDepthReceiverRequests;
    private bool _allowPrimaryDepthReceiverRequests;

    /// <summary>
    /// Allows depth-based primary requests at the next geometry reconciliation. The pipeline verifies
    /// the actual saved Forward entry capability and current first-person instances. Unsupported
    /// materials and receiver configurations retain early evaluation in the selected shading path.
    /// </summary>
    public void SetPrimaryDepthReceiverRequests(bool enabled)
    {
        if (_frameActive) throw new InvalidOperationException("Receiver demand must be selected before staging a shadow frame.");
        _allowPrimaryDepthReceiverRequests = enabled;
        ReconcileReceiverDemand();
    }

    private void ReconcileReceiverDemand()
    {
        _primaryDepthReceiverRequests = _allowPrimaryDepthReceiverRequests && _shadowMap.DepthPageMarkPipeline is not null &&
            !_shadowMap.Settings.MarkPagesUsingFroxels && _additionalReceiverCount == 0u;
        _geometry.SetSurfaceReceiverEvaluation(!_primaryDepthReceiverRequests);
    }

    /// <summary>Whether the last staged frame used the primary-depth page request entry.</summary>
    public bool UsesPrimaryDepthReceiverRequests { get; private set; }

    private void CreateDepthPageMarking(ref RenderGraphEdit edit, GraphRegionId region)
    {
        if (_shadowMap.DepthPageMarkPipeline is not { } pipeline) return;
        _depthReceiverViews = edit.CreateBufferInput(BufferUsages.ShaderRead);
        _depthMarkPass = AddComputePass(ref edit, "Mark virtual shadow pages from primary depth", pipeline,
            new VirtualShadowDispatchPassData(default), region: region);
    }

    private void OrderDepthPageMarking(ref RenderGraphEdit edit)
    {
        if (_depthMarkPass == default) return;
        // Match the material-request entry ordering: request publication precedes the
        // pool and caster invalidation readers, after the per-view pressure update.
        edit.OrderAfter(_poolPreparePass, _depthMarkPass);
        if (_prefilterRequestPass != default) edit.OrderAfter(_prefilterRequestPass, _depthMarkPass);
        if (_workUpdatePass != default) edit.OrderAfter(_depthMarkPass, _workUpdatePass);
    }

    private void BindDepthPageMarking(ref RenderGraphEdit edit, GraphBufferInput uniforms)
    {
        if (_depthMarkPass == default) return;
        PassDefinition pass = edit.GetPass(_depthMarkPass);
        BindPageMarking(ref edit, ref pass, uniforms, depthReceiver: true);
    }
}
