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

namespace SomeEngine.Render.Features.Shadows;

/// <summary>
/// Virtual-shadow page processing and generic virtual-depth geometry integration.
/// </summary>
public sealed class VirtualShadowFeature : IClusterStageResourceProvider, IDisposable
{
    private readonly GraphBufferInput _initUniforms;
    private readonly GraphBufferInput _invalidationUniforms;
    private readonly GraphBufferInput _coarseDemandUniforms;
    private readonly GraphBufferInput _demandUniforms;
    private readonly GraphBufferInput _poolUniforms;
    private readonly GraphBufferInput _allocationUniforms;
    private readonly GraphBufferInput _clearUniforms;
    private readonly GraphBufferInput _pageHiZInitializeUniforms;
    private readonly GraphBufferInput[] _pageHiZReduceUniforms;
    private readonly GraphBufferInput _casterBounds;
    private readonly GraphTextureInput _receiverDepth;
    private readonly GraphPassId _requestResetPass;
    private readonly GraphPassId _previousPageTableCopyPass;
    private readonly GraphPassId _previousPageTableClearPass;

    private const uint DispatchStride = 16u;
    private const uint EvictionBucketCount = 8u;
    private const uint PageMetadataStride = 16u;

    private readonly IGraphicsBackend _backend;
    private readonly Device _device;
    private readonly VirtualShadowMap _shadowMap;
    private readonly ClusterPipelineSystem _geometry;
    private readonly LightingFeature _lighting;
    private readonly IndirectCommandLayout _dispatchLayout;
    private RenderView[] _shadowViews = [];
    private VirtualShadowView[] _previousShadowViews = [];
    private Matrix4x4[] _previousShadowViewProjections = [];

    private readonly GraphBufferInput _framePageTable;
    private readonly GraphBufferId _pageOwners;
    private readonly GraphTextureId _atlas;
    private readonly GraphTextureId _pageHiZ;
    private readonly GraphBufferId _freePages;
    private readonly GraphBufferId _freePageCount;
    private readonly GraphBufferId _pageMetadata;
    private readonly GraphBufferId _evictionPages;
    private readonly GraphBufferId _evictionBucketCounts;

    private readonly GraphBufferInput _requestedPages;
    private readonly GraphBufferId _activePages;
    private readonly GraphBufferId _activePageCount;
    private readonly GraphBufferId _rasterRegionGrid;
    private readonly GraphBufferId _clusterInvocationDispatchArguments;
    private readonly GraphBufferId _pageWorkDispatchArguments;
    private readonly GraphBufferId _allocationFailures;
    private readonly GraphBufferId _rasterInvocationViews;
    private readonly GraphBufferId _rasterTargetInvocations;
    private readonly GraphBufferInput _clusterSourceViews;

    private readonly GraphPassId _frameResetPass;
    private readonly GraphPassId _atlasInitializePass;
    private readonly GraphPassId _pageHiZStorageInitializePass;
    private readonly GraphPassId _initializePass;
    private readonly GraphPassId _invalidatePass;
    private readonly GraphPassId _coarseMarkPass;
    private readonly GraphPassId _markPass;
    private readonly GraphPassId _poolResetPass;
    private readonly GraphPassId _poolPass;
    private readonly GraphPassId _allocatePass;
    private readonly GraphPassId _clearPass;
    private readonly GraphPassId _compactViewsPass;
    private readonly GraphPassId _pageHiZInitializePass;
    private readonly GraphPassId[] _pageHiZReducePasses;
    private readonly GraphPassId _finalPageHiZInitializePass;
    private readonly GraphPassId[] _finalPageHiZReducePasses;
    private readonly GraphPassId _rasterCompletePass;
    private readonly GraphPassId _frameCompletePass;

    private readonly GraphBufferInput _frameShadowViews;
    private readonly GraphBufferInput _framePreviousShadowViews;
    private readonly GraphBufferInput _framePreviousPageTable;
    private readonly GraphBufferInput _frameSamplingUniforms;
    private readonly GraphBufferInput _frameInvocationUniforms;
    private bool _frameHasPreviousHistory;
    private uint _frameIndex;
    private bool _forceStateReset;
    private int _viewCount;
    private bool _shadowFrameActive;
    private bool _frameActive;
    private bool _disposed;
    private RenderInstanceBatch? _committedInstances;
    private RenderInstanceBatch? _frameInstances;
    private ulong _committedInstanceRevision;
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
        LightingFeature lighting)
    {
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        _device = device ?? throw new ArgumentNullException(nameof(device));
        ArgumentNullException.ThrowIfNull(shaders);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(graph);
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
            shadowMap = null;
            dispatchLayout = null;

            RenderGraphEdit edit = graph.BeginEdit();
            try
            {
                ulong pageBytes = checked((ulong)settings.MaxPhysicalPages * sizeof(uint));
                _pageOwners = edit.DeclareExternalBuffer(new BufferDesc(
                    pageBytes,
                    BufferUsages.ShaderRead | BufferUsages.ShaderWrite | BufferUsages.CopyDestination,
                    "Virtual shadow page owner slot"));
                _atlas = edit.DeclareExternalTexture(new TextureDesc(
                    TextureDimension.Texture2D,
                    settings.AtlasSize,
                    settings.AtlasSize,
                    1,
                    1,
                    1,
                    1,
                    Format.R32UInt,
                    TextureUsages.Sampled | TextureUsages.Storage | TextureUsages.ColorAttachment,
                    label: "Virtual shadow atlas slot"));
                int pageHiZMipCount = checked(BitOperations.Log2(Math.Max(settings.PageSize / 2u, 1u)) + 1);
                _pageHiZ = edit.CreatePersistentTexture(new TextureDesc(
                    TextureDimension.Texture2D,
                    Math.Max(settings.PageSize / 2u, 1u),
                    Math.Max(settings.PageSize / 2u, 1u),
                    1,
                    checked((uint)pageHiZMipCount),
                    settings.MaxPhysicalPages,
                    1,
                    Format.R32Float,
                    TextureUsages.Sampled | TextureUsages.Storage | TextureUsages.ColorAttachment,
                    label: "Virtual shadow physical-page HiZ"));
                BufferUsages cacheUsages = BufferUsages.ShaderRead | BufferUsages.ShaderWrite |
                    BufferUsages.CopySource | BufferUsages.CopyDestination;
                _freePages = edit.DeclareExternalBuffer(new BufferDesc(
                    pageBytes,
                    cacheUsages,
                    "Virtual shadow free pages slot"));
                _freePageCount = edit.DeclareExternalBuffer(new BufferDesc(
                    sizeof(uint),
                    cacheUsages,
                    "Virtual shadow free count slot"));
                _pageMetadata = edit.DeclareExternalBuffer(new BufferDesc(
                    checked((ulong)settings.MaxPhysicalPages * PageMetadataStride),
                    cacheUsages,
                    "Virtual shadow page metadata slot"));
                _evictionPages = edit.DeclareExternalBuffer(new BufferDesc(
                    checked((ulong)settings.MaxPhysicalPages *
                        EvictionBucketCount * sizeof(uint)),
                    cacheUsages,
                    "Virtual shadow eviction pages slot"));
                _evictionBucketCounts = edit.DeclareExternalBuffer(new BufferDesc(
                    checked((ulong)EvictionBucketCount * sizeof(uint)),
                    cacheUsages,
                    "Virtual shadow eviction counts slot"));

                _activePages = edit.CreateBuffer(new BufferDesc(
                    checked(pageBytes * 2u),
                    BufferUsages.ShaderRead | BufferUsages.ShaderWrite,
                    "Virtual shadow active raster pages"));
                _activePageCount = Counter(edit, "Virtual shadow active page count");
                uint sourceViewCapacity = checked(settings.MaxShadowLights * Math.Max(settings.DirectionalClipmapLevels, 6u));
                uint fineGridWords = settings.VirtualPagesPerView +
                    settings.VirtualPagesPerRow * DivideRoundUp(settings.VirtualPagesPerRow, 32u);
                uint coarseGridWords = settings.CoarsePagesPerView +
                    settings.CoarseTablePagesPerRow * DivideRoundUp(settings.CoarseTablePagesPerRow, 32u);
                _rasterRegionGrid = edit.CreateBuffer(new BufferDesc(
                    checked((ulong)sourceViewCapacity * (fineGridWords + coarseGridWords) * sizeof(uint)),
                    BufferUsages.ShaderRead | BufferUsages.ShaderWrite | BufferUsages.CopyDestination,
                    "Virtual shadow dirty region grid"));
                _clusterInvocationDispatchArguments = DispatchBuffer(
                    edit,
                    "Virtual shadow Cluster invocation arguments");
                _pageWorkDispatchArguments = DispatchBuffer(
                    edit,
                    "Virtual shadow page-work dispatch arguments",
                    checked((uint)pageHiZMipCount));
                _allocationFailures = edit.CreateBuffer(new BufferDesc(
                    2u * sizeof(uint),
                    BufferUsages.ShaderRead | BufferUsages.ShaderWrite | BufferUsages.CopyDestination,
                    "Virtual shadow allocation failures"));
                _rasterInvocationViews = edit.CreateBuffer(new BufferDesc(
                    checked((ulong)sourceViewCapacity * 2u * 432u),
                    BufferUsages.ShaderRead | BufferUsages.ShaderWrite,
                    "Virtual shadow raster invocation views"));
                _rasterTargetInvocations = edit.CreateBuffer(new BufferDesc(
                    checked((ulong)settings.MaxPhysicalPages * 48u),
                    BufferUsages.ShaderRead | BufferUsages.ShaderWrite,
                    "Virtual shadow raster target invocations"));

                var resetData = new VirtualShadowFrameResetPassData(
                    _activePageCount,
                    _rasterRegionGrid,
                    _clusterInvocationDispatchArguments,
                    _pageWorkDispatchArguments,
                    _allocationFailures);
                _frameResetPass = edit.AddCopyFramePass(
                    "Reset virtual shadow frame data",
                    PassQueueSelection.AnyOfType(QueueType.Compute),
                    resetData,
                    new PassOptions(Culling: PassCullingMode.NeverCull),
                    VirtualShadowFrameResetPassData.Declare,
                    VirtualShadowFrameResetPassData.Record);

                var atlasInitialize = new VirtualShadowAtlasInitializePassData(_atlas);
                _atlasInitializePass = edit.AddCopyFramePass(
                    "Initialize virtual shadow atlas",
                    PassQueueSelection.AnyOfType(QueueType.Graphics),
                    atlasInitialize,
                    new PassOptions(Culling: PassCullingMode.NeverCull),
                    VirtualShadowAtlasInitializePassData.Declare,
                    VirtualShadowAtlasInitializePassData.Record);
                var pageHiZStorageInitialize = new VirtualShadowPageHiZStorageInitializePassData(
                    _pageHiZ,
                    new TextureSubresourceRange(
                        0,
                        checked((uint)pageHiZMipCount),
                        0,
                        settings.MaxPhysicalPages,
                        TextureAspects.Color));
                _pageHiZStorageInitializePass = edit.AddCopyFramePass(
                    "Initialize virtual shadow physical-page HiZ storage",
                    PassQueueSelection.AnyOfType(QueueType.Graphics),
                    pageHiZStorageInitialize,
                    new PassOptions(Culling: PassCullingMode.NeverCull),
                    VirtualShadowPageHiZStorageInitializePassData.Declare,
                    VirtualShadowPageHiZStorageInitializePassData.Record);

                _initializePass = AddComputePass(
                    ref edit,
                    "Initialize virtual shadow state",
                    _shadowMap.StateInitializePipeline,
                    new VirtualShadowDispatchPassData(default));
                _invalidatePass = AddComputePass(
                    ref edit,
                    "Invalidate moved virtual shadow casters",
                    _shadowMap.CasterInvalidationPipeline,
                    new VirtualShadowDispatchPassData(default));
                _coarseMarkPass = AddComputePass(
                    ref edit,
                    "Mark coarse virtual shadow pages",
                    _shadowMap.CoarsePageMarkPipeline,
                    new VirtualShadowDispatchPassData(default));
                _markPass = AddComputePass(
                    ref edit,
                    "Mark virtual shadow pages",
                    _shadowMap.PageMarkPipeline,
                    new VirtualShadowDispatchPassData(default));

                var poolReset = new VirtualShadowPoolResetPassData(
                    _freePageCount,
                    _evictionBucketCounts);
                _poolResetPass = edit.AddCopyFramePass(
                    "Reset virtual shadow page pool",
                    PassQueueSelection.AnyOfType(QueueType.Compute),
                    poolReset,
                    new PassOptions(Culling: PassCullingMode.NeverCull),
                    VirtualShadowPoolResetPassData.Declare,
                    VirtualShadowPoolResetPassData.Record);
                _poolPass = AddComputePass(
                    ref edit,
                    "Build virtual shadow page pool",
                    _shadowMap.PagePoolPipeline,
                    new VirtualShadowDispatchPassData(default));
                _allocatePass = AddComputePass(
                    ref edit,
                    "Allocate virtual shadow pages",
                    _shadowMap.PageAllocatePipeline,
                    new VirtualShadowDispatchPassData(default));
                _clearPass = AddIndirectPass(
                    ref edit,
                    "Clear dirty virtual shadow pages",
                    _shadowMap.PageClearPipeline,
                    _pageWorkDispatchArguments);
                _compactViewsPass = AddComputePass(
                    ref edit,
                    "Build virtual shadow raster regions",
                    _shadowMap.ViewCompactPipeline,
                    new VirtualShadowDispatchPassData(default));
                _pageHiZInitializePass = AddIndirectPass(
                    ref edit,
                    "Initialize virtual shadow physical-page HiZ",
                    _shadowMap.PageHiZInitializePipeline,
                    _pageWorkDispatchArguments);
                _pageHiZReducePasses = new GraphPassId[(pageHiZMipCount - 1) / 4];
                for (int mip = 4; mip < pageHiZMipCount; mip += 4)
                {
                    _pageHiZReducePasses[mip / 4 - 1] = AddIndirectPass(
                        ref edit,
                        $"Reduce virtual shadow physical-page HiZ mip {mip}",
                        _shadowMap.PageHiZReducePipeline,
                        _pageWorkDispatchArguments,
                        checked((ulong)mip * DispatchStride));
                }
                _finalPageHiZInitializePass = AddIndirectPass(
                    ref edit,
                    "Finalize virtual shadow physical-page HiZ",
                    _shadowMap.PageHiZInitializePipeline,
                    _pageWorkDispatchArguments);
                _finalPageHiZReducePasses = new GraphPassId[(pageHiZMipCount - 1) / 4];
                for (int mip = 4; mip < pageHiZMipCount; mip += 4)
                {
                    _finalPageHiZReducePasses[mip / 4 - 1] = AddIndirectPass(
                        ref edit,
                        $"Finalize virtual shadow physical-page HiZ mip {mip}",
                        _shadowMap.PageHiZReducePipeline,
                        _pageWorkDispatchArguments,
                        checked((ulong)mip * DispatchStride));
                }
                _rasterCompletePass = edit.AddCopyFramePass(
                    "Virtual shadow raster completion",
                    PassQueueSelection.AnyOfType(QueueType.Graphics),
                    0u,
                    new PassOptions(Culling: PassCullingMode.NeverCull),
                    static (ref PassDefinition _, ref uint _) => { },
                    static (ref CopyPassCommandScope _, in uint _) => { });
                _frameCompletePass = edit.AddGeneralFramePass(
                    "Finalize virtual shadow frame endpoints",
                    PassQueueSelection.AnyOfType(QueueType.Graphics),
                    0u,
                    new PassOptions(Culling: PassCullingMode.NeverCull),
                    static (ref PassDefinition _, ref uint _) => { },
                    static (ref GeneralPassCommandScope _, in uint _) => { });

                edit.OrderAfter(_atlasInitializePass, _frameResetPass);
                edit.OrderAfter(_pageHiZStorageInitializePass, _frameResetPass);
                edit.OrderAfter(_initializePass, _atlasInitializePass);
                edit.OrderAfter(_invalidatePass, _initializePass);
                edit.OrderAfter(_coarseMarkPass, _invalidatePass);
                edit.OrderAfter(_markPass, _coarseMarkPass);
                edit.OrderAfter(_poolResetPass, _markPass);
                edit.OrderAfter(_poolPass, _poolResetPass);
                edit.OrderAfter(_allocatePass, _poolPass);
                edit.OrderAfter(_compactViewsPass, _allocatePass);
                edit.OrderAfter(_clearPass, _compactViewsPass);
                edit.OrderAfter(_rasterCompletePass, _allocatePass);
                edit.OrderAfter(_frameCompletePass, _rasterCompletePass);
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

                _geometry.ConfigureRenderContext(new ClusterRenderContext(
                    1u,
                    BindRasterPolicyFilterResources,
                    BindRasterInvocationViews,
                    BindClusterSourceViews,
                    BindRasterInvocationCount,
                    _clusterInvocationDispatchArguments,
                    sourceViewCapacity * 2u,
                    settings.PageSize,
                    settings.PageSize,
                    _clearPass,
                    _pageHiZInitializePass,
                    _pageHiZReducePasses.Length == 0 ? _pageHiZInitializePass : _pageHiZReducePasses[^1],
                    _finalPageHiZInitializePass,
                    _finalPageHiZReducePasses.Length == 0 ? _finalPageHiZInitializePass : _finalPageHiZReducePasses[^1],
                    checked((uint)pageHiZMipCount),
                    Math.Max(settings.PageSize / 2u, 1u),
                    _rasterCompletePass));
                _geometry.ConfigureDepthCompleteBefore(_markPass);
                _geometry.ConfigureShadeBeginAfter(_rasterCompletePass);
                _geometry.ConfigureShadeCompleteBefore(_frameCompletePass);
                _framePageTable = edit.CreateBufferInput(BufferUsages.ShaderRead | BufferUsages.ShaderWrite | BufferUsages.CopySource);
                _requestedPages = edit.CreateBufferInput(BufferUsages.ShaderRead | BufferUsages.ShaderWrite | BufferUsages.CopyDestination);
                _framePreviousPageTable = edit.CreateBufferInput(BufferUsages.ShaderRead | BufferUsages.CopyDestination);
                _clusterSourceViews = edit.CreateBufferInput(BufferUsages.ShaderRead);
                _frameShadowViews = edit.CreateBufferInput(BufferUsages.ShaderRead);
                _framePreviousShadowViews = edit.CreateBufferInput(BufferUsages.ShaderRead);
                _frameChangedInstances = edit.CreateBufferInput(BufferUsages.ShaderRead);
                _frameSamplingUniforms = edit.CreateBufferInput(BufferUsages.Constant);
                _frameInvocationUniforms = edit.CreateBufferInput(BufferUsages.Constant);
                _initUniforms = edit.CreateBufferInput(BufferUsages.Constant);
                _invalidationUniforms = edit.CreateBufferInput(BufferUsages.Constant);
                _coarseDemandUniforms = edit.CreateBufferInput(BufferUsages.Constant);
                _demandUniforms = edit.CreateBufferInput(BufferUsages.Constant);
                _poolUniforms = edit.CreateBufferInput(BufferUsages.Constant);
                _allocationUniforms = edit.CreateBufferInput(BufferUsages.Constant);
                _clearUniforms = edit.CreateBufferInput(BufferUsages.Constant);
                _pageHiZInitializeUniforms = edit.CreateBufferInput(BufferUsages.Constant);
                _casterBounds = edit.CreateBufferInput(BufferUsages.ShaderRead);
                _receiverDepth = edit.CreateTextureInput();
                _pageHiZReduceUniforms = new GraphBufferInput[_pageHiZReducePasses.Length];
                BindInitialize(ref edit, _initUniforms);
                BindInvalidation(ref edit, _invalidationUniforms);
                BindCoarseMark(ref edit, _coarseDemandUniforms);
                BindMark(ref edit, _demandUniforms);
                BindPool(ref edit, _poolPass, _poolUniforms);
                BindAllocation(ref edit, _allocatePass, _allocationUniforms);
                BindClear(ref edit, _clearUniforms);
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
                _requestResetPass = edit.AddCopyFramePass(
                    "Reset virtual shadow request range", PassQueueSelection.AnyOfType(QueueType.Compute),
                    new VirtualShadowBufferClearPassData(_requestedPages),
                    new PassOptions(Culling: PassCullingMode.NeverCull),
                    VirtualShadowBufferClearPassData.Declare, VirtualShadowBufferClearPassData.Record);
                _previousPageTableCopyPass = edit.AddCopyFramePass(
                    "Copy previous virtual shadow page table", PassQueueSelection.AnyOfType(QueueType.Compute),
                    new VirtualShadowPageTableCopyData(_framePageTable, _framePreviousPageTable, 0, true),
                    new PassOptions(Culling: PassCullingMode.NeverCull),
                    VirtualShadowPageTableCopyData.Declare, VirtualShadowPageTableCopyData.Record);
                _previousPageTableClearPass = edit.AddCopyFramePass(
                    "Clear previous virtual shadow page table", PassQueueSelection.AnyOfType(QueueType.Compute),
                    new VirtualShadowPageTableCopyData(_framePageTable, _framePreviousPageTable, 0, false),
                    new PassOptions(Culling: PassCullingMode.NeverCull),
                    VirtualShadowPageTableCopyData.Declare, VirtualShadowPageTableCopyData.Record);
                edit.OrderAfter(_requestResetPass, _frameResetPass);
                edit.OrderAfter(_previousPageTableCopyPass, _requestResetPass);
                edit.OrderAfter(_previousPageTableClearPass, _requestResetPass);
                foreach (GraphPassId consumer in new[] { _initializePass, _invalidatePass, _coarseMarkPass, _markPass })
                {
                    edit.OrderAfter(consumer, _previousPageTableCopyPass);
                    edit.OrderAfter(consumer, _previousPageTableClearPass);
                }
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
            shadowMap?.Dispose();
            throw;
        }
    }

    public VirtualShadowMap ShadowMap => _shadowMap;

    public int ShadowViewCount => _viewCount;

    private uint ActiveFinePageTableEntryCount => checked(
        (uint)_viewCount * _shadowMap.Settings.VirtualPagesPerView);

    private uint ActivePageTableEntryCount => checked(
        ActiveFinePageTableEntryCount +
        (uint)_viewCount * _shadowMap.Settings.CoarsePagesPerView);

    private void BindRasterInvocationViews(
        ref RenderGraphEdit graph,
        ref PassDefinition pass)
    {
        _ = pass.Bind(
            graph.CreateBufferSrv(
                _rasterInvocationViews,
                structureStride: 432,
                label: "Virtual shadow raster invocation views"),
            PipelineSync.AllShading,
            BufferContentScope.CountBoundedPrefix);
    }

    private void BindClusterSourceViews(
        ref RenderGraphFrame graph,
        GraphBufferSlice sourceViews)
    {
        graph.SetBufferInput(_clusterSourceViews, sourceViews);
    }

    private void BindRasterTargetInvocations(
        ref RenderGraphEdit graph,
        ref PassDefinition pass)
    {
        _ = pass.Bind(
            graph.CreateBufferSrv(
                _rasterTargetInvocations,
                structureStride: 48,
                label: "Virtual shadow raster regions"),
            PipelineSync.AllShading,
            BufferContentScope.CountBoundedPrefix);
    }

    private void BindRasterPolicyFilterResources(
        ref RenderGraphEdit graph,
        ref PassDefinition pass)
    {
        BindRasterTargetInvocations(ref graph, ref pass);
        _ = pass.Bind(
            graph.CreateTextureSrv(
                _pageHiZ,
                format: Format.R32Float,
                dimension: SomeEngine.Graphics.TextureViewDimension.Texture2DArray,
                label: "Virtual shadow current raster-target HiZ"),
            PipelineSync.ComputeShading);
        _ = pass.Bind(
            graph.CreateTextureSrv(
                _pageHiZ,
                format: Format.R32Float,
                dimension: SomeEngine.Graphics.TextureViewDimension.Texture2DArray,
                label: "Virtual shadow previous raster-target HiZ"),
            PipelineSync.ComputeShading);
        _ = pass.Bind(graph.CreateBufferSrv(_rasterRegionGrid, structureStride: sizeof(uint),
            label: "Virtual shadow dirty region grid"), PipelineSync.ComputeShading);
    }

    private void BindPageDepthAtlas(
        ref RenderGraphEdit graph,
        ref PassDefinition pass)
    {
        _ = pass.Bind(
            graph.CreateTextureUav(
                _atlas,
                format: Format.R32UInt,
                dimension: SomeEngine.Graphics.TextureViewDimension.Texture2D,
                label: "Virtual shadow raster depth atlas"),
            PipelineSync.AllShading,
            GraphAccessMode.ReadWrite,
            WriteCoverage.Partial,
            unorderedAccessHazards: UnorderedAccessHazardMode.AtomicMergeable);
    }

    public void StageFrame(
        ref RenderGraphFrame graph,
        RenderLightSet lights,
        in RenderView receiverView)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(lights);
        if (_frameActive)
            throw new InvalidOperationException("The previous virtual-shadow frame is still active.");
        if (!_shadowMap.Settings.Enabled)
            throw new InvalidOperationException("The virtual-shadow feature is disabled by its settings.");

        int instanceCount = _geometry.CurrentInstanceCount;
        if (instanceCount <= 0)
            throw new InvalidOperationException("Virtual shadows require at least one render instance.");
        _frameInstances = _geometry.CurrentInstances;
        _frameInstanceRevision = _frameInstances!.ContentRevision;
        _forceStateReset |= !ReferenceEquals(_frameInstances, _committedInstances) || _invalidateAllCasters;
        _changedInstances.Clear();
        _frameInstances.CollectChangedInstances(_committedInstanceRevision, _changedInstances);
        _changedInstances.AddRange(_deformedInstances);
        _changedInstances.Sort();
        int unique = 0;
        for (int index = 0; index < _changedInstances.Count; index++)
            if (unique == 0 || _changedInstances[index] != _changedInstances[unique - 1])
                _changedInstances[unique++] = _changedInstances[index];
        _changedInstances.RemoveRange(unique, _changedInstances.Count - unique);
        _forceStateReset |= _changedInstances.Count != 0 && !_geometry.HasFiniteGeometryBounds;
        _shadowMap.BeginFrame();
        _shadowFrameActive = true;
        try
        {
            ReadOnlySpan<VirtualShadowView> views = _shadowMap.PrepareViews(
                lights,
                receiverView,
                _geometry.GeometryRevision,
                _geometry.RasterRevision,
                instanceCount);
            _viewCount = views.Length;
            if (_viewCount == 0)
                throw new InvalidOperationException("Virtual shadows require at least one shadowable light view.");
            VirtualShadowMapFrameResources resources =
                _shadowMap.AcquireFrameResources(out SomeEngine.Graphics.Buffer? retiredPageTable);
            if (retiredPageTable is not null)
                graph.RetireAfterSubmittedFrames(retiredPageTable);
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
                !_forceStateReset &&
                _shadowMap.TryCopyCommittedViews(
                    _previousShadowViews,
                    _previousShadowViewProjections);
            _frameHasPreviousHistory = hasPreviousTargetHistory;

            uint activeTableEntryCount = checked((uint)_viewCount *
                (_shadowMap.Settings.VirtualPagesPerView +
                 _shadowMap.Settings.CoarsePagesPerView));
            graph.SetBufferInput(_requestedPages, new GraphBufferSlice(graph.CreateBuffer(new BufferDesc(
                checked((ulong)activeTableEntryCount * sizeof(uint)),
                BufferUsages.ShaderRead | BufferUsages.ShaderWrite |
                    BufferUsages.CopyDestination,
                "Virtual shadow active-range request flags")), BufferRange.Whole));
            ulong previousPageTableBytes = checked(
                (ulong)activeTableEntryCount * VirtualShadowView.PageTableEntrySizeInBytes);
            graph.SetBufferInput(_framePreviousPageTable, new GraphBufferSlice(graph.CreateBuffer(new BufferDesc(
                previousPageTableBytes,
                BufferUsages.ShaderRead | BufferUsages.CopyDestination,
                "Virtual shadow active-range previous page table")), BufferRange.Whole));
            graph.SetPassData(_previousPageTableCopyPass,
                new VirtualShadowPageTableCopyData(_framePageTable, _framePreviousPageTable, previousPageTableBytes, true));
            graph.SetPassData(_previousPageTableClearPass,
                new VirtualShadowPageTableCopyData(_framePageTable, _framePreviousPageTable, previousPageTableBytes, false));
            graph.SetPassEnabled(_previousPageTableCopyPass, hasPreviousTargetHistory);
            graph.SetPassEnabled(_previousPageTableClearPass, !hasPreviousTargetHistory);
            graph.SetPassEnabled(_requestResetPass, true);
            ClusterPipelineOutputs geometry = _geometry.Outputs;
            graph.SetBufferInput(_casterBounds, new GraphBufferSlice(geometry.Geometry.InstanceWorldBounds, BufferRange.Whole));
            graph.SetTextureInput(_receiverDepth, geometry.Depth);
            graph.OrderAfter(_invalidatePass, geometry.Geometry.BoundsReady);

            graph.SetBufferInput(_frameShadowViews, graph.Upload(
                views,
                BufferUsages.ShaderRead,
                "Virtual shadow view parameters"));
            graph.SetBufferInput(_frameChangedInstances, graph.Upload<uint>(
                _changedInstances.Count != 0 ? CollectionsMarshal.AsSpan(_changedInstances) : [0u],
                BufferUsages.ShaderRead,
                "Virtual shadow changed caster indices"));
            graph.SetBufferInput(_framePreviousShadowViews, graph.Upload(
                hasPreviousTargetHistory
                    ? _previousShadowViews.AsSpan(0, _viewCount)
                    : views,
                BufferUsages.ShaderRead,
                "Virtual shadow previous view parameters"));
            var sampling = new VirtualShadowSamplingUniforms
            {
                ViewCount = checked((uint)_viewCount),
                DirectionalLightCount = _shadowMap.PendingDirectionalLightCount,
                DirectionalClipmapLevels = _shadowMap.Settings.DirectionalClipmapLevels,
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
                NormalBias = _shadowMap.Settings.NormalBias,
            };
            graph.SetBufferInput(_frameSamplingUniforms, FeatureUploads.UploadConstant(
                ref graph,
                in sampling,
                "Virtual shadow sampling uniforms"));
            _geometry.StageViews([receiverView]);
            int copied = _shadowMap.CopyPreparedRenderViews(_shadowViews);
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
                checked((uint)_viewCount));
            _frameActive = true;
        }
        catch
        {
            if (_shadowFrameActive)
            {
                _shadowMap.Discard();
                _shadowFrameActive = false;
            }
            _viewCount = 0;

            _frameHasPreviousHistory = false;
            throw;
        }
    }

    public void Commit(ReadOnlySpan<QueueCompletion> completions)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_frameActive)
            throw new InvalidOperationException("No virtual-shadow frame is active.");
        if (_shadowFrameActive)
            _shadowMap.Commit(completions);
        _committedInstances = _frameInstances;
        _committedInstanceRevision = _frameInstanceRevision;
        _deformedInstances.Clear();
        _invalidateAllCasters = false;
        _forceStateReset = false;
        ClearFrame();
    }

    public void Discard()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_shadowFrameActive)
            _shadowMap.Discard();
        ClearFrame();
    }

    public bool TryResolve(
        string name,
        ResourceBindingType type,
        out ClusterStageResourceBinder binder)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        binder = name switch
        {
            "RasterTargetInvocations" when type == ResourceBindingType.BufferSrv => BindRasterTargetInvocations,
            "RasterTargetOutput" when type == ResourceBindingType.TextureUav => BindPageDepthAtlas,
            "VirtualShadowViews" when type == ResourceBindingType.BufferSrv => BindShadeViews,
            "VirtualShadowPageTable" when type == ResourceBindingType.BufferSrv => BindShadePageTable,
            "VirtualShadowAtlas" when type == ResourceBindingType.TextureSrv => BindShadeAtlas,
            "VirtualShadowSampling" when type == ResourceBindingType.ConstantBuffer => BindShadeSampling,
            _ => null!,
        };
        return binder is not null;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        if (_shadowFrameActive)
            _shadowMap.Discard();
        _dispatchLayout.Dispose();
        _shadowMap.Dispose();
        Array.Clear(_shadowViews);

        _frameHasPreviousHistory = false;
        _viewCount = 0;
        _frameActive = false;
        _shadowFrameActive = false;
        _disposed = true;
    }

    private void UpdatePassData(
        ref RenderGraphFrame graph,
        RenderLightSet lights,
        in RenderView receiverView,
        in VirtualShadowMapFrameResources resources,
        int instanceCount,
        uint viewCount)
    {
        uint maxPages = _shadowMap.Settings.MaxPhysicalPages;
        uint fineTableEntries = checked(viewCount * _shadowMap.Settings.VirtualPagesPerView);
        uint tableEntries = checked(fineTableEntries +
            viewCount * _shadowMap.Settings.CoarsePagesPerView);
        uint pageGroups = DivideRoundUp(maxPages, 64u);
        uint initGroups = DivideRoundUp(
            Math.Max(maxPages, tableEntries),
            64u);
        uint instanceGroups = DivideRoundUp(checked((uint)instanceCount), 64u);
        bool resetState = resources.RequiresInitialization || _forceStateReset;

        var initUniforms = new VirtualShadowStateInitUniforms
        {
            PageTableEntryCount = tableEntries,
            MaxPhysicalPages = maxPages,
        };
        GraphBufferSlice initUniformBuffer = FeatureUploads.UploadConstant(
            ref graph,
            in initUniforms,
            "Virtual shadow state init uniforms");
        graph.SetBufferInput(_initUniforms, initUniformBuffer);
        graph.SetPassData(
            _initializePass,
            new VirtualShadowDispatchPassData(new DispatchArguments(initGroups, 1, 1)));
        graph.SetPassEnabled(_initializePass, resetState);
        graph.SetPassEnabled(_atlasInitializePass, resetState);
        graph.SetPassEnabled(_pageHiZStorageInitializePass, resetState);

        var invalidationUniforms = new VirtualShadowInvalidationUniforms
        {
            ChangedInstanceCount = checked((uint)_changedInstances.Count),
            ViewCount = viewCount,
            PageTableEntryCount = tableEntries,
            PageDilation = checked((uint)Math.Ceiling(
                Math.Max(
                    _shadowMap.Settings.BlockerSearchRadiusTexels,
                    _shadowMap.Settings.MaxFilterRadiusTexels) /
                _shadowMap.Settings.PageSize)),
        };
        GraphBufferSlice invalidationUniformBuffer = FeatureUploads.UploadConstant(
            ref graph,
            in invalidationUniforms,
            "Virtual shadow invalidation uniforms");
        graph.SetBufferInput(_invalidationUniforms, invalidationUniformBuffer);
        graph.SetPassData(
            _invalidatePass,
            new VirtualShadowDispatchPassData(new DispatchArguments(
                Math.Min(checked((uint)_changedInstances.Count), 65535u),
                DivideRoundUp(checked((uint)_changedInstances.Count), 65535u), viewCount)));
        graph.SetPassEnabled(
            _invalidatePass,
            !resetState &&
            _changedInstances.Count != 0 &&
            viewCount != 0u);

        var coarseDemand = new VirtualShadowCoarsePageDemandUniforms
        {
            ViewCount = viewCount,
            PageTableEntryCount = tableEntries,
            FrameIndex = _frameIndex,
        };
        GraphBufferSlice coarseDemandBuffer = FeatureUploads.UploadConstant(
            ref graph,
            in coarseDemand,
            "Virtual shadow coarse page demand uniforms");
        graph.SetBufferInput(_coarseDemandUniforms, coarseDemandBuffer);
        graph.SetPassData(
            _coarseMarkPass,
            new VirtualShadowDispatchPassData(new DispatchArguments(
                DivideRoundUp(viewCount, 64u),
                1,
                1)));
        graph.SetPassEnabled(_coarseMarkPass, viewCount != 0u);

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
            DirectionalClipmapLevels = _shadowMap.Settings.DirectionalClipmapLevels,
            PointLightCount = _shadowMap.PendingPointLightCount,
            PointLightBufferOffset = checked((uint)lights.Directional.Count),
            PointViewOffset = _shadowMap.PendingPointViewOffset,
            SpotLightCount = _shadowMap.PendingSpotLightCount,
            SpotLightBufferOffset = checked((uint)(
                lights.Directional.Count + lights.Points.Count)),
            SpotViewOffset = _shadowMap.PendingSpotViewOffset,
            ReceiverViewIndex = 0u,
            FrameIndex = _frameIndex,
            PageDilationBorderPages = (float)(
                Math.Max(
                    _shadowMap.Settings.BlockerSearchRadiusTexels,
                    _shadowMap.Settings.MaxFilterRadiusTexels) /
                _shadowMap.Settings.PageSize),
        };
        GraphBufferSlice demandBuffer = FeatureUploads.UploadConstant(
            ref graph,
            in demand,
            "Virtual shadow page demand uniforms");
        graph.SetBufferInput(_demandUniforms, demandBuffer);
        graph.SetPassData(
            _markPass,
            new VirtualShadowDispatchPassData(new DispatchArguments(
                DivideRoundUp(receiverView.ViewportWidth, 8u),
                DivideRoundUp(receiverView.ViewportHeight, 8u),
                1)));
        graph.SetPassEnabled(_markPass, viewCount != 0u);

        UploadPoolUniforms(ref graph);
        graph.SetPassData(
            _poolPass,
            new VirtualShadowDispatchPassData(new DispatchArguments(pageGroups, 1, 1)));
        graph.SetPassEnabled(_poolPass, viewCount != 0u);
        graph.SetPassEnabled(_poolResetPass, viewCount != 0u);

        UploadAllocationUniforms(ref graph);
        graph.SetPassData(
            _allocatePass,
            new VirtualShadowDispatchPassData(new DispatchArguments(
                DivideRoundUp(tableEntries, 64u),
                1,
                1)));
        graph.SetPassEnabled(_allocatePass, viewCount != 0u);

        var clearUniforms = new VirtualShadowPageClearUniforms
        {
            PageSize = _shadowMap.Settings.PageSize,
            PhysicalPagesPerRow = _shadowMap.Settings.PhysicalPagesPerRow,
            AtlasSize = _shadowMap.Settings.AtlasSize,
        };
        GraphBufferSlice clearUniformBuffer = FeatureUploads.UploadConstant(
            ref graph,
            in clearUniforms,
            "Virtual shadow clear uniforms");
        graph.SetBufferInput(_clearUniforms, clearUniformBuffer);
        graph.SetPassEnabled(_clearPass, viewCount != 0u);

        var pageHiZInitializeUniforms = new VirtualShadowPageHiZUniforms
        {
            PageSize = _shadowMap.Settings.PageSize,
            PhysicalPagesPerRow = _shadowMap.Settings.PhysicalPagesPerRow,
        };
        GraphBufferSlice pageHiZInitializeUniformBuffer = FeatureUploads.UploadConstant(
            ref graph,
            in pageHiZInitializeUniforms,
            "Virtual shadow page HiZ initialization uniforms");
        graph.SetBufferInput(_pageHiZInitializeUniforms, pageHiZInitializeUniformBuffer);
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
            };
            GraphBufferSlice pageHiZReduceUniformBuffer = FeatureUploads.UploadConstant(
                ref graph,
                in pageHiZReduceUniforms,
                $"Virtual shadow page HiZ mip {mip} uniforms");
            graph.SetBufferInput(_pageHiZReduceUniforms[index], pageHiZReduceUniformBuffer);
            graph.SetPassEnabled(_pageHiZReducePasses[index], viewCount != 0u);
            graph.SetPassEnabled(
                _finalPageHiZReducePasses[index],
                viewCount != 0u);
        }

        var compactUniforms = new VirtualShadowViewCompactUniforms
        {
            ViewCount = viewCount,
            ActivePageCapacity = _shadowMap.Settings.MaxPhysicalPages,
            PageSize = _shadowMap.Settings.PageSize,
            PageHiZMipCount = checked((uint)(
                BitOperations.Log2(Math.Max(_shadowMap.Settings.PageSize / 2u, 1u)) + 1)),
            PhysicalPagesPerRow = _shadowMap.Settings.PhysicalPagesPerRow,
            InstanceGroupCount = DivideRoundUp(checked((uint)instanceCount), 64u),
            HasPreviousHistory = _frameHasPreviousHistory ? 1u : 0u,
        };
        graph.SetBufferInput(_frameInvocationUniforms, FeatureUploads.UploadConstant(
            ref graph,
            in compactUniforms,
            "Virtual shadow raster-invocation uniforms"));
        graph.SetPassData(_compactViewsPass,
            new VirtualShadowDispatchPassData(new DispatchArguments(
                DivideRoundUp(Math.Max(_shadowMap.Settings.MaxPhysicalPages, viewCount * 2u), 64u), 1u, 1u)));
        graph.SetPassEnabled(_compactViewsPass, true);

        graph.SetPassEnabled(_rasterCompletePass, viewCount != 0u);
        graph.SetPassEnabled(_frameCompletePass, viewCount != 0u);
        graph.SetPassEnabled(_frameResetPass, true);

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
        graph.BindExternalBuffer(_freePages, resources.FreePages.Resource, resources.FreePages.BoundaryStates.Span);
        graph.BindExternalBuffer(
            _freePageCount,
            resources.FreePageCount.Resource,
            resources.FreePageCount.BoundaryStates.Span);
        graph.BindExternalBuffer(
            _pageMetadata,
            resources.PageMetadata.Resource,
            resources.PageMetadata.BoundaryStates.Span);
        graph.BindExternalBuffer(
            _evictionPages,
            resources.EvictionPages.Resource,
            resources.EvictionPages.BoundaryStates.Span);
        graph.BindExternalBuffer(
            _evictionBucketCounts,
            resources.EvictionBucketCounts.Resource,
            resources.EvictionBucketCounts.BoundaryStates.Span);
    }

    private void UploadPoolUniforms(ref RenderGraphFrame graph)
    {
        var uniforms = new VirtualShadowPagePoolUniforms
        {
            MaxPhysicalPages = _shadowMap.Settings.MaxPhysicalPages,
            PageTableEntryCount = ActivePageTableEntryCount,
            FinePageTableEntryCount = ActiveFinePageTableEntryCount,
            FrameIndex = _frameIndex,
            EvictionBucketCount = EvictionBucketCount,
        };
        GraphBufferSlice uniformBuffer = FeatureUploads.UploadConstant(
            ref graph,
            in uniforms,
            "Virtual shadow page pool uniforms");
        graph.SetBufferInput(_poolUniforms, uniformBuffer);
    }

    private void UploadAllocationUniforms(ref RenderGraphFrame graph)
    {
        var uniforms = new VirtualShadowPageAllocationUniforms
        {
            PageTableEntryCount = ActivePageTableEntryCount,
            MaxPhysicalPages = _shadowMap.Settings.MaxPhysicalPages,
            FinePageTableEntryCount = ActiveFinePageTableEntryCount,
            VirtualPagesPerView = _shadowMap.Settings.VirtualPagesPerView,
            CoarsePagesPerView = _shadowMap.Settings.CoarsePagesPerView,
            ViewCount = checked((uint)_viewCount),
            FrameIndex = _frameIndex,
            EvictionBucketCount = EvictionBucketCount,
        };
        GraphBufferSlice uniformBuffer = FeatureUploads.UploadConstant(
            ref graph,
            in uniforms,
            "Virtual shadow allocation uniforms");
        graph.SetBufferInput(_allocationUniforms, uniformBuffer);
    }

    private void BindInitialize(ref RenderGraphEdit graph, GraphBufferInput uniforms)
    {
        PassDefinition pass = graph.GetPass(_initializePass);
        _ = pass.Bind(uniforms, PipelineSync.ComputeShading);
        _ = pass.Bind(graph.CreateBufferUav(_framePageTable, structureStride: 16, label: "Virtual shadow init page table"),
            PipelineSync.ComputeShading, GraphAccessMode.Write, WriteCoverage.Complete);
        _ = pass.Bind(graph.CreateBufferUav(_pageOwners, structureStride: sizeof(uint), label: "Virtual shadow init owners"),
            PipelineSync.ComputeShading, GraphAccessMode.Write, WriteCoverage.Complete);
        _ = pass.Bind(graph.CreateBufferUav(_pageMetadata, structureStride: 16, label: "Virtual shadow init metadata"),
            PipelineSync.ComputeShading, GraphAccessMode.Write, WriteCoverage.Complete);
        _ = pass.Bind(graph.CreateBufferUav(_requestedPages, structureStride: sizeof(uint),
            label: "Virtual shadow init request epochs"), PipelineSync.ComputeShading,
            GraphAccessMode.Write, WriteCoverage.Complete);
    }

    private void BindInvalidation(ref RenderGraphEdit graph, GraphBufferInput uniforms)
    {
        PassDefinition pass = graph.GetPass(_invalidatePass);
        _ = pass.Bind(uniforms, PipelineSync.ComputeShading);
        _ = pass.Bind(graph.CreateBufferSrv(_frameShadowViews, structureStride: VirtualShadowView.SizeInBytes,
            label: "Virtual shadow invalidation views"), PipelineSync.ComputeShading);
        _ = pass.Bind(graph.CreateBufferSrv(_casterBounds, structureStride: 16, label: "Virtual shadow caster bounds"),
            PipelineSync.ComputeShading);
        _ = pass.Bind(graph.CreateBufferUav(_framePageTable, structureStride: 16,
            label: "Virtual shadow invalidation page table"), PipelineSync.ComputeShading,
            GraphAccessMode.ReadWrite, WriteCoverage.Partial);
        _ = pass.Bind(graph.CreateBufferSrv(_frameChangedInstances, structureStride: sizeof(uint),
            label: "Virtual shadow changed caster indices"), PipelineSync.ComputeShading);
    }

    private void BindMark(ref RenderGraphEdit graph, GraphBufferInput uniforms)
    {
        PassDefinition pass = graph.GetPass(_markPass);
        _ = pass.Bind(uniforms, PipelineSync.ComputeShading);
        _ = pass.Bind(graph.CreateBufferSrv(_frameShadowViews, structureStride: VirtualShadowView.SizeInBytes,
            label: "Virtual shadow demand views"), PipelineSync.ComputeShading);
        _lighting.BindVirtualShadowPageMarking(ref graph, ref pass);
        _ = pass.Bind(graph.CreateTextureSrv(
            _receiverDepth,
            new TextureSubresourceRange(0, 1, 0, 1, TextureAspects.Depth),
            Format.D32Float,
            SomeEngine.Graphics.TextureViewDimension.Texture2DArray,
            label: "Virtual shadow receiver depth"), PipelineSync.ComputeShading);
        _ = pass.Bind(graph.CreateBufferUav(_requestedPages, structureStride: sizeof(uint),
            label: "Virtual shadow request flags"), PipelineSync.ComputeShading,
            GraphAccessMode.ReadWrite, WriteCoverage.Partial);
    }

    private void BindCoarseMark(ref RenderGraphEdit graph, GraphBufferInput uniforms)
    {
        PassDefinition pass = graph.GetPass(_coarseMarkPass);
        _ = pass.Bind(uniforms, PipelineSync.ComputeShading);
        _ = pass.Bind(graph.CreateBufferSrv(
            _frameShadowViews,
            structureStride: VirtualShadowView.SizeInBytes,
            label: "Virtual shadow coarse demand views"), PipelineSync.ComputeShading);
        _ = pass.Bind(graph.CreateBufferUav(
            _requestedPages,
            structureStride: sizeof(uint),
            label: "Virtual shadow coarse request flags"), PipelineSync.ComputeShading,
            GraphAccessMode.Write, WriteCoverage.Partial);
    }

    private void BindPool(
        ref RenderGraphEdit graph,
        GraphPassId passId, GraphBufferInput uniformBuffer)
    {
        PassDefinition pass = graph.GetPass(passId);
        _ = pass.Bind(uniformBuffer, PipelineSync.ComputeShading);
        _ = pass.Bind(graph.CreateBufferSrv(_requestedPages, structureStride: sizeof(uint),
            label: "Virtual shadow pool request flags"), PipelineSync.ComputeShading);
        _ = pass.Bind(graph.CreateBufferUav(_pageOwners, structureStride: sizeof(uint),
            label: "Virtual shadow pool owners"), PipelineSync.ComputeShading,
            GraphAccessMode.ReadWrite, WriteCoverage.Partial);
        _ = pass.Bind(graph.CreateBufferUav(_pageMetadata, structureStride: 16,
            label: "Virtual shadow pool metadata"), PipelineSync.ComputeShading,
            GraphAccessMode.ReadWrite, WriteCoverage.Partial);
        _ = pass.Bind(graph.CreateBufferUav(_freePages, structureStride: sizeof(uint),
            label: "Virtual shadow free pages"), PipelineSync.ComputeShading,
            GraphAccessMode.Write, WriteCoverage.Partial,
            resultContents: ResourceContentState.Defined,
            contentScope: BufferContentScope.CountBoundedPrefix);
        _ = pass.Bind(graph.CreateBufferUav(_freePageCount, structureStride: sizeof(uint),
            label: "Virtual shadow free count"), PipelineSync.ComputeShading,
            GraphAccessMode.ReadWrite, WriteCoverage.Partial);
        _ = pass.Bind(graph.CreateBufferUav(_evictionPages, structureStride: sizeof(uint),
            label: "Virtual shadow eviction pages"), PipelineSync.ComputeShading,
            GraphAccessMode.Write, WriteCoverage.Partial,
            resultContents: ResourceContentState.Defined,
            contentScope: BufferContentScope.CountBoundedPrefix);
        _ = pass.Bind(graph.CreateBufferUav(_evictionBucketCounts, structureStride: sizeof(uint),
            label: "Virtual shadow eviction counts"), PipelineSync.ComputeShading,
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
        _ = pass.Bind(graph.CreateBufferSrv(_requestedPages, structureStride: sizeof(uint),
            label: "Virtual shadow requested-page flags"), PipelineSync.ComputeShading);
        _ = pass.Bind(graph.CreateBufferUav(_framePageTable, structureStride: 16,
            label: "Virtual shadow allocation page table"), PipelineSync.ComputeShading,
            GraphAccessMode.ReadWrite, WriteCoverage.Partial);
        _ = pass.Bind(graph.CreateBufferUav(_pageOwners, structureStride: sizeof(uint),
            label: "Virtual shadow allocation owners"), PipelineSync.ComputeShading,
            GraphAccessMode.ReadWrite, WriteCoverage.Partial);
        _ = pass.Bind(graph.CreateBufferUav(_pageMetadata, structureStride: 16,
            label: "Virtual shadow allocation metadata"), PipelineSync.ComputeShading,
            GraphAccessMode.ReadWrite, WriteCoverage.Partial);
        _ = pass.Bind(graph.CreateBufferUav(_freePages, structureStride: sizeof(uint),
            label: "Virtual shadow allocation free pages"), PipelineSync.ComputeShading,
            GraphAccessMode.ReadWrite, WriteCoverage.Partial,
            contentScope: BufferContentScope.CountBoundedPrefix);
        _ = pass.Bind(graph.CreateBufferUav(_freePageCount, structureStride: sizeof(uint),
            label: "Virtual shadow allocation free count"), PipelineSync.ComputeShading,
            GraphAccessMode.ReadWrite, WriteCoverage.Partial);
        _ = pass.Bind(graph.CreateBufferUav(_evictionPages, structureStride: sizeof(uint),
            label: "Virtual shadow allocation eviction pages"), PipelineSync.ComputeShading,
            GraphAccessMode.ReadWrite, WriteCoverage.Partial,
            contentScope: BufferContentScope.CountBoundedPrefix);
        _ = pass.Bind(graph.CreateBufferUav(_evictionBucketCounts, structureStride: sizeof(uint),
            label: "Virtual shadow allocation eviction counts"), PipelineSync.ComputeShading,
            GraphAccessMode.ReadWrite, WriteCoverage.Partial);
        _ = pass.Bind(graph.CreateBufferUav(_activePages, structureStride: 2 * sizeof(uint),
            label: "Virtual shadow active raster pages"), PipelineSync.ComputeShading,
            GraphAccessMode.Write, WriteCoverage.Partial,
            resultContents: ResourceContentState.Defined,
            contentScope: BufferContentScope.CountBoundedPrefix);
        _ = pass.Bind(graph.CreateBufferUav(_activePageCount, structureStride: sizeof(uint),
            label: "Virtual shadow active page count"), PipelineSync.ComputeShading,
            GraphAccessMode.ReadWrite, WriteCoverage.Partial);
        _ = pass.Bind(graph.CreateBufferUav(_allocationFailures, structureStride: sizeof(uint),
            label: "Virtual shadow allocation failures"), PipelineSync.ComputeShading,
            GraphAccessMode.ReadWrite, WriteCoverage.Partial);
    }

    private void BindClear(ref RenderGraphEdit graph, GraphBufferInput uniforms)
    {
        PassDefinition pass = graph.GetPass(_clearPass);
        _ = pass.Bind(uniforms, PipelineSync.ComputeShading);
        _ = pass.Bind(graph.CreateBufferSrv(_activePages, structureStride: 2 * sizeof(uint),
            label: "Virtual shadow active pages to clear"), PipelineSync.ComputeShading,
            BufferContentScope.CountBoundedPrefix);
        _ = pass.Bind(graph.CreateTextureUav(_atlas, format: Format.R32UInt,
            dimension: SomeEngine.Graphics.TextureViewDimension.Texture2D,
            label: "Virtual shadow atlas clear target"), PipelineSync.ComputeShading,
            GraphAccessMode.ReadWrite, WriteCoverage.Partial);
    }

    private void BindCompact(
        ref RenderGraphEdit graph,
        GraphBufferInput uniforms,
        GraphBufferInput sourceViews)
    {
        PassDefinition pass = graph.GetPass(_compactViewsPass);
        _ = pass.Bind(uniforms, PipelineSync.ComputeShading);
        _ = pass.Bind(graph.CreateBufferSrv(_activePages, structureStride: 2 * sizeof(uint),
            label: "Virtual shadow active raster pages"), PipelineSync.ComputeShading,
            BufferContentScope.CountBoundedPrefix);
        _ = pass.Bind(graph.CreateBufferSrv(_activePageCount, structureStride: sizeof(uint),
            label: "Virtual shadow active raster-page count"), PipelineSync.ComputeShading);
        _ = pass.Bind(graph.CreateBufferSrv(_frameShadowViews,
            structureStride: VirtualShadowView.SizeInBytes,
            label: "Virtual shadow current source views"), PipelineSync.ComputeShading);
        _ = pass.Bind(graph.CreateBufferSrv(_framePreviousShadowViews,
            structureStride: VirtualShadowView.SizeInBytes,
            label: "Virtual shadow previous source views"), PipelineSync.ComputeShading);
        _ = pass.Bind(graph.CreateBufferSrv(_framePreviousPageTable,
            structureStride: VirtualShadowView.PageTableEntrySizeInBytes,
            label: "Virtual shadow previous page table"), PipelineSync.ComputeShading);
        _ = pass.Bind(graph.CreateBufferSrv(sourceViews, structureStride: 432,
            label: "Virtual shadow source Cluster views"), PipelineSync.ComputeShading);
        _ = pass.Bind(graph.CreateBufferUav(_rasterInvocationViews, structureStride: 432,
            label: "Virtual shadow raster invocation views"), PipelineSync.ComputeShading,
            GraphAccessMode.Write, WriteCoverage.Partial,
            resultContents: ResourceContentState.Defined,
            contentScope: BufferContentScope.CountBoundedPrefix);
        _ = pass.Bind(graph.CreateBufferUav(_rasterTargetInvocations, structureStride: 48,
            label: "Virtual shadow raster target invocations"), PipelineSync.ComputeShading,
            GraphAccessMode.Write, WriteCoverage.Partial,
            resultContents: ResourceContentState.Defined,
            contentScope: BufferContentScope.CountBoundedPrefix);
        _ = pass.Bind(graph.CreateBufferUav(_rasterRegionGrid, structureStride: sizeof(uint),
            label: "Virtual shadow dirty region grid"), PipelineSync.ComputeShading,
            GraphAccessMode.ReadWrite, WriteCoverage.Partial);
        _ = pass.Bind(graph.CreateBufferUav(_clusterInvocationDispatchArguments,
            label: "Virtual shadow Cluster invocation arguments"), PipelineSync.ComputeShading,
            GraphAccessMode.Write, WriteCoverage.Complete);
        _ = pass.Bind(graph.CreateBufferUav(_pageWorkDispatchArguments,
            label: "Virtual shadow page-work dispatch arguments"), PipelineSync.ComputeShading,
            GraphAccessMode.Write, WriteCoverage.Complete);
    }

    private void BindRasterInvocationCount(
        ref RenderGraphEdit graph,
        ref PassDefinition pass)
    {
        _ = pass.Bind(
            graph.CreateBufferSrv(
                _clusterInvocationDispatchArguments,
                range: new BufferRange(12u, 4u),
                structureStride: sizeof(uint),
                label: "Virtual shadow raster invocation count"),
            PipelineSync.ComputeShading);
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
                structureStride: 2 * sizeof(uint),
                label: "Virtual shadow page HiZ active pages"),
            PipelineSync.ComputeShading,
            BufferContentScope.CountBoundedPrefix);
        _ = pass.Bind(
            graph.CreateTextureSrv(
                _atlas,
                format: Format.R32UInt,
                dimension: SomeEngine.Graphics.TextureViewDimension.Texture2D,
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
                structureStride: 2 * sizeof(uint),
                label: "Virtual shadow page HiZ active pages"),
            PipelineSync.ComputeShading,
            BufferContentScope.CountBoundedPrefix);
        _ = pass.Bind(
            graph.CreateTextureSrv(
                _pageHiZ,
                new TextureSubresourceRange(
                    sourceMip,
                    1,
                    0,
                    _shadowMap.Settings.MaxPhysicalPages,
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
                        0, _shadowMap.Settings.MaxPhysicalPages, TextureAspects.Color),
                    Format.R32Float,
                    SomeEngine.Graphics.TextureViewDimension.Texture2DArray,
                    "Virtual shadow page HiZ destination mip"),
                PipelineSync.ComputeShading, GraphAccessMode.Write, WriteCoverage.Partial);
        }
    }

    private void BindShadeViews(ref RenderGraphEdit graph, ref PassDefinition pass)
    {
        _ = pass.Bind(graph.CreateBufferSrv(_frameShadowViews, structureStride: VirtualShadowView.SizeInBytes,
            label: "Standard shade virtual shadow views"), PipelineSync.ComputeShading);
    }

    private void BindShadePageTable(ref RenderGraphEdit graph, ref PassDefinition pass)
    {
        _ = pass.Bind(graph.CreateBufferSrv(_framePageTable, structureStride: 16,
            label: "Standard shade virtual shadow page table"), PipelineSync.ComputeShading);
    }

    private void BindShadeAtlas(ref RenderGraphEdit graph, ref PassDefinition pass)
    {
        _ = pass.Bind(graph.CreateTextureSrv(_atlas, format: Format.R32UInt,
            dimension: SomeEngine.Graphics.TextureViewDimension.Texture2D,
            label: "Standard shade virtual shadow atlas"), PipelineSync.ComputeShading);
    }

    private void BindShadeSampling(ref RenderGraphEdit graph, ref PassDefinition pass)
    {
        _ = pass.Bind(_frameSamplingUniforms, PipelineSync.ComputeShading);
    }

    private GraphPassId AddComputePass(
        ref RenderGraphEdit edit,
        string label,
        LinkedComputePipeline pipeline,
        VirtualShadowDispatchPassData data) =>
        edit.AddComputeFramePass(
            label,
            PassQueueSelection.AnyOfType(QueueType.Compute),
            data,
            new PassOptions(Culling: PassCullingMode.NeverCull),
            pipeline.Pipeline,
            pipeline.ParameterLayout,
            default,
            static (ref PassDefinition _, ref VirtualShadowDispatchPassData _) => { },
            VirtualShadowDispatchPassData.Record);

    private GraphPassId AddIndirectPass(
        ref RenderGraphEdit edit,
        string label,
        LinkedComputePipeline pipeline,
        GraphBufferId arguments,
        ulong offset = 0u)
    {
        var data = new VirtualShadowIndirectPassData(_dispatchLayout, arguments, offset);
        // These passes update the exclusive atlas and HiZ between graphics raster passes.
        return edit.AddComputeFramePass(
            label,
            PassQueueSelection.AnyOfType(QueueType.Graphics),
            data,
            new PassOptions(Culling: PassCullingMode.NeverCull),
            pipeline.Pipeline,
            pipeline.ParameterLayout,
            default,
            VirtualShadowIndirectPassData.Declare,
            VirtualShadowIndirectPassData.Record);
    }

    private static GraphBufferId Counter(RenderGraphEdit edit, string label) =>
        edit.CreateBuffer(new BufferDesc(
            sizeof(uint),
            BufferUsages.ShaderRead | BufferUsages.ShaderWrite | BufferUsages.CopyDestination,
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
