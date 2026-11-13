using SomeEngine.Graphics;
using SomeEngine.Render.Shadows;
using SomeEngine.RenderGraph;
using System.Numerics;

namespace SomeEngine.Render.Features.Shadows;

internal sealed class VirtualShadowRequests(VirtualShadowFeature feature)
{
    private readonly VirtualShadowFeature _feature = feature;

    internal GraphBufferId _requestedPageFlags;
    internal GraphBufferId _requestedPageIndices;
    internal GraphBufferId _requestedPageCount;
    internal GraphTextureId _pageReceiverMasks;
    internal uint _requestFlagEntryCapacity;

    internal void Install(ref RenderGraphEdit edit, uint pageTableEntryCapacity)
    {
        _requestFlagEntryCapacity = pageTableEntryCapacity;
        _requestedPageFlags = edit.CreatePersistentBuffer(new BufferDesc(
            checked((ulong)pageTableEntryCapacity * sizeof(uint)),
            BufferUsages.ShaderRead | BufferUsages.ShaderWrite,
            "Virtual shadow request flags"));
        _requestedPageIndices = edit.CreatePersistentBuffer(new BufferDesc(
            checked((ulong)pageTableEntryCapacity * sizeof(uint)),
            BufferUsages.ShaderRead | BufferUsages.ShaderWrite,
            "Virtual shadow requested page indices"));
        _requestedPageCount = edit.CreatePersistentBuffer(new BufferDesc(
            sizeof(uint),
            BufferUsages.ShaderRead | BufferUsages.ShaderWrite | BufferUsages.CopyDestination | BufferUsages.CopySource,
            "Virtual shadow requested page count"));
        _pageReceiverMasks = edit.CreatePersistentTexture(ReceiverMaskDescription);
    }

    /// <summary>Contains caller position evaluation and direct shadow page requests.</summary>
    public GraphRegionId ShadowPageRequests { get; private set; }

    internal void ConfigureShadowRequests(ref RenderGraphEdit edit, GraphRegionId parent)
    {
        ShadowPageRequests = edit.AddRegion("Virtual shadow caller page requests", parent);
    }

    internal void BindReceiverPageDemand(ref RenderGraphEdit graph, ref PassDefinition pass, PipelineSync sync)
    {
        _ = pass.Bind(_feature._demandUniforms, sync);
        graph.OrderAfter(pass.Id, _feature._requestResetPass);
        if (_feature._prefilter._prefilterRequestPass != default) graph.OrderAfter(_feature._prefilter._prefilterRequestPass, pass.Id);
        if (_feature._prefilter._prefilterRequestCapturePass != default) graph.OrderAfter(_feature._prefilter._prefilterRequestCapturePass, pass.Id);
        graph.OrderAfter(_feature._poolPreparePass, pass.Id);
    }

    internal void BindReceiverShadowViews(ref RenderGraphEdit graph, ref PassDefinition pass, PipelineSync sync)
    {
        _ = pass.Bind(graph.CreateBufferSrv(_feature._frameShadowViews, structureStride: VirtualShadowView.SizeInBytes,
            label: "Virtual shadow demand views"), sync);
        if (_feature._quality._workUpdatePass != default) graph.OrderAfter(pass.Id, _feature._quality._workUpdatePass);
    }

    internal void BindRequestedPageFlags(ref RenderGraphEdit graph, ref PassDefinition pass, PipelineSync sync) =>
        _ = pass.Bind(graph.CreateBufferUav(_requestedPageFlags, structureStride: sizeof(uint),
            label: "Virtual shadow request flags"), sync,
            GraphAccessMode.ReadWrite, WriteCoverage.Partial);

    internal void BindRequestedPageIndices(ref RenderGraphEdit graph, ref PassDefinition pass, PipelineSync sync) =>
        _ = pass.Bind(graph.CreateBufferUav(_requestedPageIndices, structureStride: sizeof(uint),
            label: "Virtual shadow requested page indices"), sync,
            GraphAccessMode.Write, WriteCoverage.Partial,
            resultContents: ResourceContentState.Defined, contentScope: BufferContentScope.CountBoundedPrefix);

    internal void BindRequestedPageCount(ref RenderGraphEdit graph, ref PassDefinition pass, PipelineSync sync) =>
        _ = pass.Bind(graph.CreateBufferUav(_requestedPageCount, structureStride: sizeof(uint),
            label: "Virtual shadow requested page count"), sync,
            GraphAccessMode.ReadWrite, WriteCoverage.Partial);

    internal void BindPageReceiverMasks(ref RenderGraphEdit graph, ref PassDefinition pass, PipelineSync sync) =>
        _ = pass.Bind(graph.CreateTextureUav(_pageReceiverMasks,
            new TextureSubresourceRange(0, 1, 0, 1, TextureAspects.Color), Format.R32UInt,
            SomeEngine.Graphics.TextureViewDimension.Texture2D, label: "Virtual shadow page receiver masks"), sync,
            GraphAccessMode.ReadWrite, WriteCoverage.Partial);


    internal uint ReceiverMaskColumns => CalculateReceiverMaskColumns(_feature._sourceViewCapacity);
    internal uint ReceiverMaskWidth => ReceiverMaskColumns * _feature._shadowMap.Settings.VirtualPagesPerRow;
    internal uint ReceiverMaskHeight => VirtualShadowFeature.DivideRoundUp(_feature._sourceViewCapacity, ReceiverMaskColumns) * _feature._shadowMap.Settings.VirtualPagesPerRow * 3u;
    internal uint ReceiverMaskMipCount => Math.Min(checked((uint)BitOperations.Log2(_feature._shadowMap.Settings.VirtualPagesPerRow) + 1u), 8u);

    internal TextureDesc ReceiverMaskDescription => CreateReceiverMaskDescription(_feature._sourceViewCapacity);

    internal TextureDesc CreateReceiverMaskDescription(uint sourceViews) =>
        CreateReceiverMaskDescription(sourceViews, _feature._shadowMap.Settings);

    internal TextureDesc CreateReceiverMaskDescription(uint sourceViews, VirtualShadowMapSettings settings)
    {
        uint columns = CalculateReceiverMaskColumns(sourceViews);
        uint pages = settings.VirtualPagesPerRow;
        return new(TextureDimension.Texture2D, columns * pages,
            VirtualShadowFeature.DivideRoundUp(sourceViews, columns) * pages * 3u,
            1, Math.Min(checked((uint)BitOperations.Log2(pages) + 1u), 8u), 1, 1, Format.R32UInt,
            TextureUsages.Sampled | TextureUsages.Storage | TextureUsages.CopySource,
            label: "Virtual shadow receiver masks and coarse cells");
    }

    private static uint CalculateReceiverMaskColumns(uint sourceViews) =>
        checked((uint)Math.Ceiling(Math.Sqrt(sourceViews * 3.0)));

    internal void BindReceiverMaskMips(ref RenderGraphEdit graph, ref PassDefinition pass,
        GraphAccessMode access, WriteCoverage coverage, bool atomic = false)
    {
        for (uint mip = 1; mip <= 7; mip++)
            _ = pass.Bind(graph.CreateTextureUav(_pageReceiverMasks,
                new TextureSubresourceRange(Math.Min(mip, ReceiverMaskMipCount - 1u), 1, 0, 1, TextureAspects.Color),
                Format.R32UInt, SomeEngine.Graphics.TextureViewDimension.Texture2D),
                PipelineSync.ComputeShading, access, coverage,
                unorderedAccessHazards: atomic ? UnorderedAccessHazardMode.AtomicMergeable : UnorderedAccessHazardMode.Ordered);
    }
    internal void BindReceiverMaskReset(ref RenderGraphEdit graph)
    {
        const PipelineSync sync = PipelineSync.ComputeShading;
        var resources = new Dictionary<string, GraphParameterResourceBinding>
        {
            ["ReceiverReset"] = GraphParameterResourceBinding.ConstantBuffer(_feature._coarseDemandUniforms, sync),
            ["ShadowViews"] = GraphParameterResourceBinding.ReadOnlyBuffer(graph.CreateBufferSrv(_feature._frameShadowViews, structureStride: VirtualShadowView.SizeInBytes), sync),
            ["ShadowRanges"] = GraphParameterResourceBinding.ReadOnlyBuffer(graph.CreateBufferSrv(_feature._frameShadowRanges, structureStride: VirtualShadowRange.SizeInBytes), sync),
            ["PageRequestDispatchArgs"] = GraphParameterResourceBinding.ReadOnlyBuffer(graph.CreateBufferSrv(_feature._pageRequestDispatchArguments), sync),
            ["RequestedPageIndices"] = GraphParameterResourceBinding.ReadOnlyBuffer(graph.CreateBufferSrv(_requestedPageIndices, structureStride: 4), sync, BufferContentScope.CountBoundedPrefix),
            ["PageReceiverMasks"] = GraphParameterResourceBinding.StorageTexture(graph.CreateTextureUav(_pageReceiverMasks,
                new(0, 1, 0, 1, TextureAspects.Color), Format.R32UInt, TextureViewDimension.Texture2D), sync, GraphAccessMode.Write),
            ["RequestedPageFlags"] = GraphParameterResourceBinding.WritableBuffer(graph.CreateBufferUav(_requestedPageFlags, structureStride: 4), sync, GraphAccessMode.Write),
        };
        for (uint mip = 1; mip <= 7; mip++)
            resources.Add("PageReceiverMaskMip" + mip, GraphParameterResourceBinding.StorageTexture(graph.CreateTextureUav(_pageReceiverMasks,
                new(Math.Min(mip, ReceiverMaskMipCount - 1u), 1, 0, 1, TextureAspects.Color), Format.R32UInt, TextureViewDimension.Texture2D), sync, GraphAccessMode.Write));
        _feature.BindComputeResources(ref graph, _feature._receiverMaskResetPass, resources);
    }


}
