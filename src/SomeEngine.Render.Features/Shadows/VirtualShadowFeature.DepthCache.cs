using SomeEngine.Graphics;
using SomeEngine.Render.Assets;
using SomeEngine.RenderGraph;
using SomeEngine.Resources;

namespace SomeEngine.Render.Features.Shadows;

public sealed partial class VirtualShadowFeature
{
    private GraphTextureId _staticAtlas;
    private GraphPassId _staticAtlasInitializePass;
    private GraphPassId _staticMergeIntermediatePass;
    private GraphPassId _staticMergeFinalPass;
    private LinkedComputePipeline? _staticMergePipeline;

    private void InstallStaticDepth(ref RenderGraphEdit edit, VirtualShadowMapShaders shaders,
        GraphRegionId prepare, GraphRegionId finish)
    {
        var settings = _shadowMap.Settings;
        _staticAtlas = edit.DeclareExternalTexture(new TextureDesc(TextureDimension.Texture2D,
            settings.AtlasSize, settings.AtlasSize, 1, 1, 1, 1, Format.R32UInt,
            TextureUsages.Sampled | TextureUsages.Storage | TextureUsages.ColorAttachment,
            label: "Virtual shadow static depth"));
        var initialize = new VirtualShadowAtlasInitializePassData(_staticAtlas);
        _staticAtlasInitializePass = edit.AddCopyFramePass("Initialize virtual shadow static depth",
            PassQueueSelection.AnyOfType(QueueType.Graphics), initialize,
            new PassOptions(Culling: PassCullingMode.NeverCull, RepeatCommands: true),
            VirtualShadowAtlasInitializePassData.Declare, VirtualShadowAtlasInitializePassData.Record, region: prepare);
        edit.OrderAfter(_initializePass, _staticAtlasInitializePass);
        _staticMergePipeline = LinkedComputePipeline.Create(_backend, _device, shaders.MergeStaticShader,
            shaders.MergeStatic, "Virtual shadow static depth merge");
        _staticMergeIntermediatePass = AddIndirectPass(ref edit, "Merge main virtual shadow static depth",
            _staticMergePipeline, _pageWorkDispatchArguments, region: _geometry.IntermediateOcclusionRegion);
        _staticMergeFinalPass = AddIndirectPass(ref edit, "Merge post virtual shadow static depth",
            _staticMergePipeline, _pageWorkDispatchArguments, region: finish);
        foreach (GraphPassId id in new[] { _staticMergeIntermediatePass, _staticMergeFinalPass })
        {
            PassDefinition pass = edit.GetPass(id);
            _ = pass.Bind(_clearUniforms, PipelineSync.ComputeShading);
            _ = pass.Bind(edit.CreateBufferSrv(_activePages, structureStride: 16), PipelineSync.ComputeShading,
                BufferContentScope.CountBoundedPrefix);
            _ = pass.Bind(edit.CreateBufferSrv(_activePageCount, structureStride: sizeof(uint)), PipelineSync.ComputeShading);
            _ = pass.Bind(edit.CreateBufferSrv(_pageMetadata, structureStride: 16), PipelineSync.ComputeShading);
            _ = pass.Bind(edit.CreateTextureUav(_atlas, format: Format.R32UInt,
                dimension: SomeEngine.Graphics.TextureViewDimension.Texture2D), PipelineSync.ComputeShading,
                GraphAccessMode.ReadWrite, WriteCoverage.Partial);
            _ = pass.Bind(edit.CreateTextureSrv(_staticAtlas, format: Format.R32UInt,
                dimension: SomeEngine.Graphics.TextureViewDimension.Texture2D), PipelineSync.ComputeShading);
        }
        edit.OrderAfter(_pageHiZInitializePass, _staticMergeIntermediatePass);
        edit.OrderAfter(_finalPageHiZInitializePass, _staticMergeFinalPass);
    }
}
