using SomeEngine.Graphics;
using SomeEngine.Render.Cluster.Pipeline;
using SomeEngine.RenderGraph;

namespace SomeEngine.Render.Features.Shadows;

public sealed partial class VirtualShadowFeature
{
    private GraphPassId _depthMarkPass;
    private GraphBufferInput _depthReceiverViews = null!;
    private GraphTextureInput _receiverDepth = null!;

    /// <summary>Allows the installed Forward material declarations to select shared primary-depth requests.</summary>
    public void SetPrimaryDepthReceiverRequests(bool enabled) =>
        _geometry.SetPrimaryDepthReceiverRequests(enabled && _shadowMap.DepthPageMarkPipeline is not null);

    /// <summary>Whether the current frame uses the shared primary-depth page request entry.</summary>
    public bool UsesPrimaryDepthReceiverRequests { get; private set; }

    private void CreateDepthPageMarking(ref RenderGraphEdit edit, GraphRegionId region)
    {
        if (_shadowMap.DepthPageMarkPipeline is not { } pipeline) return;
        _depthReceiverViews = edit.CreateBufferInput(BufferUsages.ShaderRead);
        _receiverDepth = edit.CreateTextureInput();
        _depthMarkPass = AddComputePass(ref edit, "Mark virtual shadow pages from primary depth", pipeline,
            new VirtualShadowDispatchPassData(default), region);
    }

    private void BindDepthPageMarking(ref RenderGraphEdit edit)
    {
        if (_shadowMap.DepthPageMarkPipeline is not { } pipeline) return;
        PassDefinition pass = edit.GetPass(_depthMarkPass);
        foreach (var resource in pipeline.GlobalResources.Resources)
        {
            if (resource.Name == "ReceiverViews")
                pass.Bind(edit.CreateBufferSrv(_depthReceiverViews, structureStride: ClusterRenderContext.ViewSizeInBytes),
                    PipelineSync.ComputeShading);
            else if (resource.Name == "ReceiverDepth")
                pass.Bind(edit.CreateTextureSrv(_receiverDepth,
                    new TextureSubresourceRange(0, 1, 0, 1, TextureAspects.Depth), Format.D32Float,
                    SomeEngine.Graphics.TextureViewDimension.Texture2DArray), PipelineSync.ComputeShading);
            else if (TryResolve(resource.Name, resource.Type, out GraphResourceBinder binder) ||
                _lighting.TryResolve(resource.Name, resource.Type, out binder))
                binder(ref edit, ref pass, PipelineSync.ComputeShading);
            else
                throw new InvalidDataException($"Primary-depth shadow request resource '{resource.Name}' has no provider.");
        }
    }

    private void OrderDepthPageMarking(ref RenderGraphEdit edit)
    {
        if (_depthMarkPass == default) return;
        edit.OrderAfter(_depthMarkPass, _initializePass);
        if (_workUpdatePass != default) edit.OrderAfter(_depthMarkPass, _workUpdatePass);
        edit.OrderAfter(_poolPreparePass, _depthMarkPass);
        if (_prefilterRequestPass != default) edit.OrderAfter(_prefilterRequestPass, _depthMarkPass);
    }
}
