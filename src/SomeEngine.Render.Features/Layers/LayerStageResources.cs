using SomeEngine.Graphics;
using SomeEngine.RenderGraph;

namespace SomeEngine.Render.Features.Layers;

public readonly record struct LayerFragmentBuffers(GraphBufferId Blocks, GraphBufferId Payloads,
    GraphBufferId TileHeads, GraphBufferId FrameCounters,
    GraphBufferUavId BlocksUav, GraphBufferUavId PayloadsUav,
    GraphBufferUavId TileHeadsUav, GraphBufferUavId FrameCountersUav,
    GraphBufferSrvId BlocksSrv, GraphBufferSrvId PayloadsSrv,
    GraphBufferSrvId TileHeadsSrv, GraphBufferSrvId FrameCountersSrv);

/// <summary>Committed storage and separate graph regions for geometry and material shading.</summary>
public readonly record struct LayerStageResources(GraphRegionId VisibilityRegion,
    GraphRegionId ShadingRegion, GraphPassId VisibilityBegin, GraphPassId ShadingBegin,
    GraphBufferId StoreParameters, GraphBufferCbvId StoreParametersView,
    GraphBufferId CompositionParameters, GraphBufferCbvId CompositionParametersView,
    LayerFragmentBuffers Fragments, GraphTextureSrvId OpaqueDepthView,
    GraphBufferSrvId RasterMaterialsView = default, GraphBufferCbvId TessellationView = default)
{
    public uint ListCapacity { get; init; }
    public IGraphReadbackReceiver ListDiagnostics { get; init; } = null!;
    public GraphBufferSrvId RasterStageIndicesView { get; init; }

    public GraphParameterResourceBinding? ResolveVisibility(string name, ResourceBindingType type) =>
        Resolve(name, type, GraphAccessMode.Write, GraphAccessMode.ReadWrite);

    public GraphParameterResourceBinding? ResolveShading(string name, ResourceBindingType type) =>
        Resolve(name, type, GraphAccessMode.Read, GraphAccessMode.Read);

    private GraphParameterResourceBinding? Resolve(string name, ResourceBindingType type,
        GraphAccessMode blocks, GraphAccessMode sharedState) => name switch
    {
        "LayerFragmentStore" => GraphParameterResourceBinding.ConstantBuffer(StoreParametersView, PipelineSync.AllShading),
        "LayerCompositionUniforms" => GraphParameterResourceBinding.ConstantBuffer(CompositionParametersView, PipelineSync.AllShading),
        "LayerTessellation" => GraphParameterResourceBinding.ConstantBuffer(TessellationView, PipelineSync.AllShading),
        "LayerOpaqueDepth" => GraphParameterResourceBinding.SampledTexture(OpaqueDepthView, PipelineSync.AllShading),
        "LayerRasterMaterials" => GraphParameterResourceBinding.ReadOnlyBuffer(RasterMaterialsView, PipelineSync.AllShading),
        "LayerMaterialRasterStages" when RasterStageIndicesView != default =>
            GraphParameterResourceBinding.ReadOnlyBuffer(RasterStageIndicesView, PipelineSync.AllShading),
        "LayerFragmentBlocks" => Buffer(type, Fragments.BlocksSrv, Fragments.BlocksUav, blocks),
        "LayerTileHeads" => Buffer(type, Fragments.TileHeadsSrv, Fragments.TileHeadsUav, sharedState, BufferContentScope.FullRange),
        "LayerFrameCounters" => Buffer(type, Fragments.FrameCountersSrv, Fragments.FrameCountersUav, sharedState, BufferContentScope.FullRange),
        "LayerFragmentPayloads" => Buffer(type, Fragments.PayloadsSrv, Fragments.PayloadsUav, GraphAccessMode.Write),
        _ => null,
    };

    private static GraphParameterResourceBinding Buffer(ResourceBindingType type,
        GraphBufferSrvId srv, GraphBufferUavId uav, GraphAccessMode mode,
        BufferContentScope scope = BufferContentScope.CountBoundedRanges) => type == ResourceBindingType.BufferSrv
        ? GraphParameterResourceBinding.ReadOnlyBuffer(srv, PipelineSync.AllShading, scope)
        : GraphParameterResourceBinding.WritableBuffer(uav, PipelineSync.AllShading, mode,
            WriteCoverage.Partial, mode == GraphAccessMode.Write ? ResourceContentState.Defined : null, scope);
}

public readonly record struct LayerStagePasses(GraphPassId VisibilityFirst, GraphPassId VisibilityComplete,
    GraphPassId ShadingFirst, GraphPassId ShadingComplete)
{
    public LayerShadingResources ShadingBins { get; init; }
    public LayerMaterialShadingResources MaterialShading { get; init; }
    public LayerTileBinningResources TileBinning { get; init; }
    public LayerSoftwareRasterResources SoftwareRaster { get; init; }
    public LayerHardwareBinningResources HardwareBinning { get; init; }
    public LayerHardwareRasterResources HardwareRaster { get; init; }
    public LayerStagePasses(GraphPassId visibility, GraphPassId shading) : this(visibility, visibility, shading, shading) { }
}

/// <summary>Registers passes from supplied graph IDs and returns their stage boundaries.</summary>
public delegate LayerStagePasses LayerStageRegistration(ref RenderGraphEdit edit, in LayerStageResources resources);
