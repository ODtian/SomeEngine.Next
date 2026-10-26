using SomeEngine.Graphics;
using SomeEngine.Render.Shadows;
using SomeEngine.RenderGraph;

namespace SomeEngine.Render.Features.Shadows;

public sealed partial class VirtualShadowFeature
{
    /// <summary>Contains caller position evaluation and direct shadow page requests.</summary>
    public GraphRegionId ShadowPageRequests { get; private set; }

    private void ConfigureShadowRequests(ref RenderGraphEdit edit, GraphRegionId parent)
    {
        ShadowPageRequests = edit.AddRegion("Virtual shadow caller page requests", parent);
        if (_shadowMap.Settings.Enabled)
            _geometry.ConfigureShadowRequests(edit, ShadowPageRequests);
    }

    private void BindReceiverPageDemand(ref RenderGraphEdit graph, ref PassDefinition pass, PipelineSync sync)
    {
        _ = pass.Bind(_demandUniforms, sync);
        graph.OrderAfter(pass.Id, _requestResetPass);
        if (_prefilterRequestPass != default) graph.OrderAfter(_prefilterRequestPass, pass.Id);
        graph.OrderAfter(_poolPreparePass, pass.Id);
    }

    private void BindReceiverShadowViews(ref RenderGraphEdit graph, ref PassDefinition pass, PipelineSync sync)
    {
        _ = pass.Bind(graph.CreateBufferSrv(_frameShadowViews, structureStride: VirtualShadowView.SizeInBytes,
            label: "Virtual shadow demand views"), sync);
        if (_workUpdatePass != default) graph.OrderAfter(pass.Id, _workUpdatePass);
    }

    private void BindRequestedPageFlags(ref RenderGraphEdit graph, ref PassDefinition pass, PipelineSync sync) =>
        _ = pass.Bind(graph.CreateBufferUav(_requestedPageFlags, structureStride: sizeof(uint),
            label: "Virtual shadow request flags"), sync,
            GraphAccessMode.ReadWrite, WriteCoverage.Partial);

    private void BindRequestedPageIndices(ref RenderGraphEdit graph, ref PassDefinition pass, PipelineSync sync) =>
        _ = pass.Bind(graph.CreateBufferUav(_requestedPageIndices, structureStride: sizeof(uint),
            label: "Virtual shadow requested page indices"), sync,
            GraphAccessMode.Write, WriteCoverage.Partial,
            resultContents: ResourceContentState.Defined, contentScope: BufferContentScope.CountBoundedPrefix);

    private void BindRequestedPageCount(ref RenderGraphEdit graph, ref PassDefinition pass, PipelineSync sync) =>
        _ = pass.Bind(graph.CreateBufferUav(_requestedPageCount, structureStride: sizeof(uint),
            label: "Virtual shadow requested page count"), sync,
            GraphAccessMode.ReadWrite, WriteCoverage.Partial);

    private void BindPageReceiverMasks(ref RenderGraphEdit graph, ref PassDefinition pass, PipelineSync sync) =>
        _ = pass.Bind(graph.CreateTextureUav(_pageReceiverMasks,
            new TextureSubresourceRange(0, 1, 0, 1, TextureAspects.Color), Format.R32UInt,
            SomeEngine.Graphics.TextureViewDimension.Texture2D, label: "Virtual shadow page receiver masks"), sync,
            GraphAccessMode.ReadWrite, WriteCoverage.Partial);
}
