using SomeEngine.Render;
using System.Numerics;
using System.Runtime.InteropServices;
using SomeEngine.Assets;
using SomeEngine.Assets.Schema;
using SomeEngine.Resources;
using SomeEngine.Graphics;
using SomeEngine.Render.Assets;
using SomeEngine.Render.Components;
using SomeEngine.Render.Features.Lighting;
using SomeEngine.Render.Lighting;
using SomeEngine.Render.Instances;
using SomeEngine.Render.Shadows;
using SomeEngine.RenderGraph;
using SomeEngine.ECS.Entities;
using SlangShaderSharp;

namespace SomeEngine.Render.Features.Shadows;

/// <summary>
/// Virtual-shadow page processing and generic virtual-depth geometry integration.
/// </summary>
public sealed class VirtualShadowFeature : IGraphResourceProvider, IDisposable
{

    internal readonly RenderPipelines _renderPipelines;
    private readonly Dictionary<GraphPassId, LinkedComputePipeline> _computePasses = [];
    internal FeatureUniformBuffer _uniformData;
    internal readonly VirtualShadowRequests _requests;
    internal readonly VirtualShadowPrefilter _prefilter;
    internal readonly VirtualShadowQuality _quality;
    internal readonly ShadowCasterCache _casters;
    internal readonly VirtualShadowOpaqueRequests _opaqueRequests;

    internal readonly GraphBufferInput _initUniforms;
    internal readonly GraphBufferInput _invalidationUniforms;
    internal readonly GraphBufferInput _coarseDemandUniforms;
    internal readonly GraphBufferInput _demandUniforms;
    internal readonly GraphBufferInput _poolUniforms;
    internal readonly GraphBufferInput _poolPrepareUniforms;
    internal readonly GraphBufferInput _poolFinishUniforms;
    internal readonly GraphBufferInput _allocationUniforms;
    internal readonly GraphBufferInput _clearUniforms;
    internal readonly GraphBufferInput _dynamicResolutionUniforms;
    internal readonly GraphBufferInput _pageHiZInitializeUniforms;
    internal GraphBufferInput[] _pageHiZReduceUniforms;
    internal readonly GraphBufferInput _casterBounds;

    internal const uint DispatchStride = 16u;
    internal const uint PageMetadataStride = VirtualShadowPageMetadata.SizeInBytes;
    internal uint PageHiZPagesPerRow => Math.Min(_shadowMap.Settings.AtlasSize / _shadowMap.Settings.PageSize, 8u);
    internal uint PageHiZBaseLayerCount => DivideRoundUp(_shadowMap.Settings.MaxPages, PageHiZPagesPerRow * PageHiZPagesPerRow);
    internal uint PageHiZStaticLayerOffset => _shadowMap.Settings.DynamicHiZ ? PageHiZBaseLayerCount : 0u;
    internal uint PageHiZLayerCount => PageHiZBaseLayerCount * (_shadowMap.Settings.DynamicHiZ ? 2u : 1u);

    internal readonly IGraphicsBackend _backend;
    internal readonly Device _device;
    internal readonly global::SomeEngine.RenderGraph.RenderGraph _renderGraph;
    internal readonly VirtualShadowPages _shadowMap;
    internal readonly IRenderGeometryData _geometry;
    internal readonly GraphRegionId _targetOcclusion;
    internal readonly GraphRegionId _primaryDepthAfter;
    private readonly GraphRegionId _receiverPrepare;
    internal VirtualShadowViews _viewEntities;
    internal readonly LightingFeature _lighting;
    internal readonly IndirectCommandLayout _dispatchLayout;

    internal readonly GraphBufferInput _geometryWorkStatus;
    internal readonly GraphTextureId _pageHiZ;

    internal uint _sourceViewCapacity;
    internal float[] _baseShadowBias = [];
    private RenderView[] _shadowViews = [];
    private VirtualShadowView[] _previousShadowViews = [];
    private Matrix4x4[] _previousShadowViewProjections = [];
    private PageTableRange[] _viewRanges = [];
    private uint[] _rangeEntryCounts = [];
    private VirtualShadowRange[] _rangeViews = [];
    internal readonly GraphBufferId _activePages;
    internal readonly GraphBufferId _activePageCount;
    internal readonly GraphBufferId _uncachedPageRectBounds;
    internal readonly GraphBufferId _uncachedPageHierarchy;
    internal readonly GraphBufferId _allocatedPageHierarchy;
    internal readonly GraphBufferId _allocatedPageRectBounds;
    internal readonly GraphBufferId _rasterDispatchArguments;
    internal readonly GraphBufferId _pageWorkDispatchArguments;
    internal readonly GraphBufferId _pageRequestDispatchArguments;
    internal readonly GraphBufferId _allocationFailures;
    internal readonly GraphBufferId _policyRasterWorkCount;
    internal readonly GraphBufferId _depthWorkCount;
    internal readonly GraphBufferId _dynamicResolutionBias;
    internal readonly GraphBufferId _frameResolutionBias;
    internal readonly GraphBufferId _rasterDispatchViews;
    internal readonly GraphBufferId _rasterTargetViews;
    internal readonly GraphBufferInput _clusterSourceViews;

    internal readonly GraphPassId _frameResetPass;
    internal readonly GraphPassId _receiverMaskResetPass;
    internal readonly GraphPassId _requestResetPass;
    internal readonly GraphPassId _dynamicResolutionResetPass;
    internal readonly GraphPassId _resolutionCopyPass;
    internal readonly GraphPassId _atlasInitializePass;
    internal GraphPassId _pageHiZStorageInitializePass;
    internal readonly GraphPassId _initializePass;
    private readonly GraphRegionId _casterExpansionRegion;
    private readonly IReadOnlyDictionary<string, GraphParameterResourceBinding> _casterBindings;
    private Shader _casterShader;
    internal readonly GraphPassId _coarseMarkPass;
    internal readonly GraphPassId _poolPreparePass;
    internal VirtualShadowPageCompactionResources _poolCompaction;
    private GraphBufferAccessId[] _pageListAccesses = [];
    internal readonly GraphPassId _poolFinishPass;
    internal readonly GraphPassId _poolPass;
    internal readonly GraphPassId _allocatePass;
    internal readonly GraphPassId _clearPass;
    internal readonly GraphPassId _compactViewsPass;
    internal readonly GraphPassId _dynamicResolutionPass;
    internal GraphPassId _pageHiZInitializePass;
    internal GraphPassId[] _pageHiZReducePasses;
    internal GraphPassId _finalPageHiZInitializePass;
    internal GraphPassId[] _finalPageHiZReducePasses;
    internal readonly GraphPassId _rasterCompletePass;
    internal readonly GraphPassId _frameCompletePass;

    internal readonly GraphBufferId _shadowViewBuffer;
    internal readonly GraphBufferInput _frameShadowViews;
    internal readonly GraphBufferInput _frameShadowRanges;
    internal readonly GraphBufferInput _frameLightViewIndices;
    internal readonly GraphParameterInput _blueNoiseInput;
    internal readonly SomeEngine.Resources.Texture _blueNoise;
    internal readonly RenderDevice _textureDevice;
    internal readonly GraphBufferInput _frameSamplingUniforms;
    internal readonly GraphBufferInput _frameDispatchUniforms;
    internal uint _submittedFrameIndex;
    internal int _submittedViewCount;
    internal bool _disposed;
    internal RenderInstanceBatch? _historyInstances;
    internal ulong _historyInstanceRevision;
    internal ulong _historyCasterRevision;
    internal readonly HashSet<uint> _deformedInstances = [];
    internal bool _invalidateAllCasters;
    internal readonly GraphBufferInput _frameChangedInstances;

    /// <summary>
    /// Reports changes to externally driven vertex, domain or opacity inputs before the next
    /// frame. IDs are rows in CurrentInstances; the declared expansion must cover both states.
    /// Material slot edits and instance uploads are tracked automatically.
    /// </summary>
    public void InvalidateCasters(ReadOnlySpan<uint> instanceIds)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        foreach (uint id in instanceIds)
            if (id >= _geometry.CurrentInstanceCount)
                throw new ArgumentOutOfRangeException(nameof(instanceIds));
        foreach (uint id in instanceIds)
            _deformedInstances.Add(id);
    }

    /// <summary>Invalidates cached depth when external inputs change without a bounded caster footprint.</summary>
    public void InvalidateAllCasters()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _invalidateAllCasters = true;
    }

    public VirtualShadowFeature(
        IGraphicsBackend backend,
        Device device,
        RenderPipelines pipelines,
        VirtualShadowMapShaders shaders,
        VirtualShadowMapSettings settings,
        global::SomeEngine.RenderGraph.RenderGraph graph,
        IRenderGeometryData geometry,
        LightingFeature lighting,
        GraphRegionId primaryDepthAfter,
        GraphRegionId targetOcclusion,
        GraphRegionId targetGeometryAfter,
        GraphRegionId feedback,
        RenderDevice textureDevice, GraphRegionId receiverPrepare = default)
    {

        _renderPipelines = pipelines;
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        _device = device ?? throw new ArgumentNullException(nameof(device));
        ArgumentNullException.ThrowIfNull(shaders);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(graph);
        _renderGraph = graph;
        _blueNoise = shaders.BlueNoise ?? throw new ArgumentException(
            "Virtual shadow shaders require a blue-noise texture.", nameof(shaders));
        _textureDevice = textureDevice;
        _textureDevice.PrepareTexture(_blueNoise);
        _geometry = geometry ?? throw new ArgumentNullException(nameof(geometry));
        _targetOcclusion = targetOcclusion;
        _primaryDepthAfter = primaryDepthAfter;
        _receiverPrepare = receiverPrepare == default ? primaryDepthAfter : receiverPrepare;
        _requests = new VirtualShadowRequests(this);
        _prefilter = new VirtualShadowPrefilter(this);
        _quality = new VirtualShadowQuality(this);
        _quality._qualityRegion = targetGeometryAfter;
        _quality._qualityDemandRegion = primaryDepthAfter;
        _casters = new ShadowCasterCache(this);
        _opaqueRequests = new VirtualShadowOpaqueRequests(this, shaders.OpaqueRequestsShader!, shaders.OpaqueRequests);

        _viewEntities = new VirtualShadowViews(geometry.World, settings);
        _lighting = lighting ?? throw new ArgumentNullException(nameof(lighting));
        if (!ReferenceEquals(graph.Device, device))
            throw new ArgumentException("Virtual shadows and RenderGraph must use the same Device.", nameof(graph));
        settings.Validate();
        VirtualShadowPages? shadowMap = null;
        IndirectCommandLayout? dispatchLayout = null;
        try
        {
            shadowMap = new VirtualShadowPages(backend, device, pipelines, shaders, settings);
            dispatchLayout = backend.CreateIndirectCommandLayout(
                device,
                new IndirectCommandLayoutDesc(
                    [new IndirectArgumentDesc(IndirectArgumentType.Dispatch)],
                    DispatchStride,
                    label: "Virtual shadow dispatch indirect layout"));
            _shadowMap = shadowMap;
            _dispatchLayout = dispatchLayout;

            GraphRegionId casterRegion;
            IReadOnlyDictionary<string, GraphParameterResourceBinding> casterBindings;
            RenderGraphEdit edit = graph.BeginEdit();
            try
            {
                _blueNoiseInput = edit.CreateParameterInput(ResourceBindingType.TextureSrv);
                _requests.ConfigureShadowRequests(ref edit, primaryDepthAfter);
                ulong pageBytes = checked((ulong)settings.MaxPages * sizeof(uint));
                _shadowMap.Install(ref edit);
                int pageHiZMipCount = checked(BitOperations.Log2(Math.Max(settings.PageSize / 2u, 1u)) + 1);
                _pageHiZ = edit.CreatePersistentTexture(new TextureDesc(
                    TextureDimension.Texture2D,
                    Math.Max(settings.PageSize / 2u, 1u) * PageHiZPagesPerRow,
                    Math.Max(settings.PageSize / 2u, 1u) * PageHiZPagesPerRow,
                    1,
                    checked((uint)pageHiZMipCount),
                    PageHiZLayerCount,
                    1,
                    Format.R32Float,
                    TextureUsages.Sampled | TextureUsages.Storage | TextureUsages.ColorAttachment,
                    label: "Virtual shadow physical-page HiZ"));

                _activePages = edit.CreateBuffer(new BufferDesc(
                    checked(pageBytes * 4u),
                    BufferUsages.ShaderRead | BufferUsages.ShaderWrite | BufferUsages.CopySource,
                    "Virtual shadow active raster pages"));
                _activePageCount = Counter(edit, "Virtual shadow active page count");
                uint sourceViewCapacity = checked(settings.MaxShadowLights * settings.MaximumViewsPerLight);
                _sourceViewCapacity = sourceViewCapacity;
                ResizeViewWorkArrays(sourceViewCapacity);
                uint pageTableEntryCapacity = checked(
                    sourceViewCapacity * settings.PageTableEntriesPerView);
                uint rasterHierarchyWordCapacity = checked(
                    sourceViewCapacity * settings.PageHierarchyWordsPerView);
                uint rasterViewCapacity = checked(sourceViewCapacity * settings.MipLevelCount);
                _requests.Install(ref edit, pageTableEntryCapacity);
                _uncachedPageRectBounds = edit.CreateBuffer(new BufferDesc(
                    checked((ulong)rasterViewCapacity * 4u * sizeof(uint)),
                    BufferUsages.ShaderRead | BufferUsages.ShaderWrite,
                    "Virtual shadow uncached page rect bounds"));
                _uncachedPageHierarchy = edit.CreateBuffer(new BufferDesc(
                    checked((ulong)rasterHierarchyWordCapacity * 2u * sizeof(uint)),
                    BufferUsages.ShaderRead | BufferUsages.ShaderWrite,
                    "Virtual shadow uncached page hierarchy"));
                _allocatedPageRectBounds = edit.CreateBuffer(new BufferDesc(
                    checked((ulong)rasterViewCapacity * 4u * sizeof(uint)),
                    BufferUsages.ShaderRead | BufferUsages.ShaderWrite,
                    "Virtual shadow allocated page rect bounds"));
                _allocatedPageHierarchy = edit.CreateBuffer(new BufferDesc(
                    checked((ulong)rasterHierarchyWordCapacity * sizeof(uint)),
                    BufferUsages.ShaderRead | BufferUsages.ShaderWrite,
                    "Virtual shadow allocated page hierarchy"));
                _rasterDispatchArguments = DispatchBuffer(
                    edit,
                    "Virtual shadow Cluster invocation arguments");
                _pageWorkDispatchArguments = DispatchBuffer(
                    edit,
                    "Virtual shadow page-work dispatch arguments",
                    checked((uint)pageHiZMipCount));
                _pageRequestDispatchArguments = DispatchBuffer(
                    edit,
                    "Virtual shadow requested-page dispatch arguments");
                _allocationFailures = edit.CreateBuffer(new BufferDesc(
                    3u * sizeof(uint),
                    BufferUsages.ShaderRead | BufferUsages.ShaderWrite | BufferUsages.CopyDestination | BufferUsages.CopySource,
                    "Virtual shadow allocation failures"));
                _policyRasterWorkCount = edit.CreateBuffer(new BufferDesc(
                    checked((1ul + 2ul * rasterViewCapacity / settings.MipLevelCount) * sizeof(uint)),
                    BufferUsages.ShaderRead | BufferUsages.ShaderWrite | BufferUsages.CopyDestination | BufferUsages.CopySource,
                    "Virtual shadow policy raster work count"));
                _depthWorkCount = edit.CreateBuffer(new BufferDesc(checked((ulong)sourceViewCapacity * 4u * sizeof(uint)),
                    BufferUsages.ShaderRead | BufferUsages.ShaderWrite | BufferUsages.CopySource | BufferUsages.CopyDestination,
                    "Virtual shadow static/dynamic visible work"));
                _dynamicResolutionBias = edit.CreatePersistentBuffer(new BufferDesc(
                    2u * sizeof(float),
                    BufferUsages.ShaderRead | BufferUsages.ShaderWrite | BufferUsages.CopySource | BufferUsages.CopyDestination,
                    "Virtual shadow dynamic resolution bias"));
                _frameResolutionBias = edit.CreateBuffer(new BufferDesc(sizeof(float),
                    BufferUsages.ShaderRead | BufferUsages.CopyDestination | BufferUsages.CopySource,
                    "Virtual shadow resolution bias for this frame"));
                _rasterDispatchViews = edit.CreateBuffer(new BufferDesc(
                    checked((ulong)rasterViewCapacity * (uint)RenderRasterContext.ViewSizeInBytes),
                    BufferUsages.ShaderRead | BufferUsages.ShaderWrite,
                    "Virtual shadow raster invocation views"));
                _rasterTargetViews = edit.CreateBuffer(new BufferDesc(
                    checked((ulong)rasterViewCapacity * RenderRasterContext.TargetViewSizeInBytes),
                    BufferUsages.ShaderRead | BufferUsages.ShaderWrite,
                    "Virtual shadow raster target views"));

                uint resetWords = Math.Max(rasterHierarchyWordCapacity, rasterViewCapacity);
                uint resetGroups = DivideRoundUp(resetWords, 256u);
                _frameResetPass = AddComputePass(ref edit,
                    "Reset virtual shadow frame data",
                    _shadowMap.FrameResetPipeline,
                    new VirtualShadowDispatchPassData(new DispatchArguments(
                        Math.Min(resetGroups, 65535u), DivideRoundUp(resetGroups, 65535u), 1u)), region: _receiverPrepare);
                PassDefinition frameReset = edit.GetPass(_frameResetPass);
                _ = frameReset.Bind(edit.CreateBufferUav(_activePageCount), PipelineSync.ComputeShading,
                    GraphAccessMode.Write, WriteCoverage.Complete);
                frameReset.Write(_activePages, BufferRange.Whole, PipelineSync.ComputeShading, ResourceAccess.UnorderedAccess,
                    WriteCoverage.Partial, ResourceContentState.Defined, contentScope: BufferContentScope.CountBoundedRanges);
                _ = frameReset.Bind(edit.CreateBufferUav(
                    _uncachedPageRectBounds,
                    structureStride: 4u * sizeof(uint),
                    label: "Virtual shadow uncached page rect bounds"), PipelineSync.ComputeShading,
                    GraphAccessMode.Write, WriteCoverage.Complete);
                _ = frameReset.Bind(edit.CreateBufferUav(
                    _allocatedPageRectBounds, structureStride: 4u * sizeof(uint)),
                    PipelineSync.ComputeShading, GraphAccessMode.Write, WriteCoverage.Complete);
                _ = frameReset.Bind(edit.CreateBufferUav(
                    _uncachedPageHierarchy,
                    structureStride: sizeof(uint),
                    label: "Virtual shadow uncached page hierarchy"), PipelineSync.ComputeShading,
                    GraphAccessMode.Write, WriteCoverage.Complete);
                _ = frameReset.Bind(edit.CreateBufferUav(
                    _allocatedPageHierarchy,
                    structureStride: sizeof(uint),
                    label: "Virtual shadow allocated page hierarchy"), PipelineSync.ComputeShading,
                    GraphAccessMode.Write, WriteCoverage.Complete);
                foreach (GraphBufferId buffer in new[]
                         {
                             _rasterDispatchArguments,
                             _pageWorkDispatchArguments,
                             _pageRequestDispatchArguments,
                             _allocationFailures,
                             _policyRasterWorkCount,
                             _depthWorkCount,
                         })
                    _ = frameReset.Bind(edit.CreateBufferUav(buffer), PipelineSync.ComputeShading,
                        GraphAccessMode.Write, WriteCoverage.Complete);

                _receiverMaskResetPass = AddIndirectPass(ref edit,
                    "Reset virtual shadow receiver masks",
                    _shadowMap.ReceiverMaskResetPipeline,
                    _pageRequestDispatchArguments, region: feedback);
                var requestReset = new VirtualShadowBufferResetPassData(_requests._requestedPageCount);
                _requestResetPass = edit.AddCopyFramePass(
                    "Reset virtual shadow page request count",
                    PassQueueSelection.AnyOfType(QueueType.Graphics),
                    requestReset,
                    new PassOptions(Culling: PassCullingMode.NeverCull, RepeatCommands: true),
                    VirtualShadowBufferResetPassData.Declare,
                    VirtualShadowBufferResetPassData.Record, region: _receiverPrepare);
                var dynamicResolutionReset = new VirtualShadowBufferResetPassData(
                    _dynamicResolutionBias);
                _dynamicResolutionResetPass = edit.AddCopyFramePass(
                    "Reset virtual shadow dynamic resolution bias",
                    PassQueueSelection.AnyCompatible(),
                    dynamicResolutionReset,
                    new PassOptions(Culling: PassCullingMode.NeverCull, RepeatCommands: true),
                    VirtualShadowBufferResetPassData.Declare,
                    VirtualShadowBufferResetPassData.Record, region: _receiverPrepare);

                var resolutionCopy = new VirtualShadowResolutionCopyPassData(_dynamicResolutionBias, _frameResolutionBias);
                _resolutionCopyPass = edit.AddCopyFramePass("Copy virtual shadow resolution bias for the frame",
                    PassQueueSelection.AnyCompatible(), resolutionCopy,
                    new PassOptions(Culling: PassCullingMode.NeverCull, RepeatCommands: true),
                    VirtualShadowResolutionCopyPassData.Declare, VirtualShadowResolutionCopyPassData.Record,
                    region: _receiverPrepare);
                edit.OrderAfter(_resolutionCopyPass, _dynamicResolutionResetPass);

                var atlasInitialize = new VirtualShadowAtlasInitializePassData(_shadowMap.Atlas, 2);
                _atlasInitializePass = edit.AddCopyFramePass(
                    "Initialize virtual shadow atlas",
                    PassQueueSelection.AnyOfType(QueueType.Graphics),
                    atlasInitialize,
                    new PassOptions(Culling: PassCullingMode.NeverCull, RepeatCommands: true),
                    VirtualShadowAtlasInitializePassData.Declare,
                    VirtualShadowAtlasInitializePassData.Record, region: _receiverPrepare);
                var pageHiZStorageInitialize = new VirtualShadowPageHiZStorageInitializePassData(
                    _pageHiZ,
                    new TextureSubresourceRange(
                        0,
                        checked((uint)pageHiZMipCount),
                        0,
                        PageHiZLayerCount,
                        TextureAspects.Color));
                _pageHiZStorageInitializePass = edit.AddCopyFramePass(
                    "Initialize virtual shadow physical-page HiZ storage",
                    PassQueueSelection.AnyOfType(QueueType.Graphics),
                    pageHiZStorageInitialize,
                    new PassOptions(Culling: PassCullingMode.NeverCull, RepeatCommands: true),
                    VirtualShadowPageHiZStorageInitializePassData.Declare,
                    VirtualShadowPageHiZStorageInitializePassData.Record, region: _receiverPrepare);

                _initializePass = AddComputePass(
                    ref edit,
                    "Initialize virtual shadow state",
                    _shadowMap.StateInitializePipeline,
                    new VirtualShadowDispatchPassData(default), region: _receiverPrepare);
                GraphRegionId viewUpdates = edit.AddRegion("Virtual shadow view updates", _receiverPrepare);
                _coarseMarkPass = AddComputePass(
                    ref edit,
                    "Mark coarse virtual shadow pages",
                    _shadowMap.CoarsePageMarkPipeline,
                    new VirtualShadowDispatchPassData(default), region: primaryDepthAfter);
                GraphRegionId filterRequests = edit.AddRegion("Virtual shadow filter requests", primaryDepthAfter);
                _poolPreparePass = AddComputePass(
                    ref edit, "Prepare virtual shadow page order",
                    _shadowMap.PagePoolPipeline,
                    new VirtualShadowDispatchPassData(default), region: primaryDepthAfter);
                _poolPass = AddComputePass(
                    ref edit,
                    "Build virtual shadow page pool",
                    _shadowMap.PagePoolPipeline,
                    new VirtualShadowDispatchPassData(default), region: primaryDepthAfter);
                casterRegion = edit.AddFrameRegion("Virtual shadow caster page invalidation", primaryDepthAfter);
                _poolCompaction = VirtualShadowPageCompactionPasses.Add(ref edit,_renderPipelines,
                    _shadowMap.PagePoolPipeline.Shader,_shadowMap.PagePoolPipeline.Shader,
                    _shadowMap.PageLists,settings.MaxPages,_poolPass,primaryDepthAfter);
                _allocatePass = AddIndirectPass(
                    ref edit,
                    "Allocate virtual shadow pages",
                    _shadowMap.PageAllocatePipeline,
                    _pageRequestDispatchArguments, region: primaryDepthAfter);
                _poolFinishPass = AddComputePass(
                    ref edit, "Finish virtual shadow page order",
                    _shadowMap.PagePoolPipeline,
                    new VirtualShadowDispatchPassData(new DispatchArguments(1, 1, 1)), region: primaryDepthAfter);
                _clearPass = AddIndirectPass(
                    ref edit,
                    "Clear dirty virtual shadow pages",
                    _shadowMap.PageClearPipeline,
                    _pageWorkDispatchArguments, region: primaryDepthAfter);
                _compactViewsPass = AddComputePass(
                    ref edit,
                    "Compact virtual shadow raster views",
                    _shadowMap.ViewCompactPipeline,
                    new VirtualShadowDispatchPassData(default), region: primaryDepthAfter);
                _dynamicResolutionPass = AddComputePass(
                    ref edit,
                    "Update virtual shadow dynamic resolution",
                    _shadowMap.DynamicResolutionPipeline,
                    new VirtualShadowDispatchPassData(new DispatchArguments(1u, 1u, 1u)), region: feedback);
                _pageHiZInitializePass = AddIndirectPass(
                    ref edit,
                    "Initialize virtual shadow physical-page HiZ",
                    _shadowMap.PageHiZInitializePipeline,
                    _pageWorkDispatchArguments, region: _targetOcclusion);
                _pageHiZReducePasses = new GraphPassId[(pageHiZMipCount - 1) / 4];
                for (int mip = 4; mip < pageHiZMipCount; mip += 4)
                {
                    _pageHiZReducePasses[mip / 4 - 1] = AddIndirectPass(
                        ref edit,
                        $"Reduce virtual shadow physical-page HiZ mip {mip}",
                        _shadowMap.PageHiZReducePipeline,
                        _pageWorkDispatchArguments,
                        checked((ulong)mip * DispatchStride), region: _targetOcclusion);
                }
                _finalPageHiZInitializePass = AddIndirectPass(
                    ref edit,
                    "Finalize virtual shadow physical-page HiZ",
                    _shadowMap.PageHiZInitializePipeline,
                    _pageWorkDispatchArguments, region: targetGeometryAfter);
                _finalPageHiZReducePasses = new GraphPassId[(pageHiZMipCount - 1) / 4];
                for (int mip = 4; mip < pageHiZMipCount; mip += 4)
                {
                    _finalPageHiZReducePasses[mip / 4 - 1] = AddIndirectPass(
                        ref edit,
                        $"Finalize virtual shadow physical-page HiZ mip {mip}",
                        _shadowMap.PageHiZReducePipeline,
                        _pageWorkDispatchArguments,
                        checked((ulong)mip * DispatchStride), region: targetGeometryAfter);
                }
                _geometryWorkStatus = edit.CreateBufferInput(BufferUsages.ShaderRead);
                _rasterCompletePass = AddComputePass(ref edit, "Complete virtual shadow page depth",
                    _shadowMap.PageDepthCompletePipeline,
                    new VirtualShadowDispatchPassData(new DispatchArguments((settings.MaxPages + 63u) / 64u, 1u, 1u)),
                    region: targetGeometryAfter);
                BindComputeResources(ref edit, _rasterCompletePass, new()
                {
                    ["ActivePhysicalPages"] = GraphParameterResourceBinding.ReadOnlyBuffer(edit.CreateBufferSrv(_activePages, structureStride: 16),
                        PipelineSync.ComputeShading, BufferContentScope.CountBoundedPrefix),
                    ["CompletePageCount"] = GraphParameterResourceBinding.ReadOnlyBuffer(edit.CreateBufferSrv(_activePageCount, structureStride: 4), PipelineSync.ComputeShading),
                    ["CompletedShadowPageTable"] = GraphParameterResourceBinding.WritableBuffer(edit.CreateBufferUav(_shadowMap.PageTable, structureStride: 4), PipelineSync.ComputeShading),
                    ["GeometryWorkStatus"] = GraphParameterResourceBinding.ReadOnlyBuffer(edit.CreateBufferSrv(_geometryWorkStatus), PipelineSync.ComputeShading),
                    ["CompletedPageMetadata"] = GraphParameterResourceBinding.WritableBuffer(edit.CreateBufferUav(_shadowMap.PageMetadata,
                        structureStride: VirtualShadowPageMetadata.SizeInBytes), PipelineSync.ComputeShading),
                });
                _frameCompletePass = edit.AddGeneralFramePass(
                    "Finalize virtual shadow frame endpoints",
                    PassQueueSelection.AnyOfType(QueueType.Graphics),
                    0u,
                    new PassOptions(Culling: PassCullingMode.NeverCull, RepeatCommands: true),
                    static (ref PassDefinition _, ref uint _) => { },
                    static (ref GeneralPassCommandScope _, in uint _) => { }, region: feedback);

                edit.OrderAfter(_atlasInitializePass, _frameResetPass);
                edit.OrderAfter(_pageHiZStorageInitializePass, _frameResetPass);
                edit.OrderAfter(_dynamicResolutionResetPass, _frameResetPass);
                edit.OrderAfter(_initializePass, _atlasInitializePass);
                edit.OrderAfter(_requestResetPass, _initializePass);
                edit.OrderAfter(_receiverMaskResetPass, _initializePass);
                edit.OrderAfter(_coarseMarkPass, _requestResetPass);
                edit.OrderAfter(_coarseMarkPass, _dynamicResolutionResetPass);
                edit.OrderAfter(_poolPreparePass, _coarseMarkPass);
                edit.OrderAfter(_poolPass, _poolPreparePass);
                edit.OrderAfter(_allocatePass, _poolCompaction.CompletePass);
                edit.OrderAfter(_poolFinishPass, _allocatePass);
                edit.OrderAfter(_dynamicResolutionPass, _poolFinishPass);
                edit.OrderAfter(_compactViewsPass, _allocatePass);
                edit.OrderAfter(_clearPass, _compactViewsPass);
                edit.OrderAfter(_rasterCompletePass, _allocatePass);
                edit.OrderAfter(_rasterCompletePass, _finalPageHiZReducePasses.Length == 0
                    ? _finalPageHiZInitializePass : _finalPageHiZReducePasses[^1]);
                edit.OrderAfter(_receiverMaskResetPass, _rasterCompletePass);
                edit.OrderAfter(_dynamicResolutionPass, _rasterCompletePass);
                edit.OrderAfter(_dynamicResolutionPass, _resolutionCopyPass);
                edit.OrderAfter(_frameCompletePass, _dynamicResolutionPass);
                edit.OrderAfter(_frameCompletePass, _receiverMaskResetPass);
                for (int mip = 0; mip < _pageHiZReducePasses.Length; mip++)
                {
                    edit.OrderAfter(
                        _pageHiZReducePasses[mip],
                        mip == 0 ? _pageHiZInitializePass : _pageHiZReducePasses[mip - 1]);
                }
                edit.OrderAfter(_pageHiZInitializePass, _pageHiZStorageInitializePass);
                for (int mip = 0; mip < _finalPageHiZReducePasses.Length; mip++)
                {
                    edit.OrderAfter(
                        _finalPageHiZReducePasses[mip],
                        mip == 0
                            ? _finalPageHiZInitializePass
                            : _finalPageHiZReducePasses[mip - 1]);
                }

                ConfigureRasterContext();
                _clusterSourceViews = edit.CreateBufferInput(BufferUsages.ShaderRead);
                _shadowViewBuffer = edit.CreatePersistentBuffer(new BufferDesc(
                    checked((ulong)sourceViewCapacity * VirtualShadowView.SizeInBytes),
                    BufferUsages.ShaderRead | BufferUsages.ShaderWrite | BufferUsages.CopyDestination | BufferUsages.CopySource,
                    "Virtual shadow view parameters"));
                edit.AddBufferUploadPass(_shadowViewBuffer);
                _frameShadowViews = edit.CreateBufferInput(BufferUsages.ShaderRead | BufferUsages.ShaderWrite);
                _frameShadowRanges = edit.CreateBufferInput(BufferUsages.ShaderRead);
                _frameLightViewIndices = edit.CreateBufferInput(BufferUsages.ShaderRead);
                _frameChangedInstances = edit.CreateBufferInput(BufferUsages.ShaderRead);
                _casters._frameDepthCacheClasses = edit.CreateBufferInput(BufferUsages.ShaderRead);
                _casters._framePreviousDepthCacheClasses = edit.CreateBufferInput(BufferUsages.ShaderRead);
                _casters._framePreviousCasterRows = edit.CreateBufferInput(BufferUsages.ShaderRead);
                _casters._frameLodBounds = edit.CreateBufferInput(BufferUsages.ShaderRead);
                _frameSamplingUniforms = edit.CreateBufferInput(BufferUsages.Constant);
                _frameDispatchUniforms = edit.CreateBufferInput(BufferUsages.Constant);
                _initUniforms = edit.CreateBufferInput(BufferUsages.Constant);
                _invalidationUniforms = edit.CreateBufferInput(BufferUsages.Constant);
                _coarseDemandUniforms = edit.CreateBufferInput(BufferUsages.Constant);
                _demandUniforms = edit.CreateBufferInput(BufferUsages.Constant);
                _poolUniforms = edit.CreateBufferInput(BufferUsages.Constant);
                _poolPrepareUniforms = edit.CreateBufferInput(BufferUsages.Constant);
                _poolFinishUniforms = edit.CreateBufferInput(BufferUsages.Constant);
                _allocationUniforms = edit.CreateBufferInput(BufferUsages.Constant);
                _clearUniforms = edit.CreateBufferInput(BufferUsages.Constant);
                _dynamicResolutionUniforms = edit.CreateBufferInput(BufferUsages.Constant);
                _pageHiZInitializeUniforms = edit.CreateBufferInput(BufferUsages.Constant);
                _casterBounds = edit.CreateBufferInput(BufferUsages.ShaderRead);
                _pageHiZReduceUniforms = new GraphBufferInput[_pageHiZReducePasses.Length];
                _requests.BindReceiverMaskReset(ref edit);
                BindInitialize(ref edit, _initUniforms);
                casterBindings = CreateCasterBindings(ref edit, _invalidationUniforms);
                BindCoarseMark(ref edit, _coarseDemandUniforms);
                BindPool(ref edit, _poolPass, _poolUniforms);
                BindPool(ref edit, _poolPreparePass, _poolPrepareUniforms);
                BindPool(ref edit, _poolFinishPass, _poolFinishUniforms);
                BindAllocation(ref edit, _allocatePass, _allocationUniforms);
                _pageListAccesses = DeclarePageListAccesses(ref edit, settings.MaxPages);
                InstallStaticDepth(ref edit, shaders, primaryDepthAfter, targetGeometryAfter);
                InstallResidentPages(ref edit, shaders, pageTableEntryCapacity, targetGeometryAfter);
                _prefilter.InstallPrefilter(ref edit, shaders, pageTableEntryCapacity, filterRequests, targetGeometryAfter);
                edit.OrderAfter(_frameCompletePass, _rasterCompletePass);
                edit.OrderAfter(_frameCompletePass, _prefilter._prefilterInitialize);
                BindClear(ref edit, _clearUniforms);
                BindDynamicResolution(ref edit, _dynamicResolutionUniforms);
                BindCompact(ref edit, _frameDispatchUniforms, _clusterSourceViews);
                BindPageHiZInitialize(ref edit, _pageHiZInitializePass, _pageHiZInitializeUniforms, settings);
                BindPageHiZInitialize(ref edit, _finalPageHiZInitializePass, _pageHiZInitializeUniforms, settings);
                for (int index = 0; index < _pageHiZReducePasses.Length; index++)
                {
                    uint mip = checked((uint)(index + 1) * 4u);
                    GraphBufferInput input = edit.CreateBufferInput(BufferUsages.Constant);
                    _pageHiZReduceUniforms[index] = input;
                    BindPageHiZReduce(ref edit, _pageHiZReducePasses[index], input, mip - 1, mip, settings);
                    BindPageHiZReduce(ref edit, _finalPageHiZReducePasses[index], input, mip - 1, mip, settings);
                }
                _quality.InstallWorkFeedback(ref edit, shaders, rasterViewCapacity / settings.MipLevelCount,
                    viewUpdates, feedback);
                _quality.InstallWorkTime(ref edit, primaryDepthAfter, targetGeometryAfter, feedback);
                if (_prefilter._prefilterPipeline is not null && _quality._workTimeQueries is not null)
                    edit.OrderAfter(_prefilter._prefilterPasses.First(), _quality._workTimeEndPass);
                _quality.InstallDistantBias(ref edit, feedback);
                _uniformData = new FeatureUniformBuffer(ref edit,
                    [_frameSamplingUniforms, _frameDispatchUniforms, _initUniforms, _invalidationUniforms,
                     _coarseDemandUniforms, _demandUniforms, _poolUniforms,
                     _poolPrepareUniforms, _poolFinishUniforms, _allocationUniforms,
                     _clearUniforms, _dynamicResolutionUniforms, _pageHiZInitializeUniforms, _quality._workUniforms, _residentUniforms, .. _prefilter.UniformInputs, .. _pageHiZReduceUniforms],
                    device.Capabilities.Limits.ConstantBufferAlignment, "Virtual shadow constant data");
                PassDefinition frameComplete = edit.GetPass(_frameCompletePass);
                _ = frameComplete.Read(
                    _shadowMap.PageTable,
                    BufferRange.Whole,
                    PipelineSync.AllShading,
                    ResourceAccess.ShaderResource);
                _ = frameComplete.Read(
                    _shadowMap.Atlas,
                    new TextureSubresourceRange(0, 1, 0, 1, TextureAspects.Color),
                    PipelineSync.AllShading,
                    ResourceAccess.ShaderResource,
                    TextureLayout.ShaderResource);
                edit.Commit();
                _casterExpansionRegion = casterRegion;
                _casterBindings = casterBindings;
                _casterShader = shaders.InvalidateCastersShader!;
            }
            finally
            {
                edit.Dispose();
            }
        }
        catch
        {
            dispatchLayout?.Dispose();
            _residentPipeline?.Dispose();
            _prefilter._prefilterPipeline?.Dispose();
            _prefilter._prefilterRequestPipeline?.Dispose();
            _prefilter._prefilterRequestCapturePipeline?.Dispose();
            _prefilter._prefilterResetPipeline?.Dispose();
            _prefilter._prefilterSelectPipeline?.Dispose();
            _staticMergePipeline?.Dispose();
            _quality._workFeedbackPipeline?.Dispose();
            _quality._workTimeQueries?.Dispose();
            shadowMap?.Dispose();
            throw;
        }
    }

    public VirtualShadowPages ShadowMap => _shadowMap;

    public int ShadowViewCount => _submittedViewCount;
    public GraphPassId OpaqueRequestPass => _opaqueRequests.CompletePass;



    internal void BindRasterDispatchViews(
        ref RenderGraphEdit graph,
        ref PassDefinition pass, PipelineSync sync)
    {
        _ = pass.Bind(
            graph.CreateBufferSrv(
                _rasterDispatchViews,
                structureStride: (uint)RenderRasterContext.ViewSizeInBytes,
                label: "Virtual shadow raster invocation views"),
            sync,
            BufferContentScope.CountBoundedPrefix);
    }

    internal void BindClusterSourceViews(
        ref RenderGraphFrame graph,
        GraphBufferSlice sourceViews)
    {
        graph.SetBufferInput(_clusterSourceViews, sourceViews);
    }

    internal void BindRasterTargetViews(
        ref RenderGraphEdit graph,
        ref PassDefinition pass, PipelineSync sync)
    {
        _ = pass.Bind(
            graph.CreateBufferSrv(
                _rasterTargetViews,
                structureStride: RenderRasterContext.TargetViewSizeInBytes,
                label: "Virtual shadow raster target views"),
            sync,
            BufferContentScope.CountBoundedPrefix);
    }

    internal void BindRasterPageHierarchy(
        ref RenderGraphEdit graph,
        ref PassDefinition pass, PipelineSync sync) =>
        _ = pass.Bind(
            graph.CreateBufferSrv(
                _uncachedPageHierarchy,
                structureStride: 2u * sizeof(uint),
                label: "Virtual shadow uncached page hierarchy"),
            sync);

    internal void BindRasterPageTable(
        ref RenderGraphEdit graph,
        ref PassDefinition pass, PipelineSync sync) =>
        _ = pass.Bind(
            graph.CreateBufferSrv(
                _shadowMap.PageTable,
                structureStride: VirtualShadowView.PageTableEntrySizeInBytes,
                label: "Virtual shadow raster page table"),
            sync);

    internal void BindRasterReceiverMasks(
        ref RenderGraphEdit graph,
        ref PassDefinition pass, PipelineSync sync) =>
        _ = pass.Bind(
            graph.CreateTextureSrv(_requests._pageReceiverMasks, format: Format.R32UInt,
                dimension: SomeEngine.Graphics.TextureViewDimension.Texture2D,
                label: "Virtual shadow raster receiver masks"),
            sync);

    internal void BindRasterHiZ(ref RenderGraphEdit graph, ref PassDefinition pass, PipelineSync sync) =>
        _ = pass.Bind(graph.CreateTextureSrv(_pageHiZ, format: Format.R32Float,
            dimension: SomeEngine.Graphics.TextureViewDimension.Texture2DArray,
            label: "Virtual shadow raster-target HiZ"), sync);

    internal void BindRasterPageMetadata(ref RenderGraphEdit graph, ref PassDefinition pass, PipelineSync sync) =>
        _ = pass.Bind(graph.CreateBufferSrv(_shadowMap.PageMetadata, structureStride: VirtualShadowPageMetadata.SizeInBytes), sync);

    internal void BindRasterPageBounds(ref RenderGraphEdit graph, ref PassDefinition pass, PipelineSync sync)
    {
        BindRasterTargetViews(ref graph, ref pass, sync);
        BindRasterPageHierarchy(ref graph, ref pass, sync);
        BindRasterPageTable(ref graph, ref pass, sync);
        BindRasterReceiverMasks(ref graph, ref pass, sync);
        _casters.BindRasterDepthCacheClasses(ref graph, ref pass, sync);
    }

    internal void BindPageDepthAtlas(
        ref RenderGraphEdit graph,
        ref PassDefinition pass,
        PipelineSync sync)
    {
        _ = pass.Bind(
            graph.CreateTextureUav(
                _shadowMap.Atlas,
                format: Format.R32UInt,
                dimension: SomeEngine.Graphics.TextureViewDimension.Texture2DArray,
                label: "Virtual shadow raster depth atlas"),
            sync,
            GraphAccessMode.ReadWrite,
            WriteCoverage.Partial,
            unorderedAccessHazards: UnorderedAccessHazardMode.AtomicMergeable);
    }

    public void PrepareViews(RenderGraphNextFrame frame, RenderLightSet lights, in RenderView receiverView)
    {
        VirtualShadowFrame data = frame.GetFrameData(this, CreateFrameData);
        ReadOnlySpan<VirtualShadowView> views = data.Views.PrepareViews(lights, lights.UploadOrder, receiverView,
            _shadowMap.RequiresInitialization, _geometry.HasFirstPersonWorldMeshes(), _requests.ReceiverMaskColumns);
        data._viewCount = views.Length;
        data._previousViewCount = data.Views.PreviousViewCount;
        data.LightKeys = data.Views.Keys;
        data.Ranges = _viewRanges.AsMemory(0, views.Length);
        for (int index = 0; index < views.Length; index++)
            _rangeEntryCounts[index] = views[index].LightKind == VirtualShadowLightKind.Directional
                ? _shadowMap.Settings.VirtualPagesPerView : _shadowMap.Settings.PageTableEntriesPerView;
        uint entries = _shadowMap.Ranges.WriteRanges(data.LightKeys.Span,
            _rangeEntryCounts.AsSpan(0, views.Length), data.Ranges.Span);
        if (EnsurePageTableCapacity(entries)) data.Views.InvalidateResourceHistory();
        data.Views.SetPageTableRanges(data.Ranges.Span, entries);
        data.RangeCount = _shadowMap.Ranges.RangeCount;
        foreach (PageTableRange range in data.Ranges.Span) data.RangeCount = Math.Max(data.RangeCount, range.RangeId + 1u);
        int rangeCount = checked((int)data.RangeCount);
        if (_rangeViews.Length < rangeCount) Array.Resize(ref _rangeViews, rangeCount);
        data._shadowViews = _shadowViews.AsMemory(0, views.Length);
        data._previousShadowViews = _previousShadowViews.AsMemory(0, data._previousViewCount);
        data._previousShadowViewProjections = _previousShadowViewProjections.AsMemory(0, views.Length);
        bool history = !_shadowMap.AcquireFrameResources().RequiresInitialization &&
            data.Views.TryCopyHistoryViews(data._previousShadowViews.Span, data._previousShadowViewProjections.Span);
        data._frameHasPreviousHistory = history;
        int count = data.Views.CopyRenderViews(data._shadowViews.Span);
        data.Views.UpdateCurrentViews(data._shadowViews.Span[..count], data._previousShadowViewProjections.Span[..count]);
    }

    public void StageFrame(
        ref RenderGraphFrame graph,
        RenderLightSet lights,
        in RenderView receiverView,
        Entity receiver = default)
    {
        VirtualShadowFrame data = GetFrame(ref graph);
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(lights);
        if (_quality._qualityReadbacks is not null)
        {
            foreach (GraphReadbackId readback in _quality._qualityReadbacks.Readbacks) graph.SetReadbackEnabled(readback, false);
            graph.SetPassEnabled(_quality._qualityReceiverCopyPass, false);
            graph.SetPassEnabled(_quality._qualityReadbacks.ActivePagesCopyPass, false);
        }
        int instanceCount = _geometry.CurrentInstanceCount;
        data._frameInstances = _geometry.CurrentInstances;
        data._frameInstanceRevision = data._frameInstances!.ContentRevision;
        data._changedInstances.Clear();
        data._streamingChangedInstances.Clear();
        data._rasterChangedInstances.Clear();
        data._frameInstances.CollectChangedInstances(_historyInstanceRevision, data._changedInstances);
        _geometry.CollectChangedInstances(_historyCasterRevision,
            data._rasterChangedInstances, data._streamingChangedInstances, out data._frameCasterRevision);
        data._changedInstances.AddRange(data._rasterChangedInstances);
        data._rasterChangedInstances.AddRange(_deformedInstances);
        data._rasterChangedInstances.Sort();
        // Batch changes already arrive in increasing row order. Raster and
        // deformation notices can add unordered or duplicate rows.
        if (data._rasterChangedInstances.Count != 0)
        {
            data._changedInstances.AddRange(_deformedInstances);
            data._changedInstances.Sort();
            int unique = 0;
            for (int index = 0; index < data._changedInstances.Count; index++)
                if (unique == 0 || data._changedInstances[index] != data._changedInstances[unique - 1])
                    data._changedInstances[unique++] = data._changedInstances[index];
            data._changedInstances.RemoveRange(unique, data._changedInstances.Count - unique);
        }
        _casters.UpdateCasterCacheClasses(ref graph, instanceCount);
        int immediateCount = data._changedInstances.Count;
        data._streamingChangedInstances.Sort();
        int streamingCount = 0;
        for (int index = 0; index < data._streamingChangedInstances.Count; index++)
        {
            uint row = data._streamingChangedInstances[index];
            if (data._changedInstances.BinarySearch(0, immediateCount, row, null) >= 0 ||
                streamingCount != 0 && row == data._streamingChangedInstances[streamingCount - 1]) continue;
            data._streamingChangedInstances[streamingCount++] = row;
        }
        data._streamingChangedInstances.RemoveRange(streamingCount, data._streamingChangedInstances.Count - streamingCount);
        if (data._rasterChangedInstances.Count != 0)
            for (int index = 0; index < immediateCount; index++)
                if (data._rasterChangedInstances.BinarySearch(data._changedInstances[index]) >= 0)
                    data._changedInstances[index] |= 0x40000000u;
        foreach (uint row in data._removedCasterRows) data._changedInstances.Add(0x20000000u | row);
        data._changedInstances.AddRange(data._streamingChangedInstances);
        for (uint index = 0; index < data._lodCasterBounds.Count / 2; index++)
            data._changedInstances.Add(0x80000000u | index);
        {
            graph.SetParameterInput(_blueNoiseInput,
                ResourceBinding.SampledTexture(_textureDevice.PrepareTextureSrv(ref graph, _blueNoiseInput, _blueNoise)));
            ReadOnlySpan<VirtualShadowView> views = data.Views.Views;
            data._frameBaseShadowBias = _baseShadowBias;
            for (int index = 0; index < views.Length; index++) data._frameBaseShadowBias[index] = views[index].ResolutionLodBias;
            VirtualShadowMapFrameResources resources = _shadowMap.AcquireFrameResources();

            data._frameIndex = data._frameIndex == uint.MaxValue ? 1u : data._frameIndex + 1u;

            bool hasPreviousTargetHistory =
                !resources.RequiresInitialization &&
                !data.Views.RequiresCacheReset &&
                data.Views.TryCopyHistoryViews(
                    data._previousShadowViews.Span,
                    data._previousShadowViewProjections.Span);
            data._frameHasPreviousHistory = hasPreviousTargetHistory;

            RenderGeometryResources geometry = _geometry.Geometry;
            graph.SetBufferInput(_geometryWorkStatus, new GraphBufferSlice(geometry.WorkStatus, BufferRange.Whole));
            graph.SetBufferInput(_casterBounds, new GraphBufferSlice(geometry.InstanceWorldBounds, BufferRange.Whole));
            VirtualShadowView emptyView = default;
            ReadOnlySpan<VirtualShadowView> viewData = views.IsEmpty
                ? MemoryMarshal.CreateReadOnlySpan(ref emptyView, 1) : views;
            graph.WriteBuffer(_shadowViewBuffer, 0, MemoryMarshal.AsBytes(viewData));
            graph.SetBufferInput(_frameShadowViews, new GraphBufferSlice(_shadowViewBuffer,
                new BufferRange(0, checked((ulong)viewData.Length * VirtualShadowView.SizeInBytes))));
            Span<VirtualShadowRange> rangeViews = _rangeViews.AsSpan(0, checked((int)data.RangeCount));
            _shadowMap.Ranges.WriteViewRanges(data.Ranges.Span, views, rangeViews);
            graph.SetBufferInput(_frameShadowRanges, graph.Upload<VirtualShadowRange>(rangeViews,
                BufferUsages.ShaderRead, "Virtual shadow range views"));
            graph.SetBufferInput(_frameChangedInstances, graph.Upload<uint>(
                data._changedInstances.Count != 0 ? CollectionsMarshal.AsSpan(data._changedInstances) : [0u],
                BufferUsages.ShaderRead,
                "Virtual shadow changed caster indices"));
            VirtualShadowReceiverProjection receiverProjection = CalculateReceiverProjection(receiverView);
            var sampling = new VirtualShadowSamplingUniforms
            {
                ViewCount = checked((uint)data._viewCount),
                DirectionalLightCount = data.Views.DirectionalLightCount,
                DirectionalClipmapLevels = data.Views.DirectionalClipmapLevelCount,
                PointLightCount = data.Views.PointLightCount,
                PointLightBufferOffset = checked((uint)lights.Directional.Count),
                PointViewOffset = data.Views.PointViewOffset,
                SpotLightCount = data.Views.SpotLightCount,
                SpotLightBufferOffset = checked((uint)(
                    lights.Directional.Count + lights.Points.Count)),
                SpotViewOffset = data.Views.SpotViewOffset,
                BlockerSampleCount = _shadowMap.Settings.BlockerSampleCount,
                FilterSampleCount = _shadowMap.Settings.FilterSampleCount,
                BlockerSearchRadiusTexels = _shadowMap.Settings.BlockerSearchRadiusTexels,
                MaxFilterRadiusTexels = _shadowMap.Settings.MaxFilterRadiusTexels,
                CullBackfacingPixels = _shadowMap.Settings.CullBackfacingPixels ? 1u : 0u,
                ScreenRayLength = _shadowMap.Settings.ScreenRayLength,
                NormalBias = _shadowMap.Settings.NormalBias,
                FrameIndex = data._frameIndex,
                DirectionalRays = new()
                {
                    RayCount = _shadowMap.Settings.DirectionalRayCount,
                    SamplesPerRay = _shadowMap.Settings.DirectionalSamplesPerRay,
                    AdaptiveRayCount = _shadowMap.Settings.AdaptiveRayCount,
                    ExtrapolateMaxSlope = _shadowMap.Settings.DirectionalExtrapolateMaxSlope,
                    TexelDitherScale = _shadowMap.Settings.DirectionalTexelDitherScale,
                    RayLengthScale = _shadowMap.Settings.DirectionalRayLengthScale,
                },
                LocalRays = new()
                {
                    RayCount = _shadowMap.Settings.LocalRayCount,
                    SamplesPerRay = _shadowMap.Settings.LocalSamplesPerRay,
                    AdaptiveRayCount = _shadowMap.Settings.AdaptiveRayCount,
                    ExtrapolateMaxSlope = _shadowMap.Settings.LocalExtrapolateMaxSlope,
                    TexelDitherScale = _shadowMap.Settings.LocalTexelDitherScale,
                    MaxRayAngle = _shadowMap.Settings.LocalMaxRayAngle,
                    MaxSlopeBias = _shadowMap.Settings.LocalMaxSlopeBias,
                },
                ReceiverProjection = receiverProjection,
                FirstPersonViewOffset = data.Views.FirstPersonViewOffset,
            };
            _uniformData.Write(ref graph, _frameSamplingUniforms, in sampling);
            UpdatePassData(
                ref graph,
                lights,
                in receiverView,
                in resources,
                instanceCount,
                checked((uint)data._viewCount),
                in receiverProjection);
            _opaqueRequests.Record(ref graph, lights, data.Views);
            _quality.StageWorkFeedback(ref graph, lights, views);
            _prefilter.StagePrefilter(ref graph, views);
            _quality.StageDistantBias(ref graph);
            _uniformData.Upload(ref graph);
            graph.ObserveExecution(new VirtualShadowSubmission(this, data));
        }
    }

    private VirtualShadowFrame CreateFrameData() => new(_viewEntities.CreateFrame())
    {
        _frameIndex = _submittedFrameIndex,
    };

    internal VirtualShadowFrame GetFrame(ref RenderGraphFrame graph) => graph.GetFrameData(this, CreateFrameData);

    internal void Submit(VirtualShadowFrame data, in RenderGraphSubmission submission)
    {
        _shadowMap.Submit();
        _shadowMap.Ranges.Apply(data.LightKeys.Span, data.Ranges.Span);
        data.Views.Submit();
        _viewEntities = data.Views;
        _quality.CommitWorkTime(data, submission.FrameIdentity);
        _quality.CommitWorkFeedback(data);
        _prefilter.CommitPrefilter();
        _quality.SubmitCapture(data);
        _casters.CommitCasterCacheClasses(data);
        _historyInstances = data._frameInstances;
        _historyInstanceRevision = data._frameInstanceRevision;
        _historyCasterRevision = data._frameCasterRevision;
        _submittedFrameIndex = data._frameIndex;
        _submittedViewCount = data._viewCount;
        _deformedInstances.Clear();
        _invalidateAllCasters = false;
    }

    private sealed class VirtualShadowSubmission(VirtualShadowFeature feature, VirtualShadowFrame data)
        : IRenderGraphFrameExecutionObserver
    {
        public void OnFrameSubmitted(in RenderGraphSubmission submission) => feature.Submit(data, in submission);
    }

    public void EnableQualityCapture() => _quality.EnableQualityCapture();

    public void CaptureQuality(ref RenderGraphFrame graph, Action<VirtualShadowFrameQuality> receive) =>
        _quality.CaptureQuality(ref graph, receive);

    public event Action<ulong, float>? ResolutionBiasCompleted
    {
        add => _quality.ResolutionBiasCompleted += value;
        remove => _quality.ResolutionBiasCompleted -= value;
    }

    public bool TryResolve(
        string name,
        ResourceBindingType type,
        out GraphResourceBinder binder)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        binder = name switch
        {
            "ShadowRanges" when type == ResourceBindingType.BufferSrv => BindRangeViews,
            "RasterTargetViews" when type == ResourceBindingType.BufferSrv => BindRasterTargetViews,
            "RasterTargetDepthCacheClasses" when type == ResourceBindingType.BufferSrv => _casters.BindRasterDepthCacheClasses,
            "RasterTargetPageHierarchy" when type == ResourceBindingType.BufferSrv => BindRasterPageHierarchy,
            "RasterTargetPageTable" when type == ResourceBindingType.BufferSrv => BindRasterPageTable,
            "RasterTargetReceiverMasks" when type == ResourceBindingType.TextureSrv => BindRasterReceiverMasks,
            "RasterTargetPageMetadata" when type == ResourceBindingType.BufferSrv => BindRasterPageMetadata,
            "RasterTargetCurrentHiZ" or "RasterTargetPreviousHiZ" when type == ResourceBindingType.TextureSrv => BindRasterHiZ,
            "RasterTargetOutput" when type == ResourceBindingType.TextureUav => BindPageDepthAtlas,
            "PageDemand" when type == ResourceBindingType.ConstantBuffer => _requests.BindReceiverPageDemand,
            "ShadowViews" when type == ResourceBindingType.BufferSrv => _requests.BindReceiverShadowViews,
            "VirtualShadowLightViewIndices" when type == ResourceBindingType.BufferSrv => BindLightViewIndices,
            "DynamicResolutionBias" when type == ResourceBindingType.BufferSrv => BindFrameResolutionBias,
            "RequestedPageFlags" when type == ResourceBindingType.BufferUav => _requests.BindRequestedPageFlags,
            "RequestedPageIndices" when type == ResourceBindingType.BufferUav => _requests.BindRequestedPageIndices,
            "RequestedPageCount" when type == ResourceBindingType.BufferUav => _requests.BindRequestedPageCount,
            "PageReceiverMasks" when type == ResourceBindingType.TextureUav => _requests.BindPageReceiverMasks,
            "VirtualShadowViews" when type == ResourceBindingType.BufferSrv => BindShadeViews,
            "VirtualShadowResidentPages" when type == ResourceBindingType.BufferSrv => BindResidentPages,
            "VirtualShadowPrefilteredAtlas" when type == ResourceBindingType.TextureSrv => _prefilter.BindPrefilteredAtlas,
            "VirtualShadowPageTable" when type == ResourceBindingType.BufferSrv => BindShadePageTable,
            "VirtualShadowReceiverMasks" when type == ResourceBindingType.TextureSrv => BindRasterReceiverMasks,
            "VirtualShadowAtlas" when type == ResourceBindingType.TextureSrv => BindShadeAtlas,
            "VirtualShadowBlueNoise" when type == ResourceBindingType.TextureSrv => BindShadeBlueNoise,
            "VirtualShadowSampling" when type == ResourceBindingType.ConstantBuffer => BindShadeSampling,
            "VirtualShadowDynamicResolutionBias" when type == ResourceBindingType.BufferSrv => BindFrameResolutionBias,
            _ => null!,
        };
        return binder is not null;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _quality._qualityReadbacks?.Requests.Clear();
        _dispatchLayout.Dispose();
        _residentPipeline?.Dispose();
        _prefilter._prefilterPipeline?.Dispose();
        _prefilter._prefilterRequestPipeline?.Dispose();
        _prefilter._prefilterRequestCapturePipeline?.Dispose();
        _prefilter._prefilterResetPipeline?.Dispose();
        _prefilter._prefilterSelectPipeline?.Dispose();
        _staticMergePipeline?.Dispose();
        _quality._workFeedbackPipeline?.Dispose();
        if (_quality._workTimeQueries is { } queries) _renderGraph.RetireAfterSubmittedFrames(queries);
        _shadowMap.Dispose();

        _viewEntities.Dispose();
        _disposed = true;
    }

    internal static VirtualShadowReceiverProjection CalculateReceiverProjection(in RenderView view)
    {
        Matrix4x4.Invert(view.View, out Matrix4x4 viewToWorld);
        Vector3 cameraPosition = new(viewToWorld.M41, viewToWorld.M42, viewToWorld.M43);
        Matrix4x4 nearView = Matrix4x4.CreateTranslation(cameraPosition) * view.View;
        Matrix4x4 worldToClip = nearView * view.Projection;
        return new VirtualShadowReceiverProjection
        {
            WorldToClipW = new(worldToClip.M14, worldToClip.M24, worldToClip.M34, worldToClip.M44),
            WorldToViewDepth = new(-nearView.M13, -nearView.M23, -nearView.M33, -nearView.M43),
            WorldTranslation = -cameraPosition,
            DepthOfField = view.DepthOfField.GetValueOrDefault(),
            ScreenPixelRadius = MathF.Min(1f / (view.ViewportWidth * view.Projection.M11),
                1f / (view.ViewportHeight * view.Projection.M22)),
            CameraPosition = cameraPosition,
            CotanHalfFieldOfView = view.Projection.M11,
        };
    }

    internal void UpdatePassData(
        ref RenderGraphFrame graph,
        RenderLightSet lights,
        in RenderView receiverView,
        in VirtualShadowMapFrameResources resources,
        int instanceCount,
        uint viewCount,
        in VirtualShadowReceiverProjection receiverProjection)
    {
        VirtualShadowFrame data = GetFrame(ref graph);
        graph.SetRegionEnabled(_requests.ShadowPageRequests, viewCount != 0u);
        uint maxPages = _shadowMap.Settings.MaxPages;
        uint tableEntries = data.Views.PageTableEntryCount;
        uint pageGroups = DivideRoundUp(maxPages, 64u);
        uint initGroups = DivideRoundUp(
            Math.Max(Math.Max(maxPages, tableEntries), Math.Max(_requests._requestFlagEntryCapacity, _requests.ReceiverMaskWidth * _requests.ReceiverMaskHeight)),
            64u);
        bool resetState = resources.RequiresInitialization;

        var initUniforms = new VirtualShadowStateInitUniforms
        {
            PageTableEntryCount = Math.Max(1u, _shadowMap.PageTableEntryCapacity),
            MaxPages = maxPages,
            RequestFlagEntryCount = _requests._requestFlagEntryCapacity,
        };
        _uniformData.Write(ref graph, _initUniforms, in initUniforms);
        graph.SetPassDataIfChanged(
            _initializePass,
            new VirtualShadowDispatchPassData(new DispatchArguments(Math.Min(initGroups, 65535u), DivideRoundUp(initGroups, 65535u), 1)));
        graph.SetPassEnabled(_initializePass, resetState);
        graph.SetPassEnabled(_atlasInitializePass, resetState);
        graph.SetPassEnabled(_staticMergeIntermediatePass, viewCount != 0u);
        graph.SetPassEnabled(_staticMergeFinalPass, viewCount != 0u);
        graph.SetPassEnabled(_pageHiZStorageInitializePass, resetState);

        var invalidationUniforms = new VirtualShadowInvalidationUniforms
        {
            ChangedInstanceCount = checked((uint)data._changedInstances.Count),
            DeferredChangedInstanceCount = checked((uint)(data._streamingChangedInstances.Count + data._lodCasterBounds.Count / 2)),
            ViewCount = viewCount,
            PageTableEntryCount = tableEntries,
            PageDilation = checked((uint)Math.Ceiling(
                Math.Max(
                    _shadowMap.Settings.BlockerSearchRadiusTexels,
                    Math.Max(_shadowMap.Settings.MaxFilterRadiusTexels, _shadowMap.Settings.PrefilteredDistantEnabled ? 2f : 0f)) /
                _shadowMap.Settings.PageSize)),
            FrameIndex = data._frameIndex,
            HierarchyWordsPerView = _shadowMap.Settings.PageHierarchyWordsPerView,
            MipLevelCount = _shadowMap.Settings.MipLevelCount,
            PreviousWorldTranslation = _geometry.PreviousBoundsOrigin.RelativeTo(receiverView.WorldOrigin),
        };
        _uniformData.Write(ref graph, _invalidationUniforms, in invalidationUniforms);
        if (viewCount != 0u && data._changedInstances.Count != 0)
        {
            data.CasterExpansion = VirtualShadowCasterExpansionPasses.Add(ref graph, _renderPipelines,
                _casterShader, _dispatchLayout, (uint)data._changedInstances.Count, viewCount,
                _shadowMap.Settings.MipLevelCount, (name, _) => _casterBindings.TryGetValue(name, out var binding)
                    ? binding : null, _casterExpansionRegion);
            graph.OrderAfter(data.CasterExpansion.FirstPass, _poolPass);
            graph.OrderAfter(_allocatePass, data.CasterExpansion.CompletePass);
        }

        var coarseDemand = new VirtualShadowCoarsePageDemandUniforms
        {
            ViewCount = viewCount,
            PageTableEntryCount = tableEntries,
            FrameIndex = data._frameIndex,
        };
        _uniformData.Write(ref graph, _coarseDemandUniforms, in coarseDemand);
        graph.SetPassDataIfChanged(
            _coarseMarkPass,
            new VirtualShadowDispatchPassData(new DispatchArguments(
                DivideRoundUp(viewCount, 64u),
                1,
                1)));
        graph.SetPassEnabled(_coarseMarkPass, viewCount != 0u);
        graph.SetPassEnabled(_receiverMaskResetPass, viewCount != 0u);
        graph.SetPassEnabled(_requestResetPass, true);
        graph.SetPassEnabled(_dynamicResolutionResetPass, resetState || viewCount == 0u);

        var dynamicResolution = new VirtualShadowDynamicResolutionUniforms
        {
            MaxPages = _shadowMap.Settings.MaxPages,
            PagePoolLoadStart = _shadowMap.Settings.DynamicResolutionPagePoolLoadStart,
            MaxLodBias = _shadowMap.Settings.DynamicResolutionMaxLodBias,
            Enabled = _shadowMap.Settings.DynamicResolutionEnabled && viewCount != 0u ? 1u : 0u,
        };
        _uniformData.Write(ref graph, _dynamicResolutionUniforms, in dynamicResolution);
        graph.SetPassEnabled(_dynamicResolutionPass, viewCount != 0u);

        var demand = new VirtualShadowPageDemandUniforms
        {
            ViewCount = viewCount,
            PageTableEntryCount = tableEntries,
            PointLightCount = data.Views.PointLightCount,
            PointLightBufferOffset = checked((uint)lights.Directional.Count),
            PointViewOffset = data.Views.PointViewOffset,
            SpotLightCount = data.Views.SpotLightCount,
            SpotLightBufferOffset = checked((uint)(
                lights.Directional.Count + lights.Points.Count)),
            SpotViewOffset = data.Views.SpotViewOffset,
            CullBackfacingPixels = _shadowMap.Settings.CullBackfacingPixels ? 1u : 0u,
            FirstPersonPixelRequestBias = _shadowMap.Settings.FirstPersonPixelRequestBias,
            FirstPersonPixelRequestMinLevel = _shadowMap.Settings.FirstPersonPixelRequestMinLevel,
            FrameIndex = data._frameIndex,
            PageDilationBorderPages = (float)(
                Math.Max(
                    _shadowMap.Settings.BlockerSearchRadiusTexels,
                    Math.Max(_shadowMap.Settings.MaxFilterRadiusTexels, _shadowMap.Settings.PrefilteredDistantEnabled ? 2f : 0f)) /
                _shadowMap.Settings.PageSize),
            NormalBias = _shadowMap.Settings.NormalBias,
            ReceiverProjection = receiverProjection,
        };
        _uniformData.Write(ref graph, _demandUniforms, in demand);
        UploadPoolUniforms(ref graph, _poolPrepareUniforms, 0u);
        UploadPoolUniforms(ref graph, _poolUniforms, 1u);
        VirtualShadowPageCompactionPasses.SetFrameParameters(ref graph,_poolCompaction,_shadowMap.Settings.MaxPages);
        UploadPoolUniforms(ref graph, _poolFinishUniforms, 3u);
        var pageOrderDispatch = new VirtualShadowDispatchPassData(new DispatchArguments(
            DivideRoundUp(_shadowMap.Settings.MaxPages, 256u), 1, 1));
        graph.SetPassDataIfChanged(_poolPass, pageOrderDispatch);
        graph.SetPassDataIfChanged(_poolPreparePass, pageOrderDispatch);
        graph.SetPassEnabled(_poolPass, viewCount != 0u);
        graph.SetPassEnabled(_poolPreparePass, viewCount != 0u);
        graph.SetRegionEnabled(_poolCompaction.Region,viewCount != 0u);
        graph.SetPassEnabled(_poolFinishPass, viewCount != 0u);

        UploadAllocationUniforms(ref graph);
        graph.SetPassEnabled(_allocatePass, viewCount != 0u);

        var clearUniforms = new VirtualShadowPageClearUniforms
        {
            PageSize = _shadowMap.Settings.PageSize,
            AtlasPagesPerRow = _shadowMap.Settings.AtlasPagesPerRow,
            AtlasSize = _shadowMap.Settings.AtlasSize,
        };
        _uniformData.Write(ref graph, _clearUniforms, in clearUniforms);
        graph.SetPassEnabled(_clearPass, viewCount != 0u);

        var pageHiZInitializeUniforms = new VirtualShadowPageHiZUniforms
        {
            PageSize = _shadowMap.Settings.PageSize,
            AtlasPagesPerRow = _shadowMap.Settings.AtlasPagesPerRow,
            StaticLayerOffset = PageHiZStaticLayerOffset,
        };
        _uniformData.Write(ref graph, _pageHiZInitializeUniforms, in pageHiZInitializeUniforms);
        graph.SetPassEnabled(_pageHiZInitializePass, viewCount != 0u);
        graph.SetPassEnabled(_finalPageHiZInitializePass, viewCount != 0u);
        for (int index = 0; index < _pageHiZReducePasses.Length; index++)
        {
            int mip = (index + 1) * 4;
            var pageHiZReduceUniforms = new VirtualShadowPageHiZUniforms
            {
                PageSize = _shadowMap.Settings.PageSize,
                AtlasPagesPerRow = _shadowMap.Settings.AtlasPagesPerRow,
                SourceMip = checked((uint)(mip - 1)),
                DestinationMip = checked((uint)mip),
                StaticLayerOffset = PageHiZStaticLayerOffset,
            };
            _uniformData.Write(ref graph, _pageHiZReduceUniforms[index], in pageHiZReduceUniforms);
            graph.SetPassEnabled(_pageHiZReducePasses[index], viewCount != 0u);
            graph.SetPassEnabled(
                _finalPageHiZReducePasses[index],
                viewCount != 0u);
        }

        var compactUniforms = new VirtualShadowRasterWorkUniforms
        {
            ViewCount = viewCount,
            MipLevelCount = _shadowMap.Settings.MipLevelCount,
            PageTableEntriesPerView = _shadowMap.Settings.PageTableEntriesPerView,
            HierarchyWordsPerView = _shadowMap.Settings.PageHierarchyWordsPerView,
            ActivePageCapacity = _shadowMap.Settings.MaxPages,
            PageSize = _shadowMap.Settings.PageSize,
            PageHiZMipCount = checked((uint)(
                BitOperations.Log2(Math.Max(_shadowMap.Settings.PageSize / 2u, 1u)) + 1)),
            AtlasPagesPerRow = _shadowMap.Settings.AtlasPagesPerRow,
            InstanceGroupCount = DivideRoundUp(checked((uint)instanceCount), 64u),
            HasPreviousHistory = data._frameHasPreviousHistory ? 1u : 0u,
            StaticHiZLayerOffset = PageHiZStaticLayerOffset,
            WorkFeedbackEnabled = _quality._qualityReadbacks is not null ? 3u : (_shadowMap.Settings.WorkThrottlingEnabled ? 1u : 0u),
        };
        _uniformData.Write(ref graph, _frameDispatchUniforms, in compactUniforms);
        graph.SetPassDataIfChanged(_compactViewsPass,
            new VirtualShadowDispatchPassData(new DispatchArguments(
                DivideRoundUp(checked(viewCount * _shadowMap.Settings.MipLevelCount), 64u),
                1u,
                1u)));
        graph.SetPassEnabled(_compactViewsPass, viewCount != 0u);

        graph.SetPassEnabled(_rasterCompletePass, viewCount != 0u);
        graph.SetPassEnabled(_frameCompletePass, true);
        UpdateResidentPages(ref graph, viewCount);
        graph.SetPassEnabled(_frameResetPass, true);
        uint resetWords = Math.Max(checked(viewCount * _shadowMap.Settings.PageHierarchyWordsPerView * 2u),
            checked(viewCount * _shadowMap.Settings.MipLevelCount));
        graph.SetPassDataIfChanged(_frameResetPass, new VirtualShadowDispatchPassData(
            new DispatchArguments(Math.Max(1u, DivideRoundUp(resetWords, 256u)), 1u, 1u)));

    }

    internal void UploadPoolUniforms(ref RenderGraphFrame graph, GraphBufferInput input, uint phase)
    {
        VirtualShadowFrame data = GetFrame(ref graph);
        var uniforms = new VirtualShadowPagePoolUniforms
        {
            MaxPages = _shadowMap.Settings.MaxPages,
            PageTableEntryCount = data.Views.PageTableEntryCount,
            FrameIndex = data._frameIndex,
            PageListPhase = phase,
            PageTableEntriesPerView = _shadowMap.Settings.PageTableEntriesPerView,
            HierarchyWordsPerView = _shadowMap.Settings.PageHierarchyWordsPerView,
            MipLevelCount = _shadowMap.Settings.MipLevelCount,
            ViewCount = checked((uint)data._viewCount),
        };
        _uniformData.Write(ref graph, input, in uniforms);
    }

    internal void UploadAllocationUniforms(ref RenderGraphFrame graph)
    {
        VirtualShadowFrame data = GetFrame(ref graph);
        var uniforms = new VirtualShadowPageAllocationUniforms
        {
            PageTableEntryCount = data.Views.PageTableEntryCount,
            PageTableEntriesPerView = _shadowMap.Settings.PageTableEntriesPerView,
            MaxPages = _shadowMap.Settings.MaxPages,
            ViewCount = checked((uint)data._viewCount),
            DeferredInvalidationBudget = _shadowMap.Settings.DeferredInvalidationBudget,
            FrameIndex = data._frameIndex,
            HierarchyWordsPerView = _shadowMap.Settings.PageHierarchyWordsPerView,
            MipLevelCount = _shadowMap.Settings.MipLevelCount,
        };
        _uniformData.Write(ref graph, _allocationUniforms, in uniforms);
    }

    internal void BindInitialize(ref RenderGraphEdit graph, GraphBufferInput uniforms)
    {
        const PipelineSync sync = PipelineSync.ComputeShading;
        var resources = new Dictionary<string, GraphParameterResourceBinding>
        {
            ["StateInit"] = GraphParameterResourceBinding.ConstantBuffer(uniforms, sync),
            ["ShadowPageTable"] = GraphParameterResourceBinding.WritableBuffer(graph.CreateBufferUav(_shadowMap.PageTable, structureStride: 4), sync, GraphAccessMode.Write, WriteCoverage.Complete),
            ["PageAssignments"] = GraphParameterResourceBinding.WritableBuffer(graph.CreateBufferUav(_shadowMap.PageAssignments, structureStride: 4), sync, GraphAccessMode.Write, WriteCoverage.Complete),
            ["ShadowPageLists"] = GraphParameterResourceBinding.WritableBuffer(graph.CreateBufferUav(_shadowMap.PageLists, structureStride: 4), sync, GraphAccessMode.Write, WriteCoverage.Complete),
            ["PageMetadata"] = GraphParameterResourceBinding.WritableBuffer(graph.CreateBufferUav(_shadowMap.PageMetadata, structureStride: VirtualShadowPageMetadata.SizeInBytes), sync, GraphAccessMode.Write, WriteCoverage.Complete),
            ["RequestedPageFlags"] = GraphParameterResourceBinding.WritableBuffer(graph.CreateBufferUav(_requests._requestedPageFlags, structureStride: 4), sync, GraphAccessMode.Write, WriteCoverage.Complete),
            ["RequestedPageCount"] = GraphParameterResourceBinding.WritableBuffer(graph.CreateBufferUav(_requests._requestedPageCount, structureStride: 4), sync, GraphAccessMode.Write, WriteCoverage.Complete),
            ["PageReceiverMasks"] = GraphParameterResourceBinding.StorageTexture(graph.CreateTextureUav(_requests._pageReceiverMasks,
                new(0, 1, 0, 1, TextureAspects.Color), Format.R32UInt, TextureViewDimension.Texture2D), sync, GraphAccessMode.Write, WriteCoverage.Complete),
        };
        for (uint mip = 1; mip <= 7; mip++)
            resources.Add("PageReceiverMaskMip" + mip, GraphParameterResourceBinding.StorageTexture(graph.CreateTextureUav(_requests._pageReceiverMasks,
                new(Math.Min(mip, _requests.ReceiverMaskMipCount - 1u), 1, 0, 1, TextureAspects.Color), Format.R32UInt, TextureViewDimension.Texture2D),
                sync, GraphAccessMode.Write, WriteCoverage.Complete));
        BindComputeResources(ref graph, _initializePass, resources);
    }

    private IReadOnlyDictionary<string, GraphParameterResourceBinding> CreateCasterBindings(
        ref RenderGraphEdit graph, GraphBufferInput uniforms) => new Dictionary<string, GraphParameterResourceBinding>
    {
        ["Invalidation"] = GraphParameterResourceBinding.ConstantBuffer(graph.CreateBufferCbv(uniforms), PipelineSync.ComputeShading),
        ["ShadowViews"] = GraphParameterResourceBinding.ReadOnlyBuffer(graph.CreateBufferSrv(_frameShadowViews,
            structureStride: VirtualShadowView.SizeInBytes), PipelineSync.ComputeShading),
        ["InstanceWorldBounds"] = GraphParameterResourceBinding.ReadOnlyBuffer(graph.CreateBufferSrv(_casterBounds,
            structureStride: 16), PipelineSync.ComputeShading),
        ["ChangedInstances"] = GraphParameterResourceBinding.ReadOnlyBuffer(graph.CreateBufferSrv(_frameChangedInstances,
            structureStride: sizeof(uint)), PipelineSync.ComputeShading),
        ["RasterTargetDepthCacheClasses"] = GraphParameterResourceBinding.ReadOnlyBuffer(graph.CreateBufferSrv(
            _casters._frameDepthCacheClasses, structureStride: sizeof(uint)), PipelineSync.ComputeShading),
        ["PreviousDepthCacheClasses"] = GraphParameterResourceBinding.ReadOnlyBuffer(graph.CreateBufferSrv(
            _casters._framePreviousDepthCacheClasses, structureStride: sizeof(uint)), PipelineSync.ComputeShading),
        ["PreviousCasterRows"] = GraphParameterResourceBinding.ReadOnlyBuffer(graph.CreateBufferSrv(
            _casters._framePreviousCasterRows, structureStride: sizeof(uint)), PipelineSync.ComputeShading),
        ["LodWorldBounds"] = GraphParameterResourceBinding.ReadOnlyBuffer(graph.CreateBufferSrv(_casters._frameLodBounds,
            structureStride: 16), PipelineSync.ComputeShading),
        ["RequestedPageFlags"] = GraphParameterResourceBinding.ReadOnlyBuffer(graph.CreateBufferSrv(
            _requests._requestedPageFlags, structureStride: sizeof(uint)), PipelineSync.ComputeShading),
        ["AllocatedPageHierarchy"] = GraphParameterResourceBinding.ReadOnlyBuffer(graph.CreateBufferSrv(
            _allocatedPageHierarchy, structureStride: sizeof(uint)), PipelineSync.ComputeShading),
        ["AllocatedPageRectBounds"] = GraphParameterResourceBinding.ReadOnlyBuffer(graph.CreateBufferSrv(
            _allocatedPageRectBounds, structureStride: 4 * sizeof(uint)), PipelineSync.ComputeShading),
        ["PageMetadata"] = GraphParameterResourceBinding.WritableBuffer(graph.CreateBufferUav(_shadowMap.PageMetadata,
            structureStride: VirtualShadowPageMetadata.SizeInBytes), PipelineSync.ComputeShading,
            GraphAccessMode.ReadWrite, WriteCoverage.Partial, unorderedAccessHazards: UnorderedAccessHazardMode.AtomicMergeable),
        ["ShadowPageTable"] = GraphParameterResourceBinding.WritableBuffer(graph.CreateBufferUav(_shadowMap.PageTable,
            structureStride: sizeof(uint)), PipelineSync.ComputeShading, GraphAccessMode.ReadWrite, WriteCoverage.Partial),
    };

    internal void BindDynamicResolution(ref RenderGraphEdit graph, GraphBufferInput uniforms)
    {
        PassDefinition pass = graph.GetPass(_dynamicResolutionPass);
        _ = pass.Bind(uniforms, PipelineSync.ComputeShading);
        pass.Bind(GraphParameterResourceBinding.ReadOnlyBufferDescriptor(graph.CreateBufferSrv(_shadowMap.PageLists,
            structureStride: sizeof(uint), label: "Virtual shadow available page count")));
        _ = pass.Bind(graph.CreateBufferSrv(
            _allocationFailures,
            structureStride: sizeof(uint),
            label: "Virtual shadow dynamic resolution allocation failures"),
            PipelineSync.ComputeShading);
        _ = pass.Bind(graph.CreateBufferUav(
            _dynamicResolutionBias,
            structureStride: 2 * sizeof(float),
            label: "Virtual shadow dynamic resolution output"),
            PipelineSync.ComputeShading,
            GraphAccessMode.ReadWrite,
            WriteCoverage.Complete);
    }

    internal void BindCoarseMark(ref RenderGraphEdit graph, GraphBufferInput uniforms)
        => BindCoarseMark(ref graph, _coarseMarkPass, uniforms);

    internal void BindCoarseMark(ref RenderGraphEdit graph, GraphPassId passId, GraphBufferInput uniforms)
    {
        const PipelineSync sync = PipelineSync.ComputeShading;
        BindComputeResources(ref graph, passId, new()
        {
            ["CoarsePageDemand"] = GraphParameterResourceBinding.ConstantBuffer(uniforms, sync),
            ["ShadowViews"] = GraphParameterResourceBinding.ReadOnlyBuffer(graph.CreateBufferSrv(_frameShadowViews, structureStride: VirtualShadowView.SizeInBytes), sync),
            ["RequestedPageFlags"] = GraphParameterResourceBinding.WritableBuffer(graph.CreateBufferUav(_requests._requestedPageFlags, structureStride: 4), sync),
            ["RequestedPageIndices"] = GraphParameterResourceBinding.WritableBuffer(graph.CreateBufferUav(_requests._requestedPageIndices, structureStride: 4), sync,
                GraphAccessMode.Write, WriteCoverage.Partial, ResourceContentState.Defined, BufferContentScope.CountBoundedPrefix),
            ["RequestedPageCount"] = GraphParameterResourceBinding.WritableBuffer(graph.CreateBufferUav(_requests._requestedPageCount, structureStride: 4), sync),
            ["PageReceiverMasks"] = GraphParameterResourceBinding.StorageTexture(graph.CreateTextureUav(_requests._pageReceiverMasks,
                new(0, 1, 0, 1, TextureAspects.Color), Format.R32UInt, TextureViewDimension.Texture2D), sync),
        });
    }

    internal void BindPool(ref RenderGraphEdit graph, GraphPassId passId, GraphBufferInput uniformBuffer)
    {
        graph.SetPassParameterBindings(passId,[]);
        PassDefinition pass = graph.GetPass(passId);
        LinkedComputePipeline pipeline = _shadowMap.PagePoolPipeline;
        foreach (ShaderParameterResource resource in ShaderParameterResources.Create(pipeline.Shader,
            _renderPipelines.Device.Capabilities.CompileTarget,pipeline.EntryPoint.Name).Resources)
        {
            if (resource.ParameterLayout != VariableLayoutReflection.Null)
            {
                pass.BindParameterBlock(resource.ParameterLayout);
                continue;
            }
            if (!resource.IsUsed)
            {
                pass.Bind(GraphParameterResourceBinding.Null(resource.Type));
                continue;
            }
            GraphParameterResourceBinding? binding = resource.Name switch
            {
                "PagePool" => GraphParameterResourceBinding.ConstantBuffer(uniformBuffer,PipelineSync.ComputeShading),
                "ShadowViews" => GraphParameterResourceBinding.ReadOnlyBuffer(graph.CreateBufferSrv(_frameShadowViews,
                    structureStride:VirtualShadowView.SizeInBytes),PipelineSync.ComputeShading),
                "ShadowPageTable" => GraphParameterResourceBinding.WritableBuffer(graph.CreateBufferUav(_shadowMap.PageTable,
                    structureStride:sizeof(uint)),PipelineSync.ComputeShading),
                "RequestedPageFlags" => GraphParameterResourceBinding.ReadOnlyBuffer(graph.CreateBufferSrv(
                    _requests._requestedPageFlags,structureStride:sizeof(uint)),PipelineSync.ComputeShading),
                "RequestedPageCount" => GraphParameterResourceBinding.ReadOnlyBuffer(graph.CreateBufferSrv(
                    _requests._requestedPageCount,structureStride:sizeof(uint)),PipelineSync.ComputeShading),
                "PageRequestDispatchArgs" => GraphParameterResourceBinding.WritableBuffer(graph.CreateBufferUav(
                    _pageRequestDispatchArguments),PipelineSync.ComputeShading,GraphAccessMode.Write,WriteCoverage.Complete),
                "PageAssignments" => GraphParameterResourceBinding.WritableBuffer(graph.CreateBufferUav(_shadowMap.PageAssignments,
                    structureStride:sizeof(uint)),PipelineSync.ComputeShading),
                "PageMetadata" => GraphParameterResourceBinding.WritableBuffer(graph.CreateBufferUav(_shadowMap.PageMetadata,
                    structureStride:VirtualShadowPageMetadata.SizeInBytes),PipelineSync.ComputeShading),
                "ShadowPageLists" => GraphParameterResourceBinding.WritableBufferDescriptor(graph.CreateBufferUav(_shadowMap.PageLists,
                    structureStride:sizeof(uint))),
                "AllocatedPageHierarchy" => GraphParameterResourceBinding.WritableBuffer(graph.CreateBufferUav(
                    _allocatedPageHierarchy,structureStride:sizeof(uint)),PipelineSync.ComputeShading),
                "AllocatedPageRectBounds" => GraphParameterResourceBinding.WritableBuffer(graph.CreateBufferUav(
                    _allocatedPageRectBounds,structureStride:4u * sizeof(uint)),PipelineSync.ComputeShading),
                _ => null,
            };
            if (binding is { } value) pass.Bind(value);
            else if (TryResolve(resource.Name,resource.Type,out GraphResourceBinder binder))
                binder(ref graph,ref pass,PipelineSync.ComputeShading);
            else throw new InvalidDataException($"Virtual shadow pool resource '{resource.Name}' has no binding.");
        }
    }

    internal void BindAllocation(ref RenderGraphEdit graph, GraphPassId passId, GraphBufferInput uniformBuffer)
    {
        const PipelineSync sync = PipelineSync.ComputeShading;
        var resources = new Dictionary<string, GraphParameterResourceBinding>
        {
            ["PageAllocation"] = GraphParameterResourceBinding.ConstantBuffer(uniformBuffer, sync),
            ["ShadowViews"] = GraphParameterResourceBinding.ReadOnlyBuffer(graph.CreateBufferSrv(_frameShadowViews, structureStride: VirtualShadowView.SizeInBytes), sync),
            ["ShadowRanges"] = GraphParameterResourceBinding.ReadOnlyBuffer(graph.CreateBufferSrv(_frameShadowRanges, structureStride: VirtualShadowRange.SizeInBytes), sync),
            ["RequestedPageIndices"] = GraphParameterResourceBinding.ReadOnlyBuffer(graph.CreateBufferSrv(_requests._requestedPageIndices, structureStride: 4), sync, BufferContentScope.CountBoundedPrefix),
            ["RequestedPageCount"] = GraphParameterResourceBinding.ReadOnlyBuffer(graph.CreateBufferSrv(_requests._requestedPageCount, structureStride: 4), sync),
            ["RequestedPageFlags"] = GraphParameterResourceBinding.ReadOnlyBuffer(graph.CreateBufferSrv(_requests._requestedPageFlags, structureStride: 4), sync),
            ["ShadowPageTable"] = GraphParameterResourceBinding.WritableBuffer(graph.CreateBufferUav(_shadowMap.PageTable, structureStride: 4), sync),
            ["PageAssignments"] = GraphParameterResourceBinding.WritableBuffer(graph.CreateBufferUav(_shadowMap.PageAssignments, structureStride: 4), sync),
            ["PageMetadata"] = GraphParameterResourceBinding.WritableBuffer(graph.CreateBufferUav(_shadowMap.PageMetadata, structureStride: VirtualShadowPageMetadata.SizeInBytes), sync),
            ["ShadowPageLists"] = GraphParameterResourceBinding.WritableBufferDescriptor(graph.CreateBufferUav(_shadowMap.PageLists, structureStride: 4)),
            ["ActivePhysicalPages"] = GraphParameterResourceBinding.WritableBuffer(graph.CreateBufferUav(_activePages, structureStride: 16), sync,
                GraphAccessMode.Write, WriteCoverage.Partial, ResourceContentState.Defined, BufferContentScope.CountBoundedPrefix),
            ["ActivePhysicalPageCount"] = GraphParameterResourceBinding.WritableBuffer(graph.CreateBufferUav(_activePageCount, structureStride: 4), sync),
            ["UncachedPageRectBounds"] = GraphParameterResourceBinding.WritableBuffer(graph.CreateBufferUav(_uncachedPageRectBounds, structureStride: 16), sync),
            ["RasterTargetPageHierarchy"] = GraphParameterResourceBinding.WritableBuffer(graph.CreateBufferUav(_uncachedPageHierarchy, structureStride: 8), sync),
            ["ShadowAllocationFailures"] = GraphParameterResourceBinding.WritableBuffer(graph.CreateBufferUav(_allocationFailures, structureStride: 4), sync),
            ["PageReceiverMasks"] = GraphParameterResourceBinding.StorageTexture(graph.CreateTextureUav(_requests._pageReceiverMasks,
                new(0, 1, 0, 1, TextureAspects.Color), Format.R32UInt, TextureViewDimension.Texture2D), sync),
        };
        for (uint mip = 1; mip <= 7; mip++)
            resources.Add("PageReceiverMaskMip" + mip, GraphParameterResourceBinding.StorageTexture(graph.CreateTextureUav(_requests._pageReceiverMasks,
                new(Math.Min(mip, _requests.ReceiverMaskMipCount - 1u), 1, 0, 1, TextureAspects.Color), Format.R32UInt, TextureViewDimension.Texture2D),
                sync, unorderedAccessHazards: UnorderedAccessHazardMode.AtomicMergeable));
        BindComputeResources(ref graph, passId, resources);
    }

    private GraphBufferAccessId[] DeclarePageListAccesses(ref RenderGraphEdit graph, uint capacity)
    {
        const PipelineSync sync = PipelineSync.ComputeShading;
        const ResourceAccess access = ResourceAccess.UnorderedAccess;
        GraphBufferId lists = _shadowMap.PageLists;
        ulong bytes = (ulong)capacity * sizeof(uint);
        ulong stride = bytes + sizeof(uint);
        BufferRange lru = new(0, bytes);
        BufferRange available = new(stride, bytes);
        BufferRange empty = new(2ul * stride, bytes);
        BufferRange requested = new(3ul * stride, bytes);
        BufferRange availableCount = new(stride + bytes, sizeof(uint));
        BufferRange emptyCount = new(2ul * stride + bytes, sizeof(uint));
        BufferRange requestedCount = new(3ul * stride + bytes, sizeof(uint));
        var accesses = new List<GraphBufferAccessId>();

        PassDefinition prepare = graph.GetPass(_poolPreparePass);
        // Finish returns every page to REQUESTED, so its valid prefix has MaxPages entries.
        accesses.Add(prepare.Read(lists, requested, sync, access, contentScope: BufferContentScope.CountBoundedPrefix));
        accesses.Add(prepare.Write(lists, lru, sync, access, WriteCoverage.Complete));
        accesses.Add(prepare.Write(lists, availableCount, sync, access, WriteCoverage.Complete));
        accesses.Add(prepare.Write(lists, emptyCount, sync, access, WriteCoverage.Complete));
        accesses.Add(prepare.Write(lists, requestedCount, sync, access, WriteCoverage.Complete));

        PassDefinition build = graph.GetPass(_poolPass);
        accesses.Add(build.ReadWrite(lists, lru, sync, access));
        accesses.Add(build.Write(lists, empty, sync, access, WriteCoverage.Partial, ResourceContentState.Defined,
            contentScope: BufferContentScope.CountBoundedPrefix));
        accesses.Add(build.ReadWrite(lists, emptyCount, sync, access));
        accesses.Add(build.Write(lists, requested, sync, access, WriteCoverage.Partial, ResourceContentState.Defined,
            contentScope: BufferContentScope.CountBoundedPrefix));
        accesses.Add(build.ReadWrite(lists, requestedCount, sync, access));

        PassDefinition allocate = graph.GetPass(_allocatePass);
        accesses.Add(allocate.Read(lists, available, sync, access, contentScope: BufferContentScope.CountBoundedPrefix));
        accesses.Add(allocate.ReadWrite(lists, availableCount, sync, access));
        accesses.Add(allocate.Write(lists, requested, sync, access, WriteCoverage.Partial, ResourceContentState.Defined,
            contentScope: BufferContentScope.CountBoundedPrefix));
        accesses.Add(allocate.ReadWrite(lists, requestedCount, sync, access));

        PassDefinition finish = graph.GetPass(_poolFinishPass);
        accesses.Add(finish.Read(lists, available, sync, access, contentScope: BufferContentScope.CountBoundedPrefix));
        accesses.Add(finish.Read(lists, availableCount, sync, access));
        accesses.Add(finish.Write(lists, requested, sync, access, WriteCoverage.Partial, ResourceContentState.Defined,
            contentScope: BufferContentScope.CountBoundedPrefix));
        accesses.Add(finish.ReadWrite(lists, requestedCount, sync, access));
        PassDefinition resolution = graph.GetPass(_dynamicResolutionPass);
        accesses.Add(resolution.Read(lists, availableCount, sync, ResourceAccess.ShaderResource));
        return accesses.ToArray();
    }

    internal void BindClear(ref RenderGraphEdit graph, GraphBufferInput uniforms)
    {
        PassDefinition pass = graph.GetPass(_clearPass);
        _ = pass.Bind(uniforms, PipelineSync.ComputeShading);
        _ = pass.Bind(graph.CreateBufferSrv(_activePages, structureStride: 4 * sizeof(uint),
            label: "Virtual shadow active pages to clear"), PipelineSync.ComputeShading,
            BufferContentScope.CountBoundedPrefix);
        _ = pass.Bind(graph.CreateBufferSrv(_activePageCount, structureStride: sizeof(uint)),
            PipelineSync.ComputeShading);
        _ = pass.Bind(graph.CreateTextureUav(_shadowMap.Atlas, format: Format.R32UInt,
            dimension: SomeEngine.Graphics.TextureViewDimension.Texture2DArray,
            label: "Virtual shadow atlas clear target"), PipelineSync.ComputeShading,
            GraphAccessMode.ReadWrite, WriteCoverage.Partial);
        _ = pass.Bind(graph.CreateBufferSrv(_shadowMap.PageMetadata, structureStride: VirtualShadowPageMetadata.SizeInBytes), PipelineSync.ComputeShading);
    }

    internal void BindCompact(ref RenderGraphEdit graph, GraphBufferInput uniforms, GraphBufferInput sourceViews)
    {
        const PipelineSync sync = PipelineSync.ComputeShading;
        BindComputeResources(ref graph, _compactViewsPass, new()
        {
            ["ViewCompact"] = GraphParameterResourceBinding.ConstantBuffer(uniforms, sync),
            ["UncachedPageRectBounds"] = GraphParameterResourceBinding.ReadOnlyBuffer(graph.CreateBufferSrv(_uncachedPageRectBounds, structureStride: 16), sync),
            ["ActivePhysicalPageCount"] = GraphParameterResourceBinding.ReadOnlyBuffer(graph.CreateBufferSrv(_activePageCount, structureStride: 4), sync),
            ["ShadowViews"] = GraphParameterResourceBinding.ReadOnlyBuffer(graph.CreateBufferSrv(_frameShadowViews, structureStride: VirtualShadowView.SizeInBytes), sync),
            ["ClusterSourceViews"] = GraphParameterResourceBinding.ReadOnlyBuffer(graph.CreateBufferSrv(sourceViews, structureStride: RenderRasterContext.ViewSizeInBytes), sync),
            ["RasterViews"] = GraphParameterResourceBinding.WritableBuffer(graph.CreateBufferUav(_rasterDispatchViews, structureStride: RenderRasterContext.ViewSizeInBytes), sync,
                GraphAccessMode.Write, WriteCoverage.Partial, ResourceContentState.Defined, BufferContentScope.CountBoundedPrefix),
            ["RasterTargetViews"] = GraphParameterResourceBinding.WritableBuffer(graph.CreateBufferUav(_rasterTargetViews, structureStride: RenderRasterContext.TargetViewSizeInBytes), sync,
                GraphAccessMode.Write, WriteCoverage.Partial, ResourceContentState.Defined, BufferContentScope.CountBoundedPrefix),
            ["ClusterDispatchArguments"] = GraphParameterResourceBinding.WritableBuffer(graph.CreateBufferUav(_rasterDispatchArguments), sync),
            ["ShadowPageWorkDispatchArguments"] = GraphParameterResourceBinding.WritableBuffer(graph.CreateBufferUav(_pageWorkDispatchArguments), sync,
                GraphAccessMode.Write, WriteCoverage.Complete),
        });
    }

    internal void BindRasterDispatchCount(
        ref RenderGraphEdit graph,
        ref PassDefinition pass, PipelineSync sync)
    {
        _ = pass.Bind(
            graph.CreateBufferSrv(
                _rasterDispatchArguments,
                range: new BufferRange(0u, 16u),
                structureStride: sizeof(uint),
                label: "Virtual shadow raster invocation count"),
            sync);
    }

    internal void BindPageHiZInitialize(
        ref RenderGraphEdit graph,
        GraphPassId passId,
        GraphBufferInput uniforms,
        VirtualShadowMapSettings settings)
    {
        PassDefinition pass = graph.GetPass(passId);
        _ = pass.Bind(
            uniforms,
            PipelineSync.ComputeShading);
        _ = pass.Bind(
            graph.CreateBufferSrv(
                _activePages,
                structureStride: 4 * sizeof(uint),
                label: "Virtual shadow page HiZ active pages"),
            PipelineSync.ComputeShading,
            BufferContentScope.CountBoundedPrefix);
        _ = pass.Bind(graph.CreateBufferSrv(_activePageCount, structureStride: sizeof(uint)),
            PipelineSync.ComputeShading);
        _ = pass.Bind(graph.CreateBufferSrv(_shadowMap.PageMetadata,
            structureStride: VirtualShadowPageMetadata.SizeInBytes,
            label: "Virtual shadow page HiZ depth validity"), PipelineSync.ComputeShading);
        _ = pass.Bind(
            graph.CreateTextureSrv(
                _shadowMap.Atlas,
                format: Format.R32UInt,
                dimension: SomeEngine.Graphics.TextureViewDimension.Texture2DArray,
                label: "Virtual shadow page HiZ source atlas"),
            PipelineSync.ComputeShading);
        BindPageHiZDestinations(ref graph, ref pass, 0u, settings);
    }

    internal void BindPageHiZReduce(
        ref RenderGraphEdit graph,
        GraphPassId passId,
        GraphBufferInput uniforms,
        uint sourceMip,
        uint destinationMip,
        VirtualShadowMapSettings settings)
    {
        PassDefinition pass = graph.GetPass(passId);
        _ = pass.Bind(
            uniforms,
            PipelineSync.ComputeShading);
        _ = pass.Bind(
            graph.CreateBufferSrv(
                _activePages,
                structureStride: 4 * sizeof(uint),
                label: "Virtual shadow page HiZ active pages"),
            PipelineSync.ComputeShading,
            BufferContentScope.CountBoundedPrefix);
        _ = pass.Bind(graph.CreateBufferSrv(_activePageCount, structureStride: sizeof(uint)),
            PipelineSync.ComputeShading);
        _ = pass.Bind(graph.CreateBufferSrv(_shadowMap.PageMetadata,
            structureStride: VirtualShadowPageMetadata.SizeInBytes,
            label: "Virtual shadow page HiZ depth validity"), PipelineSync.ComputeShading);
        _ = pass.Bind(
            graph.CreateTextureSrv(
                _pageHiZ,
                new TextureSubresourceRange(
                    sourceMip,
                    1,
                    0,
                    CalculatePageHiZLayerCount(settings),
                    TextureAspects.Color),
                Format.R32Float,
                SomeEngine.Graphics.TextureViewDimension.Texture2DArray,
                "Virtual shadow page HiZ source mip"),
            PipelineSync.ComputeShading);
        BindPageHiZDestinations(ref graph, ref pass, destinationMip, settings);
    }

    internal void BindPageHiZDestinations(
        ref RenderGraphEdit graph, ref PassDefinition pass, uint firstMip, VirtualShadowMapSettings settings)
    {
        uint lastMip = checked((uint)BitOperations.Log2(Math.Max(settings.PageSize / 2u, 1u)));
        uint layerCount = CalculatePageHiZLayerCount(settings);
        for (uint offset = 0u; offset < 4u; offset++)
        {
            _ = pass.Bind(
                graph.CreateTextureUav(
                    _pageHiZ,
                    new TextureSubresourceRange(Math.Min(firstMip + offset, lastMip), 1,
                        0, layerCount, TextureAspects.Color),
                    Format.R32Float,
                    SomeEngine.Graphics.TextureViewDimension.Texture2DArray,
                    "Virtual shadow page HiZ destination mip"),
                PipelineSync.ComputeShading, GraphAccessMode.Write, WriteCoverage.Partial);
        }
    }

    private void BindLightViewIndices(ref RenderGraphEdit graph, ref PassDefinition pass, PipelineSync sync) =>
        pass.Bind(GraphParameterResourceBinding.ReadOnlyBuffer(
            graph.CreateBufferSrv(_frameLightViewIndices, structureStride: sizeof(uint),
                label: "Shadow light view indices"), sync, BufferContentScope.CountBoundedPrefix));

    internal void BindShadeViews(ref RenderGraphEdit graph, ref PassDefinition pass, PipelineSync sync)
    {
        _ = pass.Bind(graph.CreateBufferSrv(_frameShadowViews, structureStride: VirtualShadowView.SizeInBytes,
            label: "Standard shade virtual shadow views"), sync);
    }

    internal void BindRangeViews(ref RenderGraphEdit graph, ref PassDefinition pass, PipelineSync sync) =>
        pass.Bind(graph.CreateBufferSrv(_frameShadowRanges, structureStride: VirtualShadowRange.SizeInBytes), sync);

    internal void BindComputeResources(ref RenderGraphEdit graph, GraphPassId id,
        Dictionary<string, GraphParameterResourceBinding> resources)
    {
        LinkedComputePipeline pipeline = _computePasses[id];
        PassDefinition pass = graph.GetPass(id);
        foreach (GraphParameterResourceBinding binding in ShaderEntryBindings.Resolve(pipeline.Shader, pipeline,
            (name, _) => resources.TryGetValue(name, out GraphParameterResourceBinding value) ? value : null))
            pass.Bind(binding);
    }

    internal void BindShadePageTable(ref RenderGraphEdit graph, ref PassDefinition pass, PipelineSync sync)
    {
        _ = pass.Bind(graph.CreateBufferSrv(_shadowMap.PageTable, structureStride: sizeof(uint),
            label: "Standard shade virtual shadow page table"), sync);
    }

    internal void BindShadeAtlas(ref RenderGraphEdit graph, ref PassDefinition pass, PipelineSync sync)
    {
        _ = pass.Bind(graph.CreateTextureSrv(_shadowMap.Atlas, format: Format.R32UInt,
            dimension: SomeEngine.Graphics.TextureViewDimension.Texture2DArray,
            label: "Standard shade virtual shadow atlas"), sync);
    }

    internal void BindShadeBlueNoise(ref RenderGraphEdit graph, ref PassDefinition pass, PipelineSync sync) =>
        pass.Bind(_blueNoiseInput, sync);

    internal void BindShadeSampling(ref RenderGraphEdit graph, ref PassDefinition pass, PipelineSync sync)
    {
        _ = pass.Bind(_frameSamplingUniforms, sync);
    }

    internal void BindFrameResolutionBias(
        ref RenderGraphEdit graph,
        ref PassDefinition pass,
        PipelineSync sync)
    {
        _ = pass.Bind(graph.CreateBufferSrv(
            _frameResolutionBias,
            structureStride: sizeof(float),
            label: "Virtual shadow dynamic resolution bias for the frame"), sync);
        graph.OrderAfter(pass.Id, _resolutionCopyPass);
    }

    internal GraphPassId AddComputePass(
        ref RenderGraphEdit edit,
        string label,
        LinkedComputePipeline pipeline,
        VirtualShadowDispatchPassData data,
        GraphRegionId region)
    {
        GraphPassId pass = edit.AddComputeFramePass(
            label,
            PassQueueSelection.AnyOfType(QueueType.Graphics),
            data,
            new PassOptions(Culling: PassCullingMode.NeverCull, RepeatCommands: true),
            pipeline.Pipeline,
            pipeline.ParameterLayout,
            default,
            static (ref PassDefinition _, ref VirtualShadowDispatchPassData _) => { },
            VirtualShadowDispatchPassData.Record, region: region);
        _computePasses.Add(pass, pipeline);
        return pass;
    }

    internal GraphPassId AddIndirectPass(
        ref RenderGraphEdit edit,
        string label,
        LinkedComputePipeline pipeline,
        GraphBufferId arguments,
        ulong offset = 0u,
        GraphRegionId region = default)
    {
        GraphPassId pass = CreateIndirectPass(ref edit, label, pipeline, arguments, offset, region);
        _computePasses.Add(pass, pipeline);
        return pass;
    }

    private GraphPassId CreateIndirectPass(
        ref RenderGraphEdit edit,
        string label,
        LinkedComputePipeline pipeline,
        GraphBufferId arguments,
        ulong offset = 0u,
        GraphRegionId region = default)
    {
        var data = new VirtualShadowIndirectPassData(_dispatchLayout, arguments, offset);
        // These passes update the exclusive atlas and HiZ between graphics raster passes.
        GraphPassId pass = edit.AddComputeFramePass(
            label,
            PassQueueSelection.AnyOfType(QueueType.Graphics),
            data,
            new PassOptions(Culling: PassCullingMode.NeverCull, RepeatCommands: true),
            pipeline.Pipeline,
            pipeline.ParameterLayout,
            default,
            VirtualShadowIndirectPassData.Declare,
            VirtualShadowIndirectPassData.Record, region: region);
        return pass;
    }

    internal static GraphBufferId Counter(RenderGraphEdit edit, string label) =>
        edit.CreateBuffer(new BufferDesc(
            sizeof(uint),
            BufferUsages.ShaderRead | BufferUsages.ShaderWrite | BufferUsages.CopyDestination | BufferUsages.CopySource,
            label));

    internal static GraphBufferId DispatchBuffer(
        RenderGraphEdit edit,
        string label,
        uint commandCount = 1u) =>
        edit.CreateBuffer(new BufferDesc(
            checked(DispatchStride * commandCount),
            BufferUsages.ShaderRead | BufferUsages.ShaderWrite |
                BufferUsages.Indirect | BufferUsages.CopyDestination,
            label));

    internal static uint DivideRoundUp(uint value, uint divisor) =>
        value == 0u ? 0u : checked((value + divisor - 1u) / divisor);



    /// <summary>Grows light-dependent storage before beginning the next graph frame.</summary>
    public void ReplaceShader(Shader previous, Shader next)
    {
        _opaqueRequests.ReplaceShader(previous, next);
        var changed = new HashSet<LinkedComputePipeline>();
        foreach (LinkedComputePipeline pipeline in _computePasses.Values.Distinct())
            if (pipeline.ReplaceShader(_renderPipelines, previous, next)) changed.Add(pipeline);
        RenderGraphEdit edit = _renderGraph.BeginEdit();
        try
        {
            VirtualShadowPageCompactionPasses.ReplaceShader(ref edit,_poolCompaction,_renderPipelines,previous,next);
            foreach ((GraphPassId id, LinkedComputePipeline pipeline) in _computePasses)
            {
                if (!changed.Contains(pipeline)) continue;
                PassDefinition pass = edit.GetPass(id);
                pass.SetPipeline(pipeline.Pipeline);
                pass.SetParameterBlock(pipeline.ParameterLayout);
            }
            if (changed.Contains(_shadowMap.PagePoolPipeline))
            {
                BindPool(ref edit,_poolPreparePass,_poolPrepareUniforms);
                BindPool(ref edit,_poolPass,_poolUniforms);
                BindPool(ref edit,_poolFinishPass,_poolFinishUniforms);
            }
            if (changed.Contains(_prefilter._prefilterRequestPipeline!) || changed.Contains(_prefilter._prefilterRequestCapturePipeline!))
                _prefilter.ReplaceRequestBindings(ref edit);
            edit.Commit();
        }
        finally { edit.Dispose(); }
        if (ReferenceEquals(_casterShader, previous)) _casterShader = next;
    }

    public void ReconcileOpaqueRequests(RenderGeometryResources geometry, GraphTextureId visibility,
        GraphTextureId depth, uint width, uint height, uint viewCount) =>
        _opaqueRequests.Reconcile(geometry, visibility, depth, width, height, viewCount);

    public VirtualShadowMapSettings Settings => _shadowMap.Settings;

    public void SetSettings(VirtualShadowMapSettings settings)
    {
        if (_shadowMap.Settings == settings) return;
        settings.Validate();
        VirtualShadowPageCompactionResources compaction = _poolCompaction;
        GraphBufferAccessId[] pageListAccesses = _pageListAccesses;
        RenderGraphEdit edit = _renderGraph.BeginEdit();
        try
        {
            uint sourceViews = Math.Max(_sourceViewCapacity, checked(settings.MaxShadowLights * settings.MaximumViewsPerLight));
            uint tableEntries = Math.Max(_shadowMap.PageTableEntryCapacity, checked(sourceViews * settings.PageTableEntriesPerView));
            _shadowMap.SetSettings(ref edit, settings, tableEntries);
            if (settings.MaxPages != _shadowMap.Settings.MaxPages)
            {
                VirtualShadowPageCompactionPasses.Remove(ref edit, _poolCompaction);
                compaction = VirtualShadowPageCompactionPasses.Add(ref edit, _renderPipelines,
                    _shadowMap.PagePoolPipeline.Shader, _shadowMap.PagePoolPipeline.Shader,
                    _shadowMap.PageLists, settings.MaxPages, _poolPass, _primaryDepthAfter);
                edit.OrderAfter(_allocatePass, compaction.CompletePass);
                foreach (GraphBufferAccessId access in _pageListAccesses) edit.Remove(access);
                pageListAccesses = DeclarePageListAccesses(ref edit, settings.MaxPages);
            }
            ResizeViewStorage(ref edit, sourceViews, settings);
            GrowLightBuffer(ref edit, _activePages, (ulong)settings.MaxPages * 4u * sizeof(uint), "Virtual shadow active raster pages");
            int mipCount = checked(BitOperations.Log2(Math.Max(settings.PageSize / 2u, 1u)) + 1);
            GrowLightBuffer(ref edit, _pageWorkDispatchArguments, (ulong)mipCount * DispatchStride, "Virtual shadow page work arguments");
            edit.SetBufferDescription(_pageWorkDispatchArguments, new BufferDesc((ulong)mipCount * DispatchStride,
                BufferUsages.ShaderRead | BufferUsages.ShaderWrite | BufferUsages.Indirect, "Virtual shadow page work arguments"));
            uint pageHiZPages = Math.Min(settings.AtlasSize / settings.PageSize, 8u);
            uint pageHiZLayers = CalculatePageHiZLayerCount(settings);
            edit.SetTextureDescription(_pageHiZ, new TextureDesc(TextureDimension.Texture2D,
                Math.Max(settings.PageSize / 2u, 1u) * pageHiZPages,
                Math.Max(settings.PageSize / 2u, 1u) * pageHiZPages,
                1, checked((uint)mipCount), pageHiZLayers, 1, Format.R32Float,
                TextureUsages.Sampled | TextureUsages.Storage | TextureUsages.ColorAttachment,
                label: "Virtual shadow physical page HiZ"));
            PageHiZPasses pageHiZ = RefreshPageHiZ(ref edit, mipCount, pageHiZLayers, settings);
            _prefilter.Resize(ref edit, tableEntries, sourceViews, settings);
            edit.Commit();
            _poolCompaction = compaction;
            _pageListAccesses = pageListAccesses;
            AcceptPageHiZ(pageHiZ);
            _shadowMap.AcceptSettings(settings, tableEntries);
            if (sourceViews != _sourceViewCapacity) _shadowMap.InvalidateResources();
            ResizeViewWorkArrays(sourceViews);
            _sourceViewCapacity = sourceViews;
            _requests._requestFlagEntryCapacity = tableEntries;
            _viewEntities.SetSettings(settings);
            _prefilter._prefilterEntryCapacity = tableEntries;
        }
        finally { edit.Dispose(); }
        ConfigureRasterContext();
        if (_shadowMap.RequiresInitialization)
        {
            _quality._workHistoryReady = false;
            _quality._historyWorkViews.Clear();
            _prefilter._prefilterHistoryReady = false;
        }
    }

    private void ConfigureRasterContext()
    {
        var settings = _shadowMap.Settings;
        uint sourceViewCapacity = _sourceViewCapacity;
        int pageHiZMipCount = checked(BitOperations.Log2(Math.Max(settings.PageSize / 2u, 1u)) + 1);
        uint rasterWindows = 0u;
        for (uint mip = 0u; mip < settings.MipLevelCount; mip++)
        {
            uint dimension = (settings.PageTableMipPagesPerRow(mip) + 3u) / 4u;
            rasterWindows = checked(rasterWindows + dimension * dimension);
        }
        _geometry.ConfigureRenderContext(new RenderRasterContext(
            (uint)ViewTargetKind.ShadowPages,
            BindRasterDispatchViews,
            BindClusterSourceViews,
            BindRasterDispatchCount,
            _rasterDispatchArguments,
            _policyRasterWorkCount,
            sourceViewCapacity * settings.MipLevelCount,
            checked(settings.PageSize * 4u),
            checked(settings.PageSize * 4u),
            checked((uint)pageHiZMipCount),
            Math.Max(settings.PageSize / 2u, 1u))
        {
            BindTargetPageBounds = BindRasterPageBounds,
            BindDepthCacheClasses = _casters.BindRasterDepthCacheClasses,
            BindWorkTargetViews = BindRasterTargetViews,
            DepthWorkCounter = _depthWorkCount,
            ViewDetailCount = settings.MipLevelCount,
            MaximumRasterWindowsPerSourceView = rasterWindows,
            DispatchCountIndex = 3u,
        });
    }

    private static uint CalculatePageHiZLayerCount(VirtualShadowMapSettings settings)
    {
        uint pagesPerRow = Math.Min(settings.AtlasPagesPerRow, 8u);
        return DivideRoundUp(settings.MaxPages, pagesPerRow * pagesPerRow) * (settings.DynamicHiZ ? 2u : 1u);
    }

    private PageHiZPasses RefreshPageHiZ(ref RenderGraphEdit edit, int mipCount, uint layerCount,
        VirtualShadowMapSettings settings)
    {
        edit.Remove(_pageHiZStorageInitializePass);
        edit.Remove(_pageHiZInitializePass);
        edit.Remove(_finalPageHiZInitializePass);
        foreach (GraphPassId pass in _pageHiZReducePasses) edit.Remove(pass);
        foreach (GraphPassId pass in _finalPageHiZReducePasses) edit.Remove(pass);
        var initialize = new VirtualShadowPageHiZStorageInitializePassData(_pageHiZ,
            new TextureSubresourceRange(0, checked((uint)mipCount), 0, layerCount, TextureAspects.Color));
        GraphPassId storageInitialize = edit.AddCopyFramePass("Initialize virtual shadow physical page HiZ storage",
            PassQueueSelection.AnyOfType(QueueType.Graphics), initialize,
            new PassOptions(Culling: PassCullingMode.NeverCull, RepeatCommands: true),
            VirtualShadowPageHiZStorageInitializePassData.Declare, VirtualShadowPageHiZStorageInitializePassData.Record,
            _receiverPrepare);
        edit.OrderAfter(storageInitialize, _frameResetPass);
        GraphPassId initializePass = CreateIndirectPass(ref edit, "Initialize virtual shadow physical page HiZ",
            _shadowMap.PageHiZInitializePipeline, _pageWorkDispatchArguments, region: _targetOcclusion);
        GraphPassId finalInitialize = CreateIndirectPass(ref edit, "Finalize virtual shadow physical page HiZ",
            _shadowMap.PageHiZInitializePipeline, _pageWorkDispatchArguments, region: _quality._qualityRegion);
        BindPageHiZInitialize(ref edit, initializePass, _pageHiZInitializeUniforms, settings);
        BindPageHiZInitialize(ref edit, finalInitialize, _pageHiZInitializeUniforms, settings);
        edit.OrderAfter(initializePass, storageInitialize);
        edit.OrderAfter(initializePass, _staticMergeIntermediatePass);
        edit.OrderAfter(finalInitialize, _staticMergeFinalPass);
        GraphBufferInput[] previousUniforms = _pageHiZReduceUniforms;
        int count = (mipCount - 1) / 4;
        var uniforms = new GraphBufferInput[count];
        var reductions = new GraphPassId[count];
        var finalReductions = new GraphPassId[count];
        var added = new List<GraphBufferInput>();
        for (int index = 0; index < count; index++)
        {
            uint mip = checked((uint)(index + 1) * 4u);
            GraphBufferInput input = index < previousUniforms.Length ? previousUniforms[index] : edit.CreateBufferInput(BufferUsages.Constant);
            if (index >= previousUniforms.Length) added.Add(input);
            uniforms[index] = input;
            reductions[index] = CreateIndirectPass(ref edit, $"Reduce virtual shadow page HiZ mip {mip}",
                _shadowMap.PageHiZReducePipeline, _pageWorkDispatchArguments, (ulong)mip * DispatchStride, _targetOcclusion);
            finalReductions[index] = CreateIndirectPass(ref edit, $"Finalize virtual shadow page HiZ mip {mip}",
                _shadowMap.PageHiZReducePipeline, _pageWorkDispatchArguments, (ulong)mip * DispatchStride, _quality._qualityRegion);
            BindPageHiZReduce(ref edit, reductions[index], input, mip - 1u, mip, settings);
            BindPageHiZReduce(ref edit, finalReductions[index], input, mip - 1u, mip, settings);
            edit.OrderAfter(reductions[index], index == 0 ? initializePass : reductions[index - 1]);
            edit.OrderAfter(finalReductions[index], index == 0 ? finalInitialize : finalReductions[index - 1]);
        }
        FeatureUniformBuffer uniformData = added.Count == 0 ? _uniformData :
            _uniformData.AddInputs(ref edit, CollectionsMarshal.AsSpan(added));
        edit.OrderAfter(_rasterCompletePass, count == 0 ? finalInitialize : finalReductions[^1]);
        return new(storageInitialize, initializePass, finalInitialize, uniforms, reductions, finalReductions, uniformData);
    }

    private void AcceptPageHiZ(in PageHiZPasses passes)
    {
        _computePasses.Remove(_pageHiZInitializePass);
        _computePasses.Remove(_finalPageHiZInitializePass);
        foreach (GraphPassId pass in _pageHiZReducePasses) _computePasses.Remove(pass);
        foreach (GraphPassId pass in _finalPageHiZReducePasses) _computePasses.Remove(pass);
        _computePasses.Add(passes.Initialize, _shadowMap.PageHiZInitializePipeline);
        _computePasses.Add(passes.FinalInitialize, _shadowMap.PageHiZInitializePipeline);
        foreach (GraphPassId pass in passes.Reductions) _computePasses.Add(pass, _shadowMap.PageHiZReducePipeline);
        foreach (GraphPassId pass in passes.FinalReductions) _computePasses.Add(pass, _shadowMap.PageHiZReducePipeline);
        _pageHiZStorageInitializePass = passes.StorageInitialize;
        _pageHiZInitializePass = passes.Initialize;
        _finalPageHiZInitializePass = passes.FinalInitialize;
        _pageHiZReduceUniforms = passes.Uniforms;
        _pageHiZReducePasses = passes.Reductions;
        _finalPageHiZReducePasses = passes.FinalReductions;
        _uniformData = passes.UniformData;
    }

    private readonly record struct PageHiZPasses(GraphPassId StorageInitialize, GraphPassId Initialize,
        GraphPassId FinalInitialize, GraphBufferInput[] Uniforms, GraphPassId[] Reductions,
        GraphPassId[] FinalReductions, FeatureUniformBuffer UniformData);

    public void ReconcileLights(RenderLightSet lights)
    {
        _quality.ReconcileFeedback(_renderGraph.LastCompletedFrame);
        uint lightCount = checked((uint)(lights.Directional.Count + lights.Points.Count + lights.Spots.Count));
        uint sourceViews = checked(lightCount * _shadowMap.Settings.MaximumViewsPerLight);
        if (sourceViews <= _sourceViewCapacity) return;
        RenderGraphEdit edit = _renderGraph.BeginEdit();
        try
        {
            ResizeViewStorage(ref edit, sourceViews, _shadowMap.Settings);
            edit.Commit();
            ResizeViewWorkArrays(sourceViews);
            _sourceViewCapacity = sourceViews;
            _requests._requestFlagEntryCapacity = Math.Max(_shadowMap.PageTableEntryCapacity, checked(sourceViews * _shadowMap.Settings.PageTableEntriesPerView));
            _shadowMap.AcceptPageTableCapacity(_requests._requestFlagEntryCapacity);
            _shadowMap.InvalidateResources();
            _prefilter._prefilterEntryCapacity = _requests._requestFlagEntryCapacity;
            _prefilter._prefilterHistoryReady = false;
        }
        finally { edit.Dispose(); }
        ConfigureRasterContext();
    }

    private bool EnsurePageTableCapacity(uint entries)
    {
        if (entries <= _shadowMap.PageTableEntryCapacity) return false;
        RenderGraphEdit edit = _renderGraph.BeginEdit();
        try
        {
            _shadowMap.SetPageTableCapacity(ref edit, entries);
            GrowLightBuffer(ref edit, _requests._requestedPageFlags, (ulong)entries * sizeof(uint), "Virtual shadow request flags");
            GrowLightBuffer(ref edit, _requests._requestedPageIndices, (ulong)entries * sizeof(uint), "Virtual shadow requested page indices");
            GrowPageSamplingStorage(ref edit, entries, _sourceViewCapacity, _shadowMap.Settings);
            edit.Commit();
            _shadowMap.AcceptPageTableCapacity(entries);
            _requests._requestFlagEntryCapacity = entries;
            _prefilter._prefilterEntryCapacity = entries;
            _prefilter._prefilterHistoryReady = false;
            _quality._workHistoryReady = false;
        }
        finally { edit.Dispose(); }
        return true;
    }

    private void ResizeViewStorage(ref RenderGraphEdit edit, uint sourceViews, VirtualShadowMapSettings settings)
    {
        uint tableEntries = Math.Max(_shadowMap.PageTableEntryCapacity, checked(sourceViews * settings.PageTableEntriesPerView));
        uint hierarchyWords = checked(sourceViews * settings.PageHierarchyWordsPerView);
        uint rasterViews = checked(sourceViews * settings.MipLevelCount);
        _shadowMap.SetPageTableCapacity(ref edit, tableEntries);
        GrowLightBuffer(ref edit, _shadowViewBuffer, (ulong)sourceViews * VirtualShadowView.SizeInBytes, "Virtual shadow view parameters");
        GrowLightBuffer(ref edit, _requests._requestedPageFlags, (ulong)tableEntries * sizeof(uint), "Virtual shadow request flags");
        GrowLightBuffer(ref edit, _requests._requestedPageIndices, (ulong)tableEntries * sizeof(uint), "Virtual shadow requested page indices");
        TextureDesc receiverMask = _requests.CreateReceiverMaskDescription(sourceViews, settings);
        edit.SetTextureDescription(_requests._pageReceiverMasks, receiverMask);
        if (_quality._qualityReadbacks is not null)
        {
            edit.SetBufferDescription(_quality._qualityReceiverCoverage, new BufferDesc((ulong)((receiverMask.Width * 4u + 255u) & ~255u) * receiverMask.Height,
                BufferUsages.CopyDestination | BufferUsages.CopySource, "Virtual shadow quality receiver cells"));
            edit.SetBufferDescription(_quality._qualityReadbacks.ActivePages, new((ulong)settings.MaxPages * 16u,
                BufferUsages.CopyDestination | BufferUsages.CopySource, "Virtual shadow quality active pages"));
        }
        GrowLightBuffer(ref edit, _uncachedPageRectBounds, (ulong)rasterViews * 4u * sizeof(uint), "Virtual shadow uncached page rect bounds");
        GrowLightBuffer(ref edit, _allocatedPageRectBounds, (ulong)rasterViews * 4u * sizeof(uint), "Virtual shadow allocated page rect bounds");
        GrowLightBuffer(ref edit, _uncachedPageHierarchy, (ulong)hierarchyWords * 2u * sizeof(uint), "Virtual shadow uncached page hierarchy");
        GrowLightBuffer(ref edit, _allocatedPageHierarchy, (ulong)hierarchyWords * sizeof(uint), "Virtual shadow allocated page hierarchy");
        GrowLightBuffer(ref edit, _policyRasterWorkCount, (1ul + 2ul * sourceViews) * sizeof(uint), "Virtual shadow policy raster work count");
        if (_quality._workFeedbackPipeline is not null)
        {
            GrowLightBuffer(ref edit, _quality._previousWorkCounts, (1ul + 2ul * sourceViews) * sizeof(uint), "Previous virtual shadow visible work");
            GrowLightBuffer(ref edit, _quality._previousWorkPressure, (1ul + sourceViews) * sizeof(float), "Previous virtual shadow work pressure");
            GrowLightBuffer(ref edit, _quality._frameWorkPressure, (1ul + sourceViews) * sizeof(float), "Frame virtual shadow work pressure");
        }
        GrowLightBuffer(ref edit, _depthWorkCount, (ulong)sourceViews * 4u * sizeof(uint), "Virtual shadow static/dynamic visible work");
        GrowLightBuffer(ref edit, _rasterDispatchViews, (ulong)rasterViews * RenderRasterContext.ViewSizeInBytes, "Virtual shadow raster invocation views");
        GrowLightBuffer(ref edit, _rasterTargetViews, (ulong)rasterViews * RenderRasterContext.TargetViewSizeInBytes, "Virtual shadow raster target views");
        GrowPageSamplingStorage(ref edit, tableEntries, sourceViews, settings);
    }

    private void ResizeViewWorkArrays(uint sourceViews)
    {
        int count = checked((int)sourceViews);
        Array.Resize(ref _baseShadowBias, count);
        Array.Resize(ref _shadowViews, count);
        Array.Resize(ref _previousShadowViews, count);
        Array.Resize(ref _previousShadowViewProjections, count);
        Array.Resize(ref _viewRanges, count);
        Array.Resize(ref _rangeEntryCounts, count);
    }

    internal static void GrowLightBuffer(ref RenderGraphEdit edit, GraphBufferId buffer, ulong size, string label)
        => edit.SetBufferDescription(buffer, new BufferDesc(checked(size),
            BufferUsages.ShaderRead | BufferUsages.ShaderWrite | BufferUsages.CopySource | BufferUsages.CopyDestination, label));

    internal void GrowPageSamplingStorage(ref RenderGraphEdit edit, uint entries, uint sourceViews, VirtualShadowMapSettings settings)
    {
        GrowLightBuffer(ref edit, _prefilter._prefilterPreviousPages, (ulong)entries * 2u * sizeof(uint), "Virtual shadow prefilter page histories");
        edit.SetTextureDescription(_prefilter._prefilterPreviousMasks, _prefilter.CreateReceiverHistoryDescription(sourceViews, settings));
    }


    internal GraphPassId _staticMergeIntermediatePass;
    internal GraphPassId _staticMergeFinalPass;
    internal LinkedComputePipeline? _staticMergePipeline;

    internal void InstallStaticDepth(ref RenderGraphEdit edit, VirtualShadowMapShaders shaders,
        GraphRegionId prepare, GraphRegionId finish)
    {
        _staticMergePipeline = LinkedComputePipeline.Create(_renderPipelines, shaders.MergeStaticShader, shaders.MergeStatic, "Virtual shadow static depth merge");
        _staticMergeIntermediatePass = AddIndirectPass(ref edit, "Merge main virtual shadow static depth",
            _staticMergePipeline, _pageWorkDispatchArguments, region: _targetOcclusion);
        _staticMergeFinalPass = AddIndirectPass(ref edit, "Merge post virtual shadow static depth",
            _staticMergePipeline, _pageWorkDispatchArguments, region: finish);
        foreach (GraphPassId id in new[] { _staticMergeIntermediatePass, _staticMergeFinalPass })
        {
            PassDefinition pass = edit.GetPass(id);
            _ = pass.Bind(_clearUniforms, PipelineSync.ComputeShading);
            _ = pass.Bind(edit.CreateBufferSrv(_activePages, structureStride: 16), PipelineSync.ComputeShading,
                BufferContentScope.CountBoundedPrefix);
            _ = pass.Bind(edit.CreateBufferSrv(_activePageCount, structureStride: sizeof(uint)), PipelineSync.ComputeShading);
            _ = pass.Bind(edit.CreateBufferSrv(_shadowMap.PageMetadata, structureStride: VirtualShadowPageMetadata.SizeInBytes), PipelineSync.ComputeShading);
            _ = pass.Bind(edit.CreateTextureUav(_shadowMap.Atlas, format: Format.R32UInt,
                dimension: SomeEngine.Graphics.TextureViewDimension.Texture2DArray), PipelineSync.ComputeShading,
                GraphAccessMode.ReadWrite, WriteCoverage.Partial);
        }
        edit.OrderAfter(_pageHiZInitializePass, _staticMergeIntermediatePass);
        edit.OrderAfter(_finalPageHiZInitializePass, _staticMergeFinalPass);
    }


    internal GraphBufferInput _residentUniforms = null!;
    internal GraphPassId _residentPropagationPass;
    internal LinkedComputePipeline? _residentPipeline;

    [StructLayout(LayoutKind.Sequential, Pack = 4, Size = 16)]
    internal struct ResidentUniforms
    {
        public uint ViewCount;
        public uint EntriesPerView;
        public uint EntryCount;
        public uint Pad0;
    }

    internal void InstallResidentPages(ref RenderGraphEdit edit, VirtualShadowMapShaders shaders, uint entryCapacity,
        GraphRegionId finish)
    {
        _residentUniforms = edit.CreateBufferInput(BufferUsages.Constant);
        _residentPipeline = LinkedComputePipeline.Create(_renderPipelines, shaders.ResidentPagesShader, shaders.ResidentPages, "Virtual shadow resident ancestor propagation");
        _residentPropagationPass = AddComputePass(ref edit, "Propagate virtual shadow resident ancestors",
            _residentPipeline, new VirtualShadowDispatchPassData(default), finish);
        const PipelineSync sync = PipelineSync.ComputeShading;
        BindComputeResources(ref edit, _residentPropagationPass, new()
        {
            ["ResidentUpdate"] = GraphParameterResourceBinding.ConstantBuffer(_residentUniforms, sync),
            ["ResidentShadowViews"] = GraphParameterResourceBinding.ReadOnlyBuffer(edit.CreateBufferSrv(_frameShadowViews, structureStride: VirtualShadowView.SizeInBytes), sync),
            ["ShadowRanges"] = GraphParameterResourceBinding.ReadOnlyBuffer(edit.CreateBufferSrv(_frameShadowRanges, structureStride: VirtualShadowRange.SizeInBytes), sync),
            ["CompletedPageTable"] = GraphParameterResourceBinding.WritableBuffer(edit.CreateBufferUav(_shadowMap.PageTable, structureStride: 4), sync),
        });
        edit.OrderAfter(_residentPropagationPass, _rasterCompletePass);
        edit.OrderAfter(_frameCompletePass, _residentPropagationPass);
    }

    internal void UpdateResidentPages(ref RenderGraphFrame graph, uint viewCount)
    {
        VirtualShadowFrame data = GetFrame(ref graph);
        var uniforms = new ResidentUniforms
        {
            ViewCount = viewCount,
            EntriesPerView = _shadowMap.Settings.PageTableEntriesPerView,
            EntryCount = data.Views.PageTableEntryCount,
        };
        _uniformData.Write(ref graph, _residentUniforms, in uniforms);
        graph.SetPassDataIfChanged(_residentPropagationPass,
            new VirtualShadowDispatchPassData(new DispatchArguments(DivideRoundUp(uniforms.EntryCount, 64u), 1, 1)));
        graph.SetPassEnabled(_residentPropagationPass, viewCount != 0u);
    }

    internal void BindResidentPages(ref RenderGraphEdit graph, ref PassDefinition pass, PipelineSync sync) =>
        _ = pass.Bind(graph.CreateBufferSrv(_shadowMap.PageTable, structureStride: sizeof(uint)), sync, BufferContentScope.CountBoundedPrefix);

}
