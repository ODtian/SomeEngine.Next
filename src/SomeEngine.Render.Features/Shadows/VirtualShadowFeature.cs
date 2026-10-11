using System.Numerics;
using System.Runtime.InteropServices;
using SomeEngine.Assets;
using SomeEngine.Assets.Schema;
using SomeEngine.Resources;
using SomeEngine.Graphics;
using SomeEngine.Render.Assets;
using SomeEngine.Render.Cluster.Pipeline;
using SomeEngine.Render.Components;
using SomeEngine.Render.Features.Lighting;
using SomeEngine.Render.Lighting;
using SomeEngine.Render.Instances;
using SomeEngine.Render.Shadows;
using SomeEngine.RenderGraph;
using SomeEngine.ECS.Entities;

namespace SomeEngine.Render.Features.Shadows;

/// <summary>
/// Virtual-shadow page processing and generic virtual-depth geometry integration.
/// </summary>
public sealed partial class VirtualShadowFeature : IGraphResourceProvider, IDisposable, IRenderGraphFrameExecutionObserver
{
    private readonly FeatureUniformBuffer _uniformData;
    private readonly GraphBufferInput _initUniforms;
    private readonly GraphBufferInput _invalidationUniforms;
    private readonly GraphBufferInput _coarseDemandUniforms;
    private readonly GraphBufferInput _demandUniforms;
    private readonly GraphBufferInput _poolUniforms;
    private readonly GraphBufferInput _poolPrepareUniforms;
    private readonly GraphBufferInput _poolCompactUniforms;
    private readonly GraphBufferInput _poolFinishUniforms;
    private readonly GraphBufferInput _allocationUniforms;
    private readonly GraphBufferInput _clearUniforms;
    private readonly GraphBufferInput _dynamicResolutionUniforms;
    private readonly GraphBufferInput _pageHiZInitializeUniforms;
    private readonly GraphBufferInput[] _pageHiZReduceUniforms;
    private readonly GraphBufferInput _casterBounds;
    private readonly GraphTextureInput _receiverDepth;

    private const uint DispatchStride = 16u;
    private const uint PageMetadataStride = VirtualShadowPageMetadata.SizeInBytes;
    private uint PageHiZPagesPerRow => Math.Min(_shadowMap.Settings.AtlasSize / _shadowMap.Settings.PageSize, 8u);
    private uint PageHiZBaseLayerCount => DivideRoundUp(_shadowMap.Settings.MaxPhysicalPages, PageHiZPagesPerRow * PageHiZPagesPerRow);
    private uint PageHiZStaticLayerOffset => _shadowMap.Settings.DynamicHiZ ? PageHiZBaseLayerCount : 0u;
    private uint PageHiZLayerCount => PageHiZBaseLayerCount * (_shadowMap.Settings.DynamicHiZ ? 2u : 1u);

    private readonly IGraphicsBackend _backend;
    private readonly Device _device;
    private readonly global::SomeEngine.RenderGraph.RenderGraph _renderGraph;
    private readonly VirtualShadowMap _shadowMap;
    private readonly ClusterPipelineSystem _geometry;
    private readonly LightingFeature _lighting;
    private readonly IndirectCommandLayout _dispatchLayout;
    private RenderView[] _shadowViews = [];
    private VirtualShadowView[] _previousShadowViews = [];
    private float[] _frameBaseShadowBias = [];
    private Matrix4x4[] _previousShadowViewProjections = [];

    private readonly GraphBufferInput _framePageTable;
    private readonly GraphBufferInput _geometryWorkStatus;
    private readonly GraphBufferId _pageOwners;
    private readonly GraphTextureId _atlas;
    private readonly GraphTextureId _pageHiZ;
    private readonly GraphBufferId _pageMetadata;
    private readonly GraphBufferId _pageLists;

    private readonly GraphBufferId _requestedPages;
    private readonly GraphBufferId _requestedPageFlags;
    private readonly GraphBufferId _requestedPageIndices;
    private readonly GraphBufferId _requestedPageCount;
    private readonly GraphTextureId _pageReceiverMasks;
    private uint _requestEpochEntryCapacity;
    private uint _sourceViewCapacity;
    private readonly GraphBufferId _activePages;
    private readonly GraphBufferId _activePageCount;
    private readonly GraphBufferId _uncachedPageRectBounds;
    private readonly GraphBufferId _uncachedPageHierarchy;
    private readonly GraphBufferId _allocatedPageHierarchy;
    private readonly GraphBufferId _allocatedPageRectBounds;
    private readonly GraphBufferId _clusterInvocationDispatchArguments;
    private readonly GraphBufferId _pageWorkDispatchArguments;
    private readonly GraphBufferId _pageRequestDispatchArguments;
    private readonly GraphBufferId _allocationFailures;
    private readonly GraphBufferId _policyRasterWorkCount;
    private readonly GraphBufferId _depthWorkCount;
    private readonly GraphBufferId _dynamicResolutionBias;
    private readonly GraphBufferId _frameResolutionBias;
    private readonly GraphBufferId _rasterInvocationViews;
    private readonly GraphBufferId _rasterTargetViews;
    private readonly GraphBufferInput _clusterSourceViews;

    private readonly GraphPassId _frameResetPass;
    private readonly GraphPassId _receiverMaskResetPass;
    private readonly GraphPassId _requestResetPass;
    private readonly GraphPassId _dynamicResolutionResetPass;
    private readonly GraphPassId _atlasInitializePass;
    private readonly GraphPassId _pageHiZStorageInitializePass;
    private readonly GraphPassId _initializePass;
    private readonly GraphPassId _invalidatePass;
    private readonly GraphPassId _coarseMarkPass;
    private readonly GraphPassId _markPass;
    private readonly GraphPassId _poolPreparePass;
    private readonly GraphPassId _poolCompactPass;
    private readonly GraphPassId _poolFinishPass;
    private readonly GraphPassId _poolPass;
    private readonly GraphPassId _allocatePass;
    private readonly GraphPassId _clearPass;
    private readonly GraphPassId _compactViewsPass;
    private readonly GraphPassId _dynamicResolutionPass;
    private readonly GraphPassId _pageHiZInitializePass;
    private readonly GraphPassId[] _pageHiZReducePasses;
    private readonly GraphPassId _finalPageHiZInitializePass;
    private readonly GraphPassId[] _finalPageHiZReducePasses;
    private readonly GraphPassId _rasterCompletePass;
    private readonly GraphPassId _frameCompletePass;

    private readonly GraphBufferInput _frameShadowViews;
    private readonly GraphRegionId _viewUploadRegion;
    private readonly GraphParameterInput _blueNoiseInput;
    private readonly SomeEngine.Resources.Texture _blueNoise;
    private readonly RenderDevice _textureDevice;
    private readonly GraphBufferInput _frameSamplingUniforms;
    private readonly GraphBufferInput _frameInvocationUniforms;
    private bool _frameHasPreviousHistory;
    private uint _frameIndex;
    private uint _previousFrameIndex;
    private bool _previousForceStateReset;
    private bool _forceStateReset;
    private int _viewCount;
    private bool _shadowFrameActive;
    private bool _frameActive;
    private bool _disposed;
    private RenderInstanceBatch? _committedInstances;
    private RenderInstanceBatch? _frameInstances;
    private ulong _committedInstanceRevision;
    private ulong _committedCasterRevision;
    private ulong _frameCasterRevision;
    private readonly List<uint> _streamingChangedInstances = [];
    private readonly List<uint> _rasterChangedInstances = [];
    private ulong _frameInstanceRevision;
    private readonly List<uint> _changedInstances = [];
    private readonly HashSet<uint> _deformedInstances = [];
    private bool _invalidateAllCasters;
    private readonly GraphBufferInput _frameChangedInstances;

    /// <summary>
    /// Reports changes to externally driven vertex, domain or opacity inputs before the next
    /// frame. IDs are rows in CurrentInstances; the declared expansion must cover both states.
    /// Material slot edits and instance uploads are tracked automatically.
    /// </summary>
    public void InvalidateCasters(ReadOnlySpan<uint> instanceIds)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_frameActive)
            throw new InvalidOperationException("Caster changes must be reported before staging a shadow frame.");
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
        if (_frameActive)
            throw new InvalidOperationException("Caster changes must be reported before staging a shadow frame.");
        _invalidateAllCasters = true;
    }

    public VirtualShadowFeature(
        IGraphicsBackend backend,
        Device device,
        VirtualShadowMapShaders shaders,
        VirtualShadowMapSettings settings,
        global::SomeEngine.RenderGraph.RenderGraph graph,
        ClusterPipelineSystem geometry,
        LightingFeature lighting,
        GraphRegionId primaryDepthAfter,
        GraphRegionId targetGeometryAfter,
        GraphRegionId feedback,
        RenderDevice textureDevice)
    {
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        _device = device ?? throw new ArgumentNullException(nameof(device));
        ArgumentNullException.ThrowIfNull(shaders);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(graph);
        _renderGraph = graph;
        _blueNoise = shaders.BlueNoise ?? throw new ArgumentException(
            "Virtual shadow shaders require a blue-noise texture.", nameof(shaders));
        _textureDevice = textureDevice;
        _qualityRegion = targetGeometryAfter;
        _qualityDemandRegion = primaryDepthAfter;
        _geometry = geometry ?? throw new ArgumentNullException(nameof(geometry));
        _lighting = lighting ?? throw new ArgumentNullException(nameof(lighting));
        if (!ReferenceEquals(graph.Device, device))
            throw new ArgumentException("Virtual shadows and RenderGraph must use the same Device.", nameof(graph));
        settings.Validate();
        VirtualShadowMap? shadowMap = null;
        IndirectCommandLayout? dispatchLayout = null;
        try
        {
            shadowMap = new VirtualShadowMap(backend, device, shaders, settings);
            dispatchLayout = backend.CreateIndirectCommandLayout(
                device,
                new IndirectCommandLayoutDesc(
                    [new IndirectArgumentDesc(IndirectArgumentType.Dispatch)],
                    DispatchStride,
                    label: "Virtual shadow dispatch indirect layout"));
            _shadowMap = shadowMap;
            _dispatchLayout = dispatchLayout;

            RenderGraphEdit edit = graph.BeginEdit();
            try
            {
                _viewUploadRegion = edit.AddFrameRegion("Virtual shadow view upload", primaryDepthAfter);
                _blueNoiseInput = edit.CreateParameterInput(ResourceBindingType.TextureSrv);
                _geometry.ConfigureSurfaceReceivers(edit.AddRegion("Virtual shadow surface receivers", primaryDepthAfter));
                ulong pageBytes = checked((ulong)settings.MaxPhysicalPages * sizeof(uint));
                _pageOwners = edit.DeclareExternalBuffer(new BufferDesc(
                    pageBytes,
                    BufferUsages.ShaderRead | BufferUsages.ShaderWrite | BufferUsages.CopySource | BufferUsages.CopyDestination,
                    "Virtual shadow page owner slot"));
                _atlas = edit.DeclareExternalTexture(new TextureDesc(
                    TextureDimension.Texture2D,
                    settings.AtlasSize,
                    settings.AtlasHeight,
                    1,
                    1,
                    2,
                    1,
                    Format.R32UInt,
                    TextureUsages.Sampled | TextureUsages.Storage | TextureUsages.ColorAttachment,
                    label: "Virtual shadow atlas slot"));
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
                BufferUsages cacheUsages = BufferUsages.ShaderRead | BufferUsages.ShaderWrite |
                    BufferUsages.CopySource | BufferUsages.CopyDestination;
                _pageMetadata = edit.DeclareExternalBuffer(new BufferDesc(
                    checked((ulong)settings.MaxPhysicalPages * PageMetadataStride),
                    cacheUsages,
                    "Virtual shadow page metadata slot"));
                _pageLists = edit.DeclareExternalBuffer(new BufferDesc(
                    VirtualShadowMap.CalculatePageListBufferSize(settings.MaxPhysicalPages),
                    cacheUsages, "Virtual shadow page lists"));

                _activePages = edit.CreateBuffer(new BufferDesc(
                    checked(pageBytes * 4u),
                    BufferUsages.ShaderRead | BufferUsages.ShaderWrite,
                    "Virtual shadow active raster pages"));
                _activePageCount = Counter(edit, "Virtual shadow active page count");
                uint sourceViewCapacity = checked(settings.MaxShadowLights * settings.MaximumViewsPerLight);
                _sourceViewCapacity = sourceViewCapacity;
                uint pageTableEntryCapacity = checked(
                    sourceViewCapacity * settings.PageTableEntriesPerView);
                _requestEpochEntryCapacity = pageTableEntryCapacity;
                uint rasterHierarchyWordCapacity = checked(
                    sourceViewCapacity * settings.PageHierarchyWordsPerView);
                uint rasterViewCapacity = checked(sourceViewCapacity * settings.MipLevelCount);
                _requestedPages = edit.CreatePersistentBuffer(new BufferDesc(
                    checked((ulong)pageTableEntryCapacity * sizeof(uint)),
                    BufferUsages.ShaderRead | BufferUsages.ShaderWrite,
                    "Virtual shadow request epochs"));
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
                _clusterInvocationDispatchArguments = DispatchBuffer(
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
                _rasterInvocationViews = edit.CreateBuffer(new BufferDesc(
                    checked((ulong)rasterViewCapacity * (uint)ClusterRenderContext.ViewSizeInBytes),
                    BufferUsages.ShaderRead | BufferUsages.ShaderWrite,
                    "Virtual shadow raster invocation views"));
                _rasterTargetViews = edit.CreateBuffer(new BufferDesc(
                    checked((ulong)rasterViewCapacity * ClusterRenderContext.TargetViewSizeInBytes),
                    BufferUsages.ShaderRead | BufferUsages.ShaderWrite,
                    "Virtual shadow raster target views"));

                uint resetWords = Math.Max(rasterHierarchyWordCapacity, rasterViewCapacity);
                uint resetGroups = DivideRoundUp(resetWords, 256u);
                _frameResetPass = AddComputePass(ref edit,
                    "Reset virtual shadow frame data",
                    _shadowMap.FrameResetPipeline,
                    new VirtualShadowDispatchPassData(new DispatchArguments(
                        Math.Min(resetGroups, 65535u), DivideRoundUp(resetGroups, 65535u), 1u)), region: primaryDepthAfter);
                PassDefinition frameReset = edit.GetPass(_frameResetPass);
                _ = frameReset.Bind(edit.CreateBufferUav(_activePageCount), PipelineSync.ComputeShading,
                    GraphAccessMode.Write, WriteCoverage.Complete);
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
                             _clusterInvocationDispatchArguments,
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
                var requestReset = new VirtualShadowBufferResetPassData(_requestedPageCount);
                _requestResetPass = edit.AddCopyFramePass(
                    "Reset virtual shadow page request count",
                    PassQueueSelection.AnyOfType(QueueType.Graphics),
                    requestReset,
                    new PassOptions(Culling: PassCullingMode.NeverCull, RepeatCommands: true),
                    VirtualShadowBufferResetPassData.Declare,
                    VirtualShadowBufferResetPassData.Record, region: primaryDepthAfter);
                var dynamicResolutionReset = new VirtualShadowBufferResetPassData(
                    _dynamicResolutionBias);
                _dynamicResolutionResetPass = edit.AddCopyFramePass(
                    "Reset virtual shadow dynamic resolution bias",
                    PassQueueSelection.AnyCompatible(),
                    dynamicResolutionReset,
                    new PassOptions(Culling: PassCullingMode.NeverCull, RepeatCommands: true),
                    VirtualShadowBufferResetPassData.Declare,
                    VirtualShadowBufferResetPassData.Record, region: primaryDepthAfter);

                var resolutionCopy = new VirtualShadowResolutionCopyPassData(_dynamicResolutionBias, _frameResolutionBias);
                GraphPassId resolutionCopyPass = edit.AddCopyFramePass("Copy virtual shadow resolution bias for the frame",
                    PassQueueSelection.AnyCompatible(), resolutionCopy,
                    new PassOptions(Culling: PassCullingMode.NeverCull, RepeatCommands: true),
                    VirtualShadowResolutionCopyPassData.Declare, VirtualShadowResolutionCopyPassData.Record,
                    region: primaryDepthAfter);
                edit.OrderAfter(resolutionCopyPass, _dynamicResolutionResetPass);

                var atlasInitialize = new VirtualShadowAtlasInitializePassData(_atlas, 2);
                _atlasInitializePass = edit.AddCopyFramePass(
                    "Initialize virtual shadow atlas",
                    PassQueueSelection.AnyOfType(QueueType.Graphics),
                    atlasInitialize,
                    new PassOptions(Culling: PassCullingMode.NeverCull, RepeatCommands: true),
                    VirtualShadowAtlasInitializePassData.Declare,
                    VirtualShadowAtlasInitializePassData.Record, region: primaryDepthAfter);
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
                    VirtualShadowPageHiZStorageInitializePassData.Record, region: primaryDepthAfter);

                _initializePass = AddComputePass(
                    ref edit,
                    "Initialize virtual shadow state",
                    _shadowMap.StateInitializePipeline,
                    new VirtualShadowDispatchPassData(default), region: primaryDepthAfter);
                _invalidatePass = AddComputePass(
                    ref edit,
                    "Invalidate moved virtual shadow casters",
                    _shadowMap.CasterInvalidationPipeline,
                    new VirtualShadowDispatchPassData(default), region: primaryDepthAfter);
                _coarseMarkPass = AddComputePass(
                    ref edit,
                    "Mark coarse virtual shadow pages",
                    _shadowMap.CoarsePageMarkPipeline,
                    new VirtualShadowDispatchPassData(default), region: primaryDepthAfter);
                _markPass = AddComputePass(
                    ref edit,
                    "Mark virtual shadow pages",
                    _shadowMap.PageMarkPipeline,
                    new VirtualShadowDispatchPassData(default), region: primaryDepthAfter);

                _poolPreparePass = AddComputePass(
                    ref edit, "Prepare virtual shadow page order",
                    _shadowMap.PagePoolPipeline,
                    new VirtualShadowDispatchPassData(default), region: primaryDepthAfter);
                _poolPass = AddComputePass(
                    ref edit,
                    "Build virtual shadow page pool",
                    _shadowMap.PagePoolPipeline,
                    new VirtualShadowDispatchPassData(default), region: primaryDepthAfter);
                _poolCompactPass = AddComputePass(
                    ref edit, "Compact available virtual shadow pages",
                    _shadowMap.PagePoolPipeline,
                    new VirtualShadowDispatchPassData(new DispatchArguments(1, 1, 1)), region: primaryDepthAfter);
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
                    _pageWorkDispatchArguments, region: _geometry.IntermediateOcclusionRegion);
                _pageHiZReducePasses = new GraphPassId[(pageHiZMipCount - 1) / 4];
                for (int mip = 4; mip < pageHiZMipCount; mip += 4)
                {
                    _pageHiZReducePasses[mip / 4 - 1] = AddIndirectPass(
                        ref edit,
                        $"Reduce virtual shadow physical-page HiZ mip {mip}",
                        _shadowMap.PageHiZReducePipeline,
                        _pageWorkDispatchArguments,
                        checked((ulong)mip * DispatchStride), region: _geometry.IntermediateOcclusionRegion);
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
                _framePageTable = edit.CreateBufferInput(BufferUsages.ShaderRead | BufferUsages.ShaderWrite | BufferUsages.CopySource);
                _geometryWorkStatus = edit.CreateBufferInput(BufferUsages.ShaderRead);
                _rasterCompletePass = AddComputePass(ref edit, "Complete virtual shadow page depth",
                    _shadowMap.PageDepthCompletePipeline,
                    new VirtualShadowDispatchPassData(new DispatchArguments((settings.MaxPhysicalPages + 63u) / 64u, 1u, 1u)),
                    region: targetGeometryAfter);
                PassDefinition depthComplete = edit.GetPass(_rasterCompletePass);
                _ = depthComplete.Bind(edit.CreateBufferSrv(_activePages, structureStride: 16), PipelineSync.ComputeShading,
                    BufferContentScope.CountBoundedPrefix);
                _ = depthComplete.Bind(edit.CreateBufferSrv(_activePageCount, structureStride: 4), PipelineSync.ComputeShading);
                _ = depthComplete.Bind(edit.CreateBufferUav(_framePageTable, structureStride: sizeof(uint)), PipelineSync.ComputeShading,
                    GraphAccessMode.ReadWrite, WriteCoverage.Partial);
                _ = depthComplete.Bind(edit.CreateBufferSrv(_geometryWorkStatus), PipelineSync.ComputeShading);
                _ = depthComplete.Bind(edit.CreateBufferUav(_pageMetadata, structureStride: VirtualShadowPageMetadata.SizeInBytes), PipelineSync.ComputeShading,
                    GraphAccessMode.ReadWrite, WriteCoverage.Partial);
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
                edit.OrderAfter(_coarseMarkPass, _initializePass);
                edit.OrderAfter(_markPass, _initializePass);
                edit.OrderAfter(_coarseMarkPass, _dynamicResolutionResetPass);
                edit.OrderAfter(_poolPreparePass, _coarseMarkPass);
                edit.OrderAfter(_poolPass, _poolPreparePass);
                edit.OrderAfter(_invalidatePass, _poolPass);
                edit.OrderAfter(_poolCompactPass, _poolPass);
                edit.OrderAfter(_allocatePass, _poolCompactPass);
                edit.OrderAfter(_poolFinishPass, _allocatePass);
                edit.OrderAfter(_dynamicResolutionPass, _poolFinishPass);
                edit.OrderAfter(_allocatePass, _invalidatePass);
                edit.OrderAfter(_compactViewsPass, _allocatePass);
                edit.OrderAfter(_clearPass, _compactViewsPass);
                edit.OrderAfter(_rasterCompletePass, _allocatePass);
                edit.OrderAfter(_rasterCompletePass, _finalPageHiZReducePasses.Length == 0
                    ? _finalPageHiZInitializePass : _finalPageHiZReducePasses[^1]);
                edit.OrderAfter(_requestResetPass, _allocatePass);
                edit.OrderAfter(_receiverMaskResetPass, _rasterCompletePass);
                edit.OrderAfter(_dynamicResolutionPass, _rasterCompletePass);
                edit.OrderAfter(_dynamicResolutionPass, resolutionCopyPass);
                edit.OrderAfter(_frameCompletePass, _dynamicResolutionPass);
                edit.OrderAfter(_frameCompletePass, _receiverMaskResetPass);
                edit.OrderAfter(_poolPreparePass, _markPass);
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

                uint rasterWindows = 0u;
                for (uint mip = 0u; mip < settings.MipLevelCount; mip++)
                {
                    uint dimension = (settings.PageTableMipPagesPerRow(mip) + 3u) / 4u;
                    rasterWindows = checked(rasterWindows + dimension * dimension);
                }
                _geometry.ConfigureRenderContext(new ClusterRenderContext(
                    1u,
                    BindRasterPolicyFilterResources,
                    BindRasterInvocationViews,
                    BindClusterSourceViews,
                    BindRasterInvocationCount,
                    _clusterInvocationDispatchArguments,
                    _policyRasterWorkCount,
                    sourceViewCapacity * settings.MipLevelCount,
                    checked(settings.PageSize * 4u),
                    checked(settings.PageSize * 4u),
                    checked((uint)pageHiZMipCount),
                    Math.Max(settings.PageSize / 2u, 1u))
                {
                    BindTargetPageBounds = BindRasterPageBounds,
                    BindDepthCacheClasses = BindRasterDepthCacheClasses,
                    BindWorkTargetViews = BindRasterTargetViews,
                    DepthWorkCounter = _depthWorkCount,
                    ViewDetailCount = settings.MipLevelCount,
                    MaximumRasterWindowsPerSourceView = rasterWindows,
                    InvocationCountIndex = 3u,
                });
                _clusterSourceViews = edit.CreateBufferInput(BufferUsages.ShaderRead);
                _frameShadowViews = edit.CreateBufferInput(BufferUsages.ShaderRead | BufferUsages.ShaderWrite);
                _frameChangedInstances = edit.CreateBufferInput(BufferUsages.ShaderRead);
                _frameDepthCacheClasses = edit.CreateBufferInput(BufferUsages.ShaderRead);
                _framePreviousDepthCacheClasses = edit.CreateBufferInput(BufferUsages.ShaderRead);
                _frameLodBounds = edit.CreateBufferInput(BufferUsages.ShaderRead);
                _frameSamplingUniforms = edit.CreateBufferInput(BufferUsages.Constant);
                _frameInvocationUniforms = edit.CreateBufferInput(BufferUsages.Constant);
                _initUniforms = edit.CreateBufferInput(BufferUsages.Constant);
                _invalidationUniforms = edit.CreateBufferInput(BufferUsages.Constant);
                _coarseDemandUniforms = edit.CreateBufferInput(BufferUsages.Constant);
                _demandUniforms = edit.CreateBufferInput(BufferUsages.Constant);
                _poolUniforms = edit.CreateBufferInput(BufferUsages.Constant);
                _poolPrepareUniforms = edit.CreateBufferInput(BufferUsages.Constant);
                _poolCompactUniforms = edit.CreateBufferInput(BufferUsages.Constant);
                _poolFinishUniforms = edit.CreateBufferInput(BufferUsages.Constant);
                _allocationUniforms = edit.CreateBufferInput(BufferUsages.Constant);
                _clearUniforms = edit.CreateBufferInput(BufferUsages.Constant);
                _dynamicResolutionUniforms = edit.CreateBufferInput(BufferUsages.Constant);
                _pageHiZInitializeUniforms = edit.CreateBufferInput(BufferUsages.Constant);
                _casterBounds = edit.CreateBufferInput(BufferUsages.ShaderRead);
                _receiverDepth = edit.CreateTextureInput();
                _surfaceReceivers = edit.CreateBufferInput(BufferUsages.ShaderRead);
                _additionalReceivers = edit.CreateBufferInput(BufferUsages.ShaderRead);
                _pageHiZReduceUniforms = new GraphBufferInput[_pageHiZReducePasses.Length];
                BindReceiverMaskReset(ref edit);
                BindInitialize(ref edit, _initUniforms);
                BindInvalidation(ref edit, _invalidationUniforms);
                BindCoarseMark(ref edit, _coarseDemandUniforms);
                BindMark(ref edit, _demandUniforms);
                InstallFroxels(ref edit, shaders, primaryDepthAfter);
                BindPool(ref edit, _poolPass, _poolUniforms);
                BindPool(ref edit, _poolPreparePass, _poolPrepareUniforms);
                BindPool(ref edit, _poolCompactPass, _poolCompactUniforms);
                BindPool(ref edit, _poolFinishPass, _poolFinishUniforms);
                BindAllocation(ref edit, _allocatePass, _allocationUniforms);
                InstallStaticDepth(ref edit, shaders, primaryDepthAfter, targetGeometryAfter);
                InstallResidentPages(ref edit, shaders, pageTableEntryCapacity, targetGeometryAfter);
                InstallPrefilter(ref edit, shaders, pageTableEntryCapacity, primaryDepthAfter, targetGeometryAfter);
                InstallProjection(ref edit, shaders, targetGeometryAfter);
                BindClear(ref edit, _clearUniforms);
                BindDynamicResolution(ref edit, _dynamicResolutionUniforms);
                BindCompact(ref edit, _frameInvocationUniforms, _clusterSourceViews);
                BindPageHiZInitialize(ref edit, _pageHiZInitializePass, _pageHiZInitializeUniforms);
                BindPageHiZInitialize(ref edit, _finalPageHiZInitializePass, _pageHiZInitializeUniforms);
                for (int index = 0; index < _pageHiZReducePasses.Length; index++)
                {
                    uint mip = checked((uint)(index + 1) * 4u);
                    GraphBufferInput input = edit.CreateBufferInput(BufferUsages.Constant);
                    _pageHiZReduceUniforms[index] = input;
                    BindPageHiZReduce(ref edit, _pageHiZReducePasses[index], input, mip - 1, mip);
                    BindPageHiZReduce(ref edit, _finalPageHiZReducePasses[index], input, mip - 1, mip);
                }
                InstallWorkFeedback(ref edit, shaders, rasterViewCapacity / settings.MipLevelCount,
                    primaryDepthAfter, feedback, resolutionCopyPass);
                InstallWorkTime(ref edit, primaryDepthAfter, targetGeometryAfter, feedback);
                if (_prefilterPipeline is not null && _workTimeQueries is not null)
                    edit.OrderAfter(_prefilterPasses[0], _workTimeEndPass);
                InstallDistantBias(ref edit, feedback);
                _uniformData = new FeatureUniformBuffer(ref edit,
                    [_frameSamplingUniforms, _frameInvocationUniforms, _initUniforms, _invalidationUniforms,
                     _coarseDemandUniforms, _demandUniforms, _poolUniforms,
                     _poolPrepareUniforms, _poolCompactUniforms, _poolFinishUniforms, _allocationUniforms,
                     _clearUniforms, _dynamicResolutionUniforms, _pageHiZInitializeUniforms, _workUniforms, _residentUniforms, _directionalProjection.Uniforms, _localProjection.Uniforms, .. _prefilterUniforms, .. _pageHiZReduceUniforms],
                    device.Capabilities.Limits.ConstantBufferAlignment, "Virtual shadow constant data");
                PassDefinition frameComplete = edit.GetPass(_frameCompletePass);
                _ = frameComplete.Read(
                    _framePageTable,
                    BufferRange.Whole,
                    PipelineSync.AllShading,
                    ResourceAccess.ShaderResource);
                _ = frameComplete.Read(
                    _atlas,
                    new TextureSubresourceRange(0, 1, 0, 1, TextureAspects.Color),
                    PipelineSync.AllShading,
                    ResourceAccess.ShaderResource,
                    TextureLayout.ShaderResource);
                edit.Commit();
            }
            finally
            {
                edit.Dispose();
            }
        }
        catch
        {
            dispatchLayout?.Dispose();
            _directionalProjection?.Pipeline.Dispose();
            _localProjection?.Pipeline.Dispose();
            _residentPipeline?.Dispose();
            _prefilterPipeline?.Dispose();
            _prefilterRequestPipeline?.Dispose();
            _prefilterResetPipeline?.Dispose();
            _prefilterSelectPipeline?.Dispose();
            _staticMergePipeline?.Dispose();
            _workFeedbackPipeline?.Dispose();
            DisposeFroxelPipelines();
            _workTimeQueries?.Dispose();
            shadowMap?.Dispose();
            throw;
        }
    }

    public VirtualShadowMap ShadowMap => _shadowMap;

    public int ShadowViewCount => _viewCount;

    private uint ActivePageTableEntryCount => checked(
        _shadowMap.PageTableEntryCount);

    private void BindRasterInvocationViews(
        ref RenderGraphEdit graph,
        ref PassDefinition pass, PipelineSync sync)
    {
        _ = pass.Bind(
            graph.CreateBufferSrv(
                _rasterInvocationViews,
                structureStride: (uint)ClusterRenderContext.ViewSizeInBytes,
                label: "Virtual shadow raster invocation views"),
            sync,
            BufferContentScope.CountBoundedPrefix);
    }

    private void BindClusterSourceViews(
        ref RenderGraphFrame graph,
        GraphBufferSlice sourceViews)
    {
        graph.SetBufferInput(_clusterSourceViews, sourceViews);
    }

    private void BindRasterTargetViews(
        ref RenderGraphEdit graph,
        ref PassDefinition pass, PipelineSync sync)
    {
        _ = pass.Bind(
            graph.CreateBufferSrv(
                _rasterTargetViews,
                structureStride: ClusterRenderContext.TargetViewSizeInBytes,
                label: "Virtual shadow raster target views"),
            sync,
            BufferContentScope.CountBoundedPrefix);
    }

    private void BindRasterPageHierarchy(
        ref RenderGraphEdit graph,
        ref PassDefinition pass, PipelineSync sync) =>
        _ = pass.Bind(
            graph.CreateBufferSrv(
                _uncachedPageHierarchy,
                structureStride: 2u * sizeof(uint),
                label: "Virtual shadow uncached page hierarchy"),
            sync);

    private void BindRasterPageTable(
        ref RenderGraphEdit graph,
        ref PassDefinition pass, PipelineSync sync) =>
        _ = pass.Bind(
            graph.CreateBufferSrv(
                _framePageTable,
                structureStride: VirtualShadowView.PageTableEntrySizeInBytes,
                label: "Virtual shadow raster page table"),
            sync);

    private void BindRasterReceiverMasks(
        ref RenderGraphEdit graph,
        ref PassDefinition pass, PipelineSync sync) =>
        _ = pass.Bind(
            graph.CreateTextureSrv(_pageReceiverMasks, format: Format.R32UInt,
                dimension: SomeEngine.Graphics.TextureViewDimension.Texture2D,
                label: "Virtual shadow raster receiver masks"),
            sync);

    private void BindRasterPolicyFilterResources(
        ref RenderGraphEdit graph,
        ref PassDefinition pass, PipelineSync sync)
    {
        BindRasterPageBounds(ref graph, ref pass, sync);
        _ = pass.Bind(graph.CreateBufferSrv(_pageMetadata, structureStride: VirtualShadowPageMetadata.SizeInBytes,
            label: "Virtual shadow raster-target HiZ page metadata"), sync);
        // Main traversal consumes prior depth before the intermediate region overwrites
        // dirty tiles. Post traversal consumes that current depth; final depth persists.
        BindRasterHiZ(ref graph, ref pass, sync);
        BindRasterHiZ(ref graph, ref pass, sync);
    }

    private void BindRasterHiZ(ref RenderGraphEdit graph, ref PassDefinition pass, PipelineSync sync) =>
        _ = pass.Bind(graph.CreateTextureSrv(_pageHiZ, format: Format.R32Float,
            dimension: SomeEngine.Graphics.TextureViewDimension.Texture2DArray,
            label: "Virtual shadow raster-target HiZ"), sync);

    private void BindRasterPageMetadata(ref RenderGraphEdit graph, ref PassDefinition pass, PipelineSync sync) =>
        _ = pass.Bind(graph.CreateBufferSrv(_pageMetadata, structureStride: VirtualShadowPageMetadata.SizeInBytes), sync);

    private void BindRasterPageBounds(ref RenderGraphEdit graph, ref PassDefinition pass, PipelineSync sync)
    {
        BindRasterTargetViews(ref graph, ref pass, sync);
        BindRasterPageHierarchy(ref graph, ref pass, sync);
        BindRasterPageTable(ref graph, ref pass, sync);
        BindRasterReceiverMasks(ref graph, ref pass, sync);
        BindRasterDepthCacheClasses(ref graph, ref pass, sync);
    }

    private void BindPageDepthAtlas(
        ref RenderGraphEdit graph,
        ref PassDefinition pass,
        PipelineSync sync)
    {
        _ = pass.Bind(
            graph.CreateTextureUav(
                _atlas,
                format: Format.R32UInt,
                dimension: SomeEngine.Graphics.TextureViewDimension.Texture2DArray,
                label: "Virtual shadow raster depth atlas"),
            sync,
            GraphAccessMode.ReadWrite,
            WriteCoverage.Partial,
            unorderedAccessHazards: UnorderedAccessHazardMode.AtomicMergeable);
    }

    public void StageFrame(
        ref RenderGraphFrame graph,
        RenderLightSet lights,
        in RenderView receiverView,
        Entity receiver = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(lights);
        if (_qualityReadbacks is not null)
        {
            foreach (GraphReadbackId readback in _qualityReadbacks.Readbacks) graph.SetReadbackEnabled(readback, false);
            graph.SetPassEnabled(_qualityReceiverCopyPass, false);
        }
        if (_frameActive)
            throw new InvalidOperationException("The previous virtual-shadow frame is still active.");

        _previousFrameIndex = _frameIndex;
        _previousForceStateReset = _forceStateReset;
        int instanceCount = _geometry.CurrentInstanceCount;
        _frameInstances = _geometry.CurrentInstances;
        _frameInstanceRevision = _frameInstances!.ContentRevision;
        _forceStateReset |= !ReferenceEquals(_frameInstances, _committedInstances) || _invalidateAllCasters;
        _changedInstances.Clear();
        _streamingChangedInstances.Clear();
        _rasterChangedInstances.Clear();
        _frameInstances.CollectChangedInstances(_committedInstanceRevision, _changedInstances);
        _forceStateReset |= !_geometry.CollectChangedCasters(_committedCasterRevision,
            _rasterChangedInstances, _streamingChangedInstances, out _frameCasterRevision);
        _changedInstances.AddRange(_rasterChangedInstances);
        _rasterChangedInstances.AddRange(_deformedInstances);
        _rasterChangedInstances.Sort();
        // Batch changes already arrive in increasing row order. Raster and
        // deformation notices can add unordered or duplicate rows.
        if (_rasterChangedInstances.Count != 0)
        {
            _changedInstances.AddRange(_deformedInstances);
            _changedInstances.Sort();
            int unique = 0;
            for (int index = 0; index < _changedInstances.Count; index++)
                if (unique == 0 || _changedInstances[index] != _changedInstances[unique - 1])
                    _changedInstances[unique++] = _changedInstances[index];
            _changedInstances.RemoveRange(unique, _changedInstances.Count - unique);
        }
        UpdateCasterCacheClasses(ref graph, instanceCount);
        int immediateCount = _changedInstances.Count;
        _streamingChangedInstances.Sort();
        int streamingCount = 0;
        for (int index = 0; index < _streamingChangedInstances.Count; index++)
        {
            uint row = _streamingChangedInstances[index];
            if (_changedInstances.BinarySearch(0, immediateCount, row, null) >= 0 ||
                streamingCount != 0 && row == _streamingChangedInstances[streamingCount - 1]) continue;
            _streamingChangedInstances[streamingCount++] = row;
        }
        _streamingChangedInstances.RemoveRange(streamingCount, _streamingChangedInstances.Count - streamingCount);
        if (_rasterChangedInstances.Count != 0)
            for (int index = 0; index < immediateCount; index++)
                if (_rasterChangedInstances.BinarySearch(_changedInstances[index]) >= 0)
                    _changedInstances[index] |= 0x40000000u;
        _changedInstances.AddRange(_streamingChangedInstances);
        for (uint index = 0; index < _lodCasterBounds.Count / 2; index++)
            _changedInstances.Add(0x80000000u | index);
        _forceStateReset |= _changedInstances.Count != 0 && !_geometry.HasFiniteGeometryBounds;
        _shadowMap.BeginFrame();
        _shadowFrameActive = true;
        try
        {
            graph.SetParameterInput(_blueNoiseInput,
                ResourceBinding.SampledTexture(_textureDevice.PrepareTextureSrv(ref graph, _blueNoiseInput, _blueNoise)));
            ReadOnlySpan<VirtualShadowView> views = _shadowMap.PrepareViews(
                lights,
                receiverView,
                0,
                0,
                instanceCount, _geometry.HasFirstPersonWorldMeshes(), ReceiverMaskColumns);
            _viewCount = views.Length;
            _frameBaseShadowBias = new float[views.Length];
            for (int index = 0; index < views.Length; index++) _frameBaseShadowBias[index] = views[index].ResolutionLodBias;
            VirtualShadowMapFrameResources resources =
                _shadowMap.AcquireFrameResources();
            graph.SetBufferInput(_framePageTable, new GraphBufferSlice(graph.Import(
                resources.PageTable.Resource,
                resources.PageTable.BoundaryStates.Span), BufferRange.Whole));
            BindExternalResources(ref graph, in resources);
            if (_shadowViews.Length != _viewCount)
            {
                _shadowViews = new RenderView[_viewCount];
                _previousShadowViews = new VirtualShadowView[_viewCount];
                _previousShadowViewProjections = new Matrix4x4[_viewCount];
            }

            if (_frameIndex == uint.MaxValue)
                _forceStateReset = true;
            _frameIndex = _frameIndex == uint.MaxValue ? 1u : _frameIndex + 1u;

            bool hasPreviousTargetHistory =
                !resources.RequiresInitialization &&
                !_shadowMap.RequiresCacheReset &&
                !_forceStateReset &&
                _shadowMap.TryCopyCommittedViews(
                    _previousShadowViews,
                    _previousShadowViewProjections);
            _frameHasPreviousHistory = hasPreviousTargetHistory;

            ClusterPipelineOutputs geometry = _geometry.Outputs;
            graph.SetBufferInput(_geometryWorkStatus, new GraphBufferSlice(geometry.Geometry.WorkStatus, BufferRange.Whole));
            graph.SetBufferInput(_casterBounds, new GraphBufferSlice(geometry.Geometry.InstanceWorldBounds, BufferRange.Whole));
            graph.SetTextureInput(_receiverDepth, geometry.Depth);
            var receiverData = new GraphBufferSlice(geometry.SurfaceReceivers, BufferRange.Whole);
            graph.SetBufferInput(_surfaceReceivers, receiverData);
            _frameAdditionalReceiverCount = _additionalReceiverCount != 0u ? _additionalReceiverCount : geometry.AdditionalReceiverLayerCount;
            graph.SetBufferInput(_additionalReceivers, _additionalReceiverCount != 0u ? _additionalReceiverData :
                new GraphBufferSlice(geometry.AdditionalSurfaceReceivers, BufferRange.Whole));

            GraphBufferSlice viewUpload = graph.Upload(
                views.IsEmpty ? [default(VirtualShadowView)] : views,
                BufferUsages.CopySource,
                "Virtual shadow view upload");
            GraphBufferId viewBuffer = graph.CreateBuffer(new BufferDesc(viewUpload.Range.Size,
                BufferUsages.ShaderRead | BufferUsages.ShaderWrite |
                    BufferUsages.CopySource | BufferUsages.CopyDestination,
                "Virtual shadow view parameters"));
            var viewCopy = new VirtualShadowViewCopyPassData(viewUpload, viewBuffer);
            graph.AddCopyPass("Upload virtual shadow view parameters", PassQueueSelection.AnyOfType(QueueType.Graphics),
                viewCopy, default, VirtualShadowViewCopyPassData.Declare, VirtualShadowViewCopyPassData.Record,
                _viewUploadRegion);
            graph.SetBufferInput(_frameShadowViews, new GraphBufferSlice(viewBuffer, BufferRange.Whole));
            graph.SetBufferInput(_frameChangedInstances, graph.Upload<uint>(
                _changedInstances.Count != 0 ? CollectionsMarshal.AsSpan(_changedInstances) : [0u],
                BufferUsages.ShaderRead,
                "Virtual shadow changed caster indices"));
            VirtualShadowReceiverProjection receiverProjection = CalculateReceiverProjection(receiverView);
            var sampling = new VirtualShadowSamplingUniforms
            {
                ViewCount = checked((uint)_viewCount),
                DirectionalLightCount = _shadowMap.PendingDirectionalLightCount,
                DirectionalClipmapLevels = _shadowMap.DirectionalClipmapLevelCount,
                PointLightCount = _shadowMap.PendingPointLightCount,
                PointLightBufferOffset = checked((uint)lights.Directional.Count),
                PointViewOffset = _shadowMap.PendingPointViewOffset,
                SpotLightCount = _shadowMap.PendingSpotLightCount,
                SpotLightBufferOffset = checked((uint)(
                    lights.Directional.Count + lights.Points.Count)),
                SpotViewOffset = _shadowMap.PendingSpotViewOffset,
                BlockerSampleCount = _shadowMap.Settings.BlockerSampleCount,
                FilterSampleCount = _shadowMap.Settings.FilterSampleCount,
                BlockerSearchRadiusTexels = _shadowMap.Settings.BlockerSearchRadiusTexels,
                MaxFilterRadiusTexels = _shadowMap.Settings.MaxFilterRadiusTexels,
                CullBackfacingPixels = _shadowMap.Settings.CullBackfacingPixels ? 1u : 0u,
                ScreenRayLength = _shadowMap.Settings.ScreenRayLength,
                NormalBias = _shadowMap.Settings.NormalBias,
                FrameIndex = _frameIndex,
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
                FirstPersonViewOffset = _shadowMap.FirstPersonViewOffset,
            };
            _uniformData.Write(ref graph, _frameSamplingUniforms, in sampling);
            StageProjection(ref graph, in geometry,
                sampling.DirectionalLightCount, sampling.PointLightCount + sampling.SpotLightCount);
            _geometry.StageViews([receiverView], identities: [receiver]);
            int copied = _shadowMap.CopyPreparedRenderViews(_shadowViews);
            if (copied != 0)
                _geometry.StageViews(
                    _shadowViews.AsSpan(0, copied),
                    policy: 1u,
                    previousViewProjections: hasPreviousTargetHistory
                        ? _previousShadowViewProjections.AsSpan(0, copied)
                        : default);

            UpdatePassData(
                ref graph,
                lights,
                in receiverView,
                in resources,
                instanceCount,
                checked((uint)_viewCount),
                in receiverProjection);
            UpdateFroxels(ref graph, in receiverView, in geometry);
            StageWorkFeedback(ref graph, lights, views);
            StagePrefilter(ref graph, views);
            StageDistantBias(ref graph);
            _uniformData.Upload(ref graph);
            graph.ObserveExecution(this);
            _frameActive = true;
        }
        catch
        {
            if (_shadowFrameActive)
            {
                if (_shadowMap.Discard() is { } unusedTable) graph.RetireAfterFrame(unusedTable);
                _shadowFrameActive = false;
            }
            _viewCount = 0;
            _workTimeFrames.Remove(_frameIndex);
            _distantBiasFrames.Remove(_frameIndex);

            _frameHasPreviousHistory = false;
            _frameIndex = _previousFrameIndex;
            _forceStateReset = _previousForceStateReset;
            throw;
        }
    }

    void IRenderGraphFrameExecutionObserver.OnRenderGraphFrameFinished(RenderGraphFrameResult result)
    {
        if (!_frameActive || _disposed) return;
        bool complete = result.TryGetPassCompletion(_frameCompletePass, out _);
        if (_shadowFrameActive && _shadowMap.FinishFrame(result, complete) is { } retiredTable)
            _renderGraph.RetireAfterSubmittedFrames(retiredTable);
        if (!complete)
        {
            _workTimeFrames.Remove(_frameIndex);
            _distantBiasFrames.Remove(_frameIndex);
            _qualityReadbacks?.Requests.Remove(_frameIndex);
            if (!result.SubmittedCompletions.IsEmpty) _forceStateReset = true;
            else
            {
                _frameIndex = _previousFrameIndex;
                _forceStateReset = _previousForceStateReset;
            }
            ClearFrame();
            return;
        }
        CommitWorkTime();
        CommitWorkFeedback();
        CommitPrefilter();
        CommitDistantBias();
        CommitCasterCacheClasses();
        _committedInstances = _frameInstances;
        _committedInstanceRevision = _frameInstanceRevision;
        _committedCasterRevision = _frameCasterRevision;
        _deformedInstances.Clear();
        _invalidateAllCasters = false;
        _forceStateReset = false;
        ClearFrame();
    }

    public bool TryResolve(
        string name,
        ResourceBindingType type,
        out GraphResourceBinder binder)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        binder = name switch
        {
            "RasterTargetViews" when type == ResourceBindingType.BufferSrv => BindRasterTargetViews,
            "RasterTargetDepthCacheClasses" when type == ResourceBindingType.BufferSrv => BindRasterDepthCacheClasses,
            "RasterTargetPageHierarchy" when type == ResourceBindingType.BufferSrv => BindRasterPageHierarchy,
            "RasterTargetPageTable" when type == ResourceBindingType.BufferSrv => BindRasterPageTable,
            "RasterTargetReceiverMasks" when type == ResourceBindingType.TextureSrv => BindRasterReceiverMasks,
            "RasterTargetPageMetadata" when type == ResourceBindingType.BufferSrv => BindRasterPageMetadata,
            "RasterTargetCurrentHiZ" or "RasterTargetPreviousHiZ" when type == ResourceBindingType.TextureSrv => BindRasterHiZ,
            "RasterTargetOutput" when type == ResourceBindingType.TextureUav => BindPageDepthAtlas,
            "VirtualShadowViews" when type == ResourceBindingType.BufferSrv => BindShadeViews,
            "VirtualShadowProjectedDirectionalVisibility" when type == ResourceBindingType.BufferSrv => BindProjectedDirectionalVisibility,
            "VirtualShadowProjectedLocalVisibility" when type == ResourceBindingType.BufferSrv => BindProjectedLocalVisibility,
            "VirtualShadowAdditionalProjectedDirectionalVisibility" when type == ResourceBindingType.BufferSrv => BindAdditionalProjectedDirectionalVisibility,
            "VirtualShadowAdditionalProjectedLocalVisibility" when type == ResourceBindingType.BufferSrv => BindAdditionalProjectedLocalVisibility,
            "VirtualShadowResidentPages" when type == ResourceBindingType.BufferSrv => BindResidentPages,
            "VirtualShadowPrefilteredAtlas" when type == ResourceBindingType.TextureSrv => BindPrefilteredAtlas,
            "VirtualShadowPageTable" when type == ResourceBindingType.BufferSrv => BindShadePageTable,
            "VirtualShadowReceiverMasks" when type == ResourceBindingType.TextureSrv => BindRasterReceiverMasks,
            "VirtualShadowAtlas" when type == ResourceBindingType.TextureSrv => BindShadeAtlas,
            "VirtualShadowBlueNoise" when type == ResourceBindingType.TextureSrv => BindShadeBlueNoise,
            "VirtualShadowSampling" when type == ResourceBindingType.ConstantBuffer => BindShadeSampling,
            "VirtualShadowDynamicResolutionBias" when type == ResourceBindingType.BufferSrv => BindShadeDynamicResolutionBias,
            _ => null!,
        };
        return binder is not null;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        if (_frameActive || _shadowFrameActive)
            throw new InvalidOperationException("Finish or cancel the active graph frame before disposing virtual shadows.");
        _qualityReadbacks?.Requests.Clear();
        _dispatchLayout.Dispose();
        _residentPipeline?.Dispose();
        _directionalProjection.Pipeline.Dispose();
        _localProjection.Pipeline.Dispose();
        _prefilterPipeline?.Dispose();
        _prefilterRequestPipeline?.Dispose();
        _prefilterResetPipeline?.Dispose();
        _prefilterSelectPipeline?.Dispose();
        _staticMergePipeline?.Dispose();
        _workFeedbackPipeline?.Dispose();
        DisposeFroxelPipelines();
        if (_workTimeQueries is { } queries) _renderGraph.RetireAfterSubmittedFrames(queries);
        _shadowMap.Dispose();
        Array.Clear(_shadowViews);

        _frameHasPreviousHistory = false;
        _viewCount = 0;
        _frameActive = false;
        _shadowFrameActive = false;
        _disposed = true;
    }

    private static VirtualShadowReceiverProjection CalculateReceiverProjection(in RenderView view)
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

    private void UpdatePassData(
        ref RenderGraphFrame graph,
        RenderLightSet lights,
        in RenderView receiverView,
        in VirtualShadowMapFrameResources resources,
        int instanceCount,
        uint viewCount,
        in VirtualShadowReceiverProjection receiverProjection)
    {
        uint maxPages = _shadowMap.Settings.MaxPhysicalPages;
        uint tableEntries = _shadowMap.PageTableEntryCount;
        uint pageGroups = DivideRoundUp(maxPages, 64u);
        uint initGroups = DivideRoundUp(
            Math.Max(Math.Max(maxPages, tableEntries), Math.Max(_requestEpochEntryCapacity, ReceiverMaskWidth * ReceiverMaskHeight)),
            64u);
        bool resetState = resources.RequiresInitialization || _shadowMap.RequiresCacheReset || _forceStateReset;

        var initUniforms = new VirtualShadowStateInitUniforms
        {
            PageTableEntryCount = Math.Max(1u, tableEntries),
            MaxPhysicalPages = maxPages,
            RequestEpochEntryCount = _requestEpochEntryCapacity,
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
            ChangedInstanceCount = checked((uint)_changedInstances.Count),
            DeferredChangedInstanceCount = checked((uint)(_streamingChangedInstances.Count + _lodCasterBounds.Count / 2)),
            ViewCount = viewCount,
            PageTableEntryCount = tableEntries,
            PageDilation = checked((uint)Math.Ceiling(
                Math.Max(
                    _shadowMap.Settings.BlockerSearchRadiusTexels,
                    Math.Max(_shadowMap.Settings.MaxFilterRadiusTexels, _shadowMap.Settings.PrefilteredDistantEnabled ? 2f : 0f)) /
                _shadowMap.Settings.PageSize)),
            FrameIndex = _frameIndex,
            HierarchyWordsPerView = _shadowMap.Settings.PageHierarchyWordsPerView,
            MipLevelCount = _shadowMap.Settings.MipLevelCount,
        };
        _uniformData.Write(ref graph, _invalidationUniforms, in invalidationUniforms);
        graph.SetPassDataIfChanged(
            _invalidatePass,
            new VirtualShadowDispatchPassData(new DispatchArguments(
                Math.Min(checked((uint)_changedInstances.Count), 65535u),
                DivideRoundUp(checked((uint)_changedInstances.Count), 65535u),
                1u)));
        // The persistent pass uses its dispatch count to express caster changes.
        // Newly initialized page tables already contain empty mappings.
        graph.SetPassEnabled(
            _invalidatePass,
            viewCount != 0u);

        var coarseDemand = new VirtualShadowCoarsePageDemandUniforms
        {
            ViewCount = viewCount,
            PageTableEntryCount = tableEntries,
            FrameIndex = _frameIndex,
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
            MaxPhysicalPages = _shadowMap.Settings.MaxPhysicalPages,
            PagePoolLoadStart = _shadowMap.Settings.DynamicResolutionPagePoolLoadStart,
            MaxLodBias = _shadowMap.Settings.DynamicResolutionMaxLodBias,
            Enabled = _shadowMap.Settings.DynamicResolutionEnabled && viewCount != 0u ? 1u : 0u,
        };
        _uniformData.Write(ref graph, _dynamicResolutionUniforms, in dynamicResolution);
        graph.SetPassEnabled(_dynamicResolutionPass, viewCount != 0u);

        if (!Matrix4x4.Invert(receiverView.View * receiverView.Projection, out Matrix4x4 clipToWorld))
            throw new ArgumentException("The receiver projection must be invertible.", nameof(receiverView));
        var demand = new VirtualShadowPageDemandUniforms
        {
            ClipToWorld = clipToWorld,
            ReceiverWidth = receiverView.ViewportWidth,
            ReceiverHeight = receiverView.ViewportHeight,
            ViewCount = viewCount,
            PageTableEntryCount = tableEntries,
            DirectionalLightCount = _shadowMap.PendingDirectionalLightCount,
            DirectionalClipmapLevels = _shadowMap.DirectionalClipmapLevelCount,
            PointLightCount = _shadowMap.PendingPointLightCount,
            PointLightBufferOffset = checked((uint)lights.Directional.Count),
            PointViewOffset = _shadowMap.PendingPointViewOffset,
            SpotLightCount = _shadowMap.PendingSpotLightCount,
            SpotLightBufferOffset = checked((uint)(
                lights.Directional.Count + lights.Points.Count)),
            SpotViewOffset = _shadowMap.PendingSpotViewOffset,
            ReceiverViewIndex = 0u,
            AdditionalReceiverLayerCount = _frameAdditionalReceiverCount,
            CullBackfacingPixels = _shadowMap.Settings.CullBackfacingPixels ? 1u : 0u,
            FirstPersonPixelRequestBias = _shadowMap.Settings.FirstPersonPixelRequestBias,
            FirstPersonPixelRequestMinLevel = _shadowMap.Settings.FirstPersonPixelRequestMinLevel,
            FrameIndex = _frameIndex,
            PageDilationBorderPages = (float)(
                Math.Max(
                    _shadowMap.Settings.BlockerSearchRadiusTexels,
                    Math.Max(_shadowMap.Settings.MaxFilterRadiusTexels, _shadowMap.Settings.PrefilteredDistantEnabled ? 2f : 0f)) /
                _shadowMap.Settings.PageSize),
            NormalBias = _shadowMap.Settings.NormalBias,
            ReceiverProjection = receiverProjection,
        };
        _uniformData.Write(ref graph, _demandUniforms, in demand);
        graph.SetPassDataIfChanged(
            _markPass,
            new VirtualShadowDispatchPassData(new DispatchArguments(
                DivideRoundUp(receiverView.ViewportWidth, 16u),
                DivideRoundUp(receiverView.ViewportHeight, 16u),
                1)));
        graph.SetPassEnabled(_markPass, viewCount != 0u);

        UploadPoolUniforms(ref graph, _poolPrepareUniforms, 0u);
        UploadPoolUniforms(ref graph, _poolUniforms, 1u);
        UploadPoolUniforms(ref graph, _poolCompactUniforms, 2u);
        UploadPoolUniforms(ref graph, _poolFinishUniforms, 3u);
        var pageOrderDispatch = new VirtualShadowDispatchPassData(new DispatchArguments(
            DivideRoundUp(_shadowMap.Settings.MaxPhysicalPages, 256u), 1, 1));
        graph.SetPassDataIfChanged(_poolPass, pageOrderDispatch);
        graph.SetPassDataIfChanged(_poolPreparePass, pageOrderDispatch);
        graph.SetPassEnabled(_poolPass, viewCount != 0u);
        graph.SetPassEnabled(_poolPreparePass, viewCount != 0u);
        graph.SetPassEnabled(_poolCompactPass, viewCount != 0u);
        graph.SetPassEnabled(_poolFinishPass, viewCount != 0u);

        UploadAllocationUniforms(ref graph);
        graph.SetPassEnabled(_allocatePass, viewCount != 0u);

        var clearUniforms = new VirtualShadowPageClearUniforms
        {
            PageSize = _shadowMap.Settings.PageSize,
            PhysicalPagesPerRow = _shadowMap.Settings.PhysicalPagesPerRow,
            AtlasSize = _shadowMap.Settings.AtlasSize,
        };
        _uniformData.Write(ref graph, _clearUniforms, in clearUniforms);
        graph.SetPassEnabled(_clearPass, viewCount != 0u);

        var pageHiZInitializeUniforms = new VirtualShadowPageHiZUniforms
        {
            PageSize = _shadowMap.Settings.PageSize,
            PhysicalPagesPerRow = _shadowMap.Settings.PhysicalPagesPerRow,
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
                PhysicalPagesPerRow = _shadowMap.Settings.PhysicalPagesPerRow,
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
            ActivePageCapacity = _shadowMap.Settings.MaxPhysicalPages,
            PageSize = _shadowMap.Settings.PageSize,
            PageHiZMipCount = checked((uint)(
                BitOperations.Log2(Math.Max(_shadowMap.Settings.PageSize / 2u, 1u)) + 1)),
            PhysicalPagesPerRow = _shadowMap.Settings.PhysicalPagesPerRow,
            InstanceGroupCount = DivideRoundUp(checked((uint)instanceCount), 64u),
            HasPreviousHistory = _frameHasPreviousHistory ? 1u : 0u,
            StaticHiZLayerOffset = PageHiZStaticLayerOffset,
            WorkFeedbackEnabled = _qualityReadbacks is not null ? 3u : (_shadowMap.Settings.WorkThrottlingEnabled ? 1u : 0u),
        };
        _uniformData.Write(ref graph, _frameInvocationUniforms, in compactUniforms);
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

    private void BindExternalResources(
        ref RenderGraphFrame graph,
        in VirtualShadowMapFrameResources resources)
    {
        graph.BindExternalBuffer(
            _pageOwners,
            resources.PhysicalPageOwners.Resource,
            resources.PhysicalPageOwners.BoundaryStates.Span);
        graph.BindExternalTexture(_atlas, resources.Atlas.Resource, resources.Atlas.BoundaryStates.Span);
        graph.BindExternalBuffer(
            _pageMetadata,
            resources.PageMetadata.Resource,
            resources.PageMetadata.BoundaryStates.Span);
        graph.BindExternalBuffer(_pageLists, resources.PageLists.Resource, resources.PageLists.BoundaryStates.Span);
    }

    private void UploadPoolUniforms(ref RenderGraphFrame graph, GraphBufferInput input, uint phase)
    {
        var uniforms = new VirtualShadowPagePoolUniforms
        {
            MaxPhysicalPages = _shadowMap.Settings.MaxPhysicalPages,
            PageTableEntryCount = ActivePageTableEntryCount,
            FrameIndex = _frameIndex,
            PageListPhase = phase,
            PageTableEntriesPerView = _shadowMap.Settings.PageTableEntriesPerView,
            HierarchyWordsPerView = _shadowMap.Settings.PageHierarchyWordsPerView,
            MipLevelCount = _shadowMap.Settings.MipLevelCount,
            ViewCount = checked((uint)_viewCount),
        };
        _uniformData.Write(ref graph, input, in uniforms);
    }

    private void UploadAllocationUniforms(ref RenderGraphFrame graph)
    {
        var uniforms = new VirtualShadowPageAllocationUniforms
        {
            PageTableEntryCount = ActivePageTableEntryCount,
            PageTableEntriesPerView = _shadowMap.Settings.PageTableEntriesPerView,
            MaxPhysicalPages = _shadowMap.Settings.MaxPhysicalPages,
            ViewCount = checked((uint)_viewCount),
            DeferredInvalidationBudget = _shadowMap.Settings.DeferredInvalidationBudget,
            FrameIndex = _frameIndex,
            HierarchyWordsPerView = _shadowMap.Settings.PageHierarchyWordsPerView,
            MipLevelCount = _shadowMap.Settings.MipLevelCount,
        };
        _uniformData.Write(ref graph, _allocationUniforms, in uniforms);
    }

    private void BindInitialize(ref RenderGraphEdit graph, GraphBufferInput uniforms)
    {
        PassDefinition pass = graph.GetPass(_initializePass);
        _ = pass.Bind(uniforms, PipelineSync.ComputeShading);
        _ = pass.Bind(graph.CreateBufferUav(_framePageTable, structureStride: sizeof(uint), label: "Virtual shadow init page table"),
            PipelineSync.ComputeShading, GraphAccessMode.Write, WriteCoverage.Complete);
        _ = pass.Bind(graph.CreateBufferUav(_pageOwners, structureStride: sizeof(uint), label: "Virtual shadow init owners"),
            PipelineSync.ComputeShading, GraphAccessMode.Write, WriteCoverage.Complete);
        _ = pass.Bind(graph.CreateBufferUav(_pageLists, structureStride: sizeof(uint), label: "Virtual shadow init page lists"),
            PipelineSync.ComputeShading, GraphAccessMode.Write, WriteCoverage.Complete);
        _ = pass.Bind(graph.CreateBufferUav(_pageMetadata, structureStride: VirtualShadowPageMetadata.SizeInBytes, label: "Virtual shadow init metadata"),
            PipelineSync.ComputeShading, GraphAccessMode.Write, WriteCoverage.Complete);
        _ = pass.Bind(graph.CreateBufferUav(_requestedPages, structureStride: sizeof(uint),
            label: "Virtual shadow init request epochs"), PipelineSync.ComputeShading,
            GraphAccessMode.Write, WriteCoverage.Complete);
        _ = pass.Bind(graph.CreateBufferUav(_requestedPageFlags, structureStride: sizeof(uint),
            label: "Virtual shadow init request flags"), PipelineSync.ComputeShading,
            GraphAccessMode.Write, WriteCoverage.Complete);
        _ = pass.Bind(graph.CreateBufferUav(_requestedPageCount, structureStride: sizeof(uint),
            label: "Virtual shadow init request count"), PipelineSync.ComputeShading,
            GraphAccessMode.Write, WriteCoverage.Complete);
        _ = pass.Bind(graph.CreateTextureUav(_pageReceiverMasks, new TextureSubresourceRange(0, 1, 0, 1, TextureAspects.Color), Format.R32UInt, SomeEngine.Graphics.TextureViewDimension.Texture2D,
            label: "Virtual shadow init receiver masks"), PipelineSync.ComputeShading,
            GraphAccessMode.Write, WriteCoverage.Complete);
        BindReceiverMaskMips(ref graph, ref pass, GraphAccessMode.Write, WriteCoverage.Complete);

    }

    private void BindInvalidation(ref RenderGraphEdit graph, GraphBufferInput uniforms)
    {
        PassDefinition pass = graph.GetPass(_invalidatePass);
        _ = pass.Bind(uniforms, PipelineSync.ComputeShading);
        _ = pass.Bind(graph.CreateBufferSrv(_frameShadowViews, structureStride: VirtualShadowView.SizeInBytes,
            label: "Virtual shadow invalidation views"), PipelineSync.ComputeShading);
        _ = pass.Bind(graph.CreateBufferSrv(_casterBounds, structureStride: 16, label: "Virtual shadow caster bounds"),
            PipelineSync.ComputeShading);
        _ = pass.Bind(graph.CreateBufferSrv(_frameChangedInstances, structureStride: sizeof(uint),
            label: "Virtual shadow changed caster indices"), PipelineSync.ComputeShading);
        BindRasterDepthCacheClasses(ref graph, ref pass, PipelineSync.ComputeShading);
        _ = pass.Bind(graph.CreateBufferSrv(_framePreviousDepthCacheClasses, structureStride: sizeof(uint)), PipelineSync.ComputeShading);
        _ = pass.Bind(graph.CreateBufferSrv(_frameLodBounds, structureStride: 16), PipelineSync.ComputeShading);
        _ = pass.Bind(graph.CreateBufferSrv(_requestedPages, structureStride: sizeof(uint),
            label: "Virtual shadow request epochs"), PipelineSync.ComputeShading);
        _ = pass.Bind(graph.CreateBufferSrv(_requestedPageFlags, structureStride: sizeof(uint),
            label: "Virtual shadow request flags"), PipelineSync.ComputeShading);
        _ = pass.Bind(graph.CreateBufferUav(_pageMetadata, structureStride: VirtualShadowPageMetadata.SizeInBytes,
            label: "Virtual shadow invalidation metadata"), PipelineSync.ComputeShading, GraphAccessMode.ReadWrite, WriteCoverage.Partial,
            unorderedAccessHazards: UnorderedAccessHazardMode.AtomicMergeable);
        _ = pass.Bind(graph.CreateBufferSrv(_allocatedPageHierarchy, structureStride: sizeof(uint),
            label: "Virtual shadow allocated page hierarchy"), PipelineSync.ComputeShading);
        _ = pass.Bind(graph.CreateBufferSrv(_allocatedPageRectBounds, structureStride: 4u * sizeof(uint),
            label: "Virtual shadow allocated page rect bounds"), PipelineSync.ComputeShading);
        _ = pass.Bind(graph.CreateBufferUav(_framePageTable, structureStride: sizeof(uint),
            label: "Virtual shadow invalidation page table"), PipelineSync.ComputeShading,
            GraphAccessMode.ReadWrite, WriteCoverage.Partial);
    }

    private void BindMark(ref RenderGraphEdit graph, GraphBufferInput uniforms)
    {
        PassDefinition pass = graph.GetPass(_markPass);
        BindPageMarking(ref graph, ref pass, uniforms);
    }

    private void BindPageMarking(ref RenderGraphEdit graph, ref PassDefinition pass, GraphBufferInput uniforms)
    {
        _ = pass.Bind(uniforms, PipelineSync.ComputeShading);
        _ = pass.Bind(graph.CreateBufferSrv(_frameShadowViews, structureStride: VirtualShadowView.SizeInBytes,
            label: "Virtual shadow demand views"), PipelineSync.ComputeShading);
        _lighting.BindVirtualShadowPageMarking(ref graph, ref pass, PipelineSync.ComputeShading);
        _ = pass.Bind(graph.CreateTextureSrv(
            _receiverDepth,
            new TextureSubresourceRange(0, 1, 0, 1, TextureAspects.Depth),
            Format.D32Float,
            SomeEngine.Graphics.TextureViewDimension.Texture2DArray,
            label: "Virtual shadow receiver depth"), PipelineSync.ComputeShading);
        _ = pass.Bind(graph.CreateBufferSrv(
            _frameResolutionBias,
            structureStride: sizeof(float),
            label: "Virtual shadow dynamic resolution bias"),
            PipelineSync.ComputeShading);
        _ = pass.Bind(graph.CreateBufferSrv(_surfaceReceivers, structureStride: SurfaceReceiver.SizeInBytes), PipelineSync.ComputeShading);
        _ = pass.Bind(graph.CreateBufferSrv(_additionalReceivers, structureStride: SurfaceReceiver.SizeInBytes), PipelineSync.ComputeShading);
        _ = pass.Bind(graph.CreateBufferUav(_requestedPages, structureStride: sizeof(uint),
            label: "Virtual shadow request flags"), PipelineSync.ComputeShading,
            GraphAccessMode.ReadWrite, WriteCoverage.Partial);
        _ = pass.Bind(graph.CreateBufferUav(_requestedPageFlags, structureStride: sizeof(uint),
            label: "Virtual shadow request page flags"), PipelineSync.ComputeShading,
            GraphAccessMode.ReadWrite, WriteCoverage.Partial);
        _ = pass.Bind(graph.CreateBufferUav(_requestedPageIndices, structureStride: sizeof(uint),
            label: "Virtual shadow requested page indices"), PipelineSync.ComputeShading,
            GraphAccessMode.Write, WriteCoverage.Partial,
            resultContents: ResourceContentState.Defined,
            contentScope: BufferContentScope.CountBoundedPrefix);
        _ = pass.Bind(graph.CreateBufferUav(_requestedPageCount, structureStride: sizeof(uint),
            label: "Virtual shadow requested page count"), PipelineSync.ComputeShading,
            GraphAccessMode.ReadWrite, WriteCoverage.Partial);
        _ = pass.Bind(graph.CreateTextureUav(_pageReceiverMasks, new TextureSubresourceRange(0, 1, 0, 1, TextureAspects.Color), Format.R32UInt, SomeEngine.Graphics.TextureViewDimension.Texture2D,
            label: "Virtual shadow page receiver masks"), PipelineSync.ComputeShading,
            GraphAccessMode.ReadWrite, WriteCoverage.Partial);
    }

    private void BindDynamicResolution(ref RenderGraphEdit graph, GraphBufferInput uniforms)
    {
        PassDefinition pass = graph.GetPass(_dynamicResolutionPass);
        _ = pass.Bind(uniforms, PipelineSync.ComputeShading);
        _ = pass.Bind(graph.CreateBufferSrv(_pageLists, structureStride: sizeof(uint),
            label: "Virtual shadow available page count"), PipelineSync.ComputeShading);
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

    private void BindCoarseMark(ref RenderGraphEdit graph, GraphBufferInput uniforms)
        => BindCoarseMark(ref graph, _coarseMarkPass, uniforms);

    private void BindCoarseMark(ref RenderGraphEdit graph, GraphPassId passId, GraphBufferInput uniforms)
    {
        PassDefinition pass = graph.GetPass(passId);
        _ = pass.Bind(uniforms, PipelineSync.ComputeShading);
        _ = pass.Bind(graph.CreateBufferSrv(
            _frameShadowViews,
            structureStride: VirtualShadowView.SizeInBytes,
            label: "Virtual shadow coarse demand views"), PipelineSync.ComputeShading);
        _ = pass.Bind(graph.CreateBufferUav(
            _requestedPages,
            structureStride: sizeof(uint),
            label: "Virtual shadow coarse request flags"), PipelineSync.ComputeShading,
            GraphAccessMode.ReadWrite, WriteCoverage.Partial);
        _ = pass.Bind(graph.CreateBufferUav(_requestedPageFlags, structureStride: sizeof(uint),
            label: "Virtual shadow coarse request page flags"), PipelineSync.ComputeShading,
            GraphAccessMode.ReadWrite, WriteCoverage.Partial);
        _ = pass.Bind(graph.CreateBufferUav(_requestedPageIndices, structureStride: sizeof(uint),
            label: "Virtual shadow coarse requested page indices"), PipelineSync.ComputeShading,
            GraphAccessMode.Write, WriteCoverage.Partial,
            resultContents: ResourceContentState.Defined,
            contentScope: BufferContentScope.CountBoundedPrefix);
        _ = pass.Bind(graph.CreateBufferUav(_requestedPageCount, structureStride: sizeof(uint),
            label: "Virtual shadow coarse requested page count"), PipelineSync.ComputeShading,
            GraphAccessMode.ReadWrite, WriteCoverage.Partial);
        _ = pass.Bind(graph.CreateTextureUav(_pageReceiverMasks, new TextureSubresourceRange(0, 1, 0, 1, TextureAspects.Color), Format.R32UInt, SomeEngine.Graphics.TextureViewDimension.Texture2D,
            label: "Virtual shadow coarse receiver masks"), PipelineSync.ComputeShading,
            GraphAccessMode.ReadWrite, WriteCoverage.Partial);
    }

    private void BindPool(
        ref RenderGraphEdit graph,
        GraphPassId passId, GraphBufferInput uniformBuffer)
    {
        PassDefinition pass = graph.GetPass(passId);
        _ = pass.Bind(uniformBuffer, PipelineSync.ComputeShading);
        _ = pass.Bind(graph.CreateBufferSrv(_frameShadowViews, structureStride: VirtualShadowView.SizeInBytes,
            label: "Virtual shadow pool views"), PipelineSync.ComputeShading);
        _ = pass.Bind(graph.CreateBufferUav(_framePageTable, structureStride: sizeof(uint),
            label: "Virtual shadow pool page table"), PipelineSync.ComputeShading, GraphAccessMode.ReadWrite, WriteCoverage.Partial);
        _ = pass.Bind(graph.CreateBufferSrv(_requestedPages, structureStride: sizeof(uint),
            label: "Virtual shadow pool request flags"), PipelineSync.ComputeShading);
        _ = pass.Bind(graph.CreateBufferSrv(_requestedPageCount, structureStride: sizeof(uint),
            label: "Virtual shadow pool requested page count"), PipelineSync.ComputeShading);
        _ = pass.Bind(graph.CreateBufferUav(_pageRequestDispatchArguments,
            label: "Virtual shadow requested-page dispatch arguments"), PipelineSync.ComputeShading,
            GraphAccessMode.Write, WriteCoverage.Complete);
        _ = pass.Bind(graph.CreateBufferUav(_pageOwners, structureStride: sizeof(uint),
            label: "Virtual shadow pool owners"), PipelineSync.ComputeShading,
            GraphAccessMode.ReadWrite, WriteCoverage.Partial);
        _ = pass.Bind(graph.CreateBufferUav(_pageMetadata, structureStride: VirtualShadowPageMetadata.SizeInBytes,
            label: "Virtual shadow pool metadata"), PipelineSync.ComputeShading,
            GraphAccessMode.ReadWrite, WriteCoverage.Partial);
        _ = pass.Bind(graph.CreateBufferUav(_pageLists, structureStride: sizeof(uint),
            label: "Virtual shadow page lists"), PipelineSync.ComputeShading,
            GraphAccessMode.ReadWrite, WriteCoverage.Partial);
        _ = pass.Bind(graph.CreateBufferUav(_allocatedPageHierarchy, structureStride: sizeof(uint),
            label: "Virtual shadow allocated page hierarchy"), PipelineSync.ComputeShading,
            GraphAccessMode.ReadWrite, WriteCoverage.Partial);
        _ = pass.Bind(graph.CreateBufferUav(_allocatedPageRectBounds, structureStride: 4u * sizeof(uint),
            label: "Virtual shadow allocated page rect bounds"), PipelineSync.ComputeShading,
            GraphAccessMode.ReadWrite, WriteCoverage.Partial);
    }

    private void BindAllocation(
        ref RenderGraphEdit graph,
        GraphPassId passId, GraphBufferInput uniformBuffer)
    {
        PassDefinition pass = graph.GetPass(passId);
        _ = pass.Bind(uniformBuffer, PipelineSync.ComputeShading);
        _ = pass.Bind(graph.CreateBufferSrv(_frameShadowViews, structureStride: VirtualShadowView.SizeInBytes,
            label: "Virtual shadow allocation views"), PipelineSync.ComputeShading);
        _ = pass.Bind(graph.CreateBufferSrv(_requestedPageIndices, structureStride: sizeof(uint),
            label: "Virtual shadow requested page indices"), PipelineSync.ComputeShading,
            BufferContentScope.CountBoundedPrefix);
        _ = pass.Bind(graph.CreateBufferSrv(_requestedPageCount, structureStride: sizeof(uint),
            label: "Virtual shadow requested page count"), PipelineSync.ComputeShading);
        _ = pass.Bind(graph.CreateBufferSrv(_requestedPageFlags, structureStride: sizeof(uint),
            label: "Virtual shadow requested page flags"), PipelineSync.ComputeShading);
        _ = pass.Bind(graph.CreateBufferUav(_framePageTable, structureStride: sizeof(uint),
            label: "Virtual shadow allocation page table"), PipelineSync.ComputeShading,
            GraphAccessMode.ReadWrite, WriteCoverage.Partial);
        _ = pass.Bind(graph.CreateBufferUav(_pageOwners, structureStride: sizeof(uint),
            label: "Virtual shadow allocation owners"), PipelineSync.ComputeShading,
            GraphAccessMode.ReadWrite, WriteCoverage.Partial);
        _ = pass.Bind(graph.CreateBufferUav(_pageMetadata, structureStride: VirtualShadowPageMetadata.SizeInBytes,
            label: "Virtual shadow allocation metadata"), PipelineSync.ComputeShading,
            GraphAccessMode.ReadWrite, WriteCoverage.Partial);
        _ = pass.Bind(graph.CreateBufferUav(_pageLists, structureStride: sizeof(uint),
            label: "Virtual shadow page lists"), PipelineSync.ComputeShading,
            GraphAccessMode.ReadWrite, WriteCoverage.Partial);
        _ = pass.Bind(graph.CreateBufferUav(_activePages, structureStride: 4 * sizeof(uint),
            label: "Virtual shadow active raster pages"), PipelineSync.ComputeShading,
            GraphAccessMode.Write, WriteCoverage.Partial,
            resultContents: ResourceContentState.Defined,
            contentScope: BufferContentScope.CountBoundedPrefix);
        _ = pass.Bind(graph.CreateBufferUav(_activePageCount, structureStride: sizeof(uint),
            label: "Virtual shadow active page count"), PipelineSync.ComputeShading,
            GraphAccessMode.ReadWrite, WriteCoverage.Partial);
        _ = pass.Bind(graph.CreateBufferUav(_uncachedPageRectBounds, structureStride: 4u * sizeof(uint),
            label: "Virtual shadow uncached page rect bounds"), PipelineSync.ComputeShading,
            GraphAccessMode.ReadWrite, WriteCoverage.Partial);
        _ = pass.Bind(graph.CreateBufferUav(_uncachedPageHierarchy, structureStride: 2u * sizeof(uint),
            label: "Virtual shadow uncached page hierarchy"), PipelineSync.ComputeShading,
            GraphAccessMode.ReadWrite, WriteCoverage.Partial);
        _ = pass.Bind(graph.CreateBufferUav(_allocationFailures, structureStride: sizeof(uint),
            label: "Virtual shadow allocation failures"), PipelineSync.ComputeShading,
            GraphAccessMode.ReadWrite, WriteCoverage.Partial);
        _ = pass.Bind(graph.CreateTextureUav(_pageReceiverMasks, new TextureSubresourceRange(0, 1, 0, 1, TextureAspects.Color), Format.R32UInt, SomeEngine.Graphics.TextureViewDimension.Texture2D,
            label: "Virtual shadow allocated receiver coverage"), PipelineSync.ComputeShading,
            GraphAccessMode.ReadWrite, WriteCoverage.Partial);
        BindReceiverMaskMips(ref graph, ref pass, GraphAccessMode.ReadWrite, WriteCoverage.Partial, atomic: true);
    }

    private void BindClear(ref RenderGraphEdit graph, GraphBufferInput uniforms)
    {
        PassDefinition pass = graph.GetPass(_clearPass);
        _ = pass.Bind(uniforms, PipelineSync.ComputeShading);
        _ = pass.Bind(graph.CreateBufferSrv(_activePages, structureStride: 4 * sizeof(uint),
            label: "Virtual shadow active pages to clear"), PipelineSync.ComputeShading,
            BufferContentScope.CountBoundedPrefix);
        _ = pass.Bind(graph.CreateBufferSrv(_activePageCount, structureStride: sizeof(uint)),
            PipelineSync.ComputeShading);
        _ = pass.Bind(graph.CreateTextureUav(_atlas, format: Format.R32UInt,
            dimension: SomeEngine.Graphics.TextureViewDimension.Texture2DArray,
            label: "Virtual shadow atlas clear target"), PipelineSync.ComputeShading,
            GraphAccessMode.ReadWrite, WriteCoverage.Partial);
        _ = pass.Bind(graph.CreateBufferSrv(_pageMetadata, structureStride: VirtualShadowPageMetadata.SizeInBytes), PipelineSync.ComputeShading);
    }

    private void BindCompact(
        ref RenderGraphEdit graph,
        GraphBufferInput uniforms,
        GraphBufferInput sourceViews)
    {
        PassDefinition pass = graph.GetPass(_compactViewsPass);
        _ = pass.Bind(uniforms, PipelineSync.ComputeShading);
        _ = pass.Bind(graph.CreateBufferSrv(_uncachedPageRectBounds, structureStride: 4u * sizeof(uint),
            label: "Virtual shadow uncached page rect bounds"), PipelineSync.ComputeShading);
        _ = pass.Bind(graph.CreateBufferSrv(_activePageCount, structureStride: sizeof(uint),
            label: "Virtual shadow active raster-page count"), PipelineSync.ComputeShading);
        _ = pass.Bind(graph.CreateBufferSrv(_frameShadowViews,
            structureStride: VirtualShadowView.SizeInBytes,
            label: "Virtual shadow current source views"), PipelineSync.ComputeShading);
        _ = pass.Bind(graph.CreateBufferSrv(sourceViews, structureStride: (uint)ClusterRenderContext.ViewSizeInBytes,
            label: "Virtual shadow source Cluster views"), PipelineSync.ComputeShading);
        _ = pass.Bind(graph.CreateBufferUav(_rasterInvocationViews, structureStride: (uint)ClusterRenderContext.ViewSizeInBytes,
            label: "Virtual shadow raster invocation views"), PipelineSync.ComputeShading,
            GraphAccessMode.Write, WriteCoverage.Partial,
            resultContents: ResourceContentState.Defined,
            contentScope: BufferContentScope.CountBoundedPrefix);
        _ = pass.Bind(graph.CreateBufferUav(_rasterTargetViews, structureStride: ClusterRenderContext.TargetViewSizeInBytes,
            label: "Virtual shadow raster target views"), PipelineSync.ComputeShading,
            GraphAccessMode.Write, WriteCoverage.Partial,
            resultContents: ResourceContentState.Defined,
            contentScope: BufferContentScope.CountBoundedPrefix);
        _ = pass.Bind(graph.CreateBufferUav(_clusterInvocationDispatchArguments,
            label: "Virtual shadow Cluster invocation arguments"), PipelineSync.ComputeShading,
            GraphAccessMode.ReadWrite, WriteCoverage.Partial);
        _ = pass.Bind(graph.CreateBufferUav(_pageWorkDispatchArguments,
            label: "Virtual shadow page-work dispatch arguments"), PipelineSync.ComputeShading,
            GraphAccessMode.Write, WriteCoverage.Complete);
    }

    private void BindRasterInvocationCount(
        ref RenderGraphEdit graph,
        ref PassDefinition pass, PipelineSync sync)
    {
        _ = pass.Bind(
            graph.CreateBufferSrv(
                _clusterInvocationDispatchArguments,
                range: new BufferRange(0u, 16u),
                structureStride: sizeof(uint),
                label: "Virtual shadow raster invocation count"),
            sync);
    }

    private void BindPageHiZInitialize(
        ref RenderGraphEdit graph,
        GraphPassId passId,
        GraphBufferInput uniforms)
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
        _ = pass.Bind(graph.CreateBufferSrv(_pageMetadata,
            structureStride: VirtualShadowPageMetadata.SizeInBytes,
            label: "Virtual shadow page HiZ depth validity"), PipelineSync.ComputeShading);
        _ = pass.Bind(
            graph.CreateTextureSrv(
                _atlas,
                format: Format.R32UInt,
                dimension: SomeEngine.Graphics.TextureViewDimension.Texture2DArray,
                label: "Virtual shadow page HiZ source atlas"),
            PipelineSync.ComputeShading);
        BindPageHiZDestinations(ref graph, ref pass, 0u);
    }

    private void BindPageHiZReduce(
        ref RenderGraphEdit graph,
        GraphPassId passId,
        GraphBufferInput uniforms,
        uint sourceMip,
        uint destinationMip)
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
        _ = pass.Bind(graph.CreateBufferSrv(_pageMetadata,
            structureStride: VirtualShadowPageMetadata.SizeInBytes,
            label: "Virtual shadow page HiZ depth validity"), PipelineSync.ComputeShading);
        _ = pass.Bind(
            graph.CreateTextureSrv(
                _pageHiZ,
                new TextureSubresourceRange(
                    sourceMip,
                    1,
                    0,
                    PageHiZLayerCount,
                    TextureAspects.Color),
                Format.R32Float,
                SomeEngine.Graphics.TextureViewDimension.Texture2DArray,
                "Virtual shadow page HiZ source mip"),
            PipelineSync.ComputeShading);
        BindPageHiZDestinations(ref graph, ref pass, destinationMip);
    }

    private void BindPageHiZDestinations(
        ref RenderGraphEdit graph, ref PassDefinition pass, uint firstMip)
    {
        uint lastMip = checked((uint)BitOperations.Log2(Math.Max(_shadowMap.Settings.PageSize / 2u, 1u)));
        for (uint offset = 0u; offset < 4u; offset++)
        {
            _ = pass.Bind(
                graph.CreateTextureUav(
                    _pageHiZ,
                    new TextureSubresourceRange(Math.Min(firstMip + offset, lastMip), 1,
                        0, PageHiZLayerCount, TextureAspects.Color),
                    Format.R32Float,
                    SomeEngine.Graphics.TextureViewDimension.Texture2DArray,
                    "Virtual shadow page HiZ destination mip"),
                PipelineSync.ComputeShading, GraphAccessMode.Write, WriteCoverage.Partial);
        }
    }

    private void BindShadeViews(ref RenderGraphEdit graph, ref PassDefinition pass, PipelineSync sync)
    {
        _ = pass.Bind(graph.CreateBufferSrv(_frameShadowViews, structureStride: VirtualShadowView.SizeInBytes,
            label: "Standard shade virtual shadow views"), sync);
    }

    private void BindShadePageTable(ref RenderGraphEdit graph, ref PassDefinition pass, PipelineSync sync)
    {
        _ = pass.Bind(graph.CreateBufferSrv(_framePageTable, structureStride: sizeof(uint),
            label: "Standard shade virtual shadow page table"), sync);
    }

    private void BindShadeAtlas(ref RenderGraphEdit graph, ref PassDefinition pass, PipelineSync sync)
    {
        _ = pass.Bind(graph.CreateTextureSrv(_atlas, format: Format.R32UInt,
            dimension: SomeEngine.Graphics.TextureViewDimension.Texture2DArray,
            label: "Standard shade virtual shadow atlas"), sync);
    }

    private void BindShadeBlueNoise(ref RenderGraphEdit graph, ref PassDefinition pass, PipelineSync sync) =>
        pass.Bind(_blueNoiseInput, sync);

    private void BindShadeSampling(ref RenderGraphEdit graph, ref PassDefinition pass, PipelineSync sync)
    {
        _ = pass.Bind(_frameSamplingUniforms, sync);
    }

    private void BindShadeDynamicResolutionBias(
        ref RenderGraphEdit graph,
        ref PassDefinition pass,
        PipelineSync sync)
    {
        _ = pass.Bind(graph.CreateBufferSrv(
            _frameResolutionBias,
            structureStride: sizeof(float),
            label: "Standard shade virtual shadow dynamic resolution bias"), sync);
    }

    private GraphPassId AddComputePass(
        ref RenderGraphEdit edit,
        string label,
        LinkedComputePipeline pipeline,
        VirtualShadowDispatchPassData data,
        GraphRegionId region) =>
        edit.AddComputeFramePass(
            label,
            PassQueueSelection.AnyOfType(QueueType.Graphics),
            data,
            new PassOptions(Culling: PassCullingMode.NeverCull, RepeatCommands: true),
            pipeline.Pipeline,
            pipeline.ParameterLayout,
            default,
            static (ref PassDefinition _, ref VirtualShadowDispatchPassData _) => { },
            VirtualShadowDispatchPassData.Record, region: region);

    private GraphPassId AddIndirectPass(
        ref RenderGraphEdit edit,
        string label,
        LinkedComputePipeline pipeline,
        GraphBufferId arguments,
        ulong offset = 0u,
        GraphRegionId region = default)
    {
        var data = new VirtualShadowIndirectPassData(_dispatchLayout, arguments, offset);
        // These passes update the exclusive atlas and HiZ between graphics raster passes.
        return edit.AddComputeFramePass(
            label,
            PassQueueSelection.AnyOfType(QueueType.Graphics),
            data,
            new PassOptions(Culling: PassCullingMode.NeverCull, RepeatCommands: true),
            pipeline.Pipeline,
            pipeline.ParameterLayout,
            default,
            VirtualShadowIndirectPassData.Declare,
            VirtualShadowIndirectPassData.Record, region: region);
    }

    private static GraphBufferId Counter(RenderGraphEdit edit, string label) =>
        edit.CreateBuffer(new BufferDesc(
            sizeof(uint),
            BufferUsages.ShaderRead | BufferUsages.ShaderWrite | BufferUsages.CopyDestination | BufferUsages.CopySource,
            label));

    private static GraphBufferId DispatchBuffer(
        RenderGraphEdit edit,
        string label,
        uint commandCount = 1u) =>
        edit.CreateBuffer(new BufferDesc(
            checked(DispatchStride * commandCount),
            BufferUsages.ShaderRead | BufferUsages.ShaderWrite |
                BufferUsages.Indirect | BufferUsages.CopyDestination,
            label));

    private static uint DivideRoundUp(uint value, uint divisor) =>
        value == 0u ? 0u : checked((value + divisor - 1u) / divisor);

    private void ClearFrame()
    {

        _frameHasPreviousHistory = false;
        _viewCount = 0;
        _frameActive = false;
        _shadowFrameActive = false;
    }
}
