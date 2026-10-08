using System.Numerics;
using SomeEngine.Graphics;
using SomeEngine.RenderGraph;

namespace SomeEngine.Render.Features.Shadows;

public sealed partial class VirtualShadowFeature
{
    private uint ReceiverMaskColumns => checked((uint)Math.Ceiling(Math.Sqrt(_sourceViewCapacity * 1.5)));
    private uint ReceiverMaskWidth => ReceiverMaskColumns * _shadowMap.Settings.VirtualPagesPerRow * 2u;
    private uint ReceiverMaskHeight => DivideRoundUp(_sourceViewCapacity, ReceiverMaskColumns) * _shadowMap.Settings.VirtualPagesPerRow * 3u;
    private uint ReceiverMaskMipCount => Math.Min(checked((uint)BitOperations.Log2(_shadowMap.Settings.VirtualPagesPerRow) + 1u), 8u);

    private TextureDesc ReceiverMaskDescription => new(TextureDimension.Texture2D,
        ReceiverMaskWidth, ReceiverMaskHeight, 1, ReceiverMaskMipCount, 1, 1,
        Format.R32UInt, TextureUsages.Sampled | TextureUsages.Storage | TextureUsages.CopySource,
        label: "Virtual shadow receiver masks and coarse cells");

    private void BindReceiverMaskMips(ref RenderGraphEdit graph, ref PassDefinition pass,
        GraphAccessMode access, WriteCoverage coverage, bool atomic = false)
    {
        for (uint mip = 1; mip <= 7; mip++)
            _ = pass.Bind(graph.CreateTextureUav(_pageReceiverMasks,
                new TextureSubresourceRange(Math.Min(mip, ReceiverMaskMipCount - 1u), 1, 0, 1, TextureAspects.Color),
                Format.R32UInt, SomeEngine.Graphics.TextureViewDimension.Texture2D),
                PipelineSync.ComputeShading, access, coverage,
                unorderedAccessHazards: atomic ? UnorderedAccessHazardMode.AtomicMergeable : UnorderedAccessHazardMode.Ordered);
    }
    private void BindReceiverMaskReset(ref RenderGraphEdit graph)
    {
        PassDefinition pass = graph.GetPass(_receiverMaskResetPass);
        _ = pass.Bind(_coarseDemandUniforms, PipelineSync.ComputeShading);
        _ = pass.Bind(graph.CreateBufferSrv(_frameShadowViews, structureStride: global::SomeEngine.Render.Shadows.VirtualShadowView.SizeInBytes), PipelineSync.ComputeShading);
        _ = pass.Bind(graph.CreateBufferSrv(_pageRequestDispatchArguments), PipelineSync.ComputeShading);
        _ = pass.Bind(graph.CreateBufferSrv(_requestedPageIndices, structureStride: sizeof(uint)),
            PipelineSync.ComputeShading, BufferContentScope.CountBoundedPrefix);
        _ = pass.Bind(graph.CreateTextureUav(_pageReceiverMasks, new TextureSubresourceRange(0, 1, 0, 1, TextureAspects.Color),
            Format.R32UInt, SomeEngine.Graphics.TextureViewDimension.Texture2D),
            PipelineSync.ComputeShading, GraphAccessMode.Write, WriteCoverage.Partial);
        BindReceiverMaskMips(ref graph, ref pass, GraphAccessMode.Write, WriteCoverage.Partial);
    }

}
