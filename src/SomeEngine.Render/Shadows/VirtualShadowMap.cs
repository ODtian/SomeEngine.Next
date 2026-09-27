using System.Numerics;
using SomeEngine.Assets;
using SomeEngine.Assets.Schema;
using SomeEngine.Resources;
using SomeEngine.Graphics;
using SomeEngine.Render.Assets;
using SomeEngine.Render.Components;
using SomeEngine.Render.Lighting;
using SomeEngine.RenderGraph;
using Buffer = SomeEngine.Graphics.Buffer;
using Texture = SomeEngine.Graphics.Texture;

namespace SomeEngine.Render.Shadows;

/// <summary>
/// Owns persistent virtual-shadow page tables, physical-page allocation state, atlas storage and
/// cross-frame readiness. Geometry pipelines consume imported frame resources but never own them.
/// </summary>
public sealed class VirtualShadowMap : IDisposable
{
    private readonly IGraphicsBackend _backend;
    private readonly Device _device;
    private Buffer? _pageTable;
    private readonly Buffer _physicalPageOwners;
    private readonly Texture _atlas;
    private VirtualShadowPageCacheResources? _pageCache;
    private VirtualShadowPageCacheFrameResources? _pendingPageCacheResources;
    private BufferBoundaryState[] _pageTableEndpoint = [];
    private readonly BufferBoundaryState[] _physicalPageOwnersEndpoint;
    private readonly TextureBoundaryState[] _atlasEndpoint;
    private VirtualShadowView[] _views = [];
    private VirtualShadowView[] _committedViews = [];
    private Matrix4x4[] _preparedLightViews = [];
    private Matrix4x4[] _preparedLightProjections = [];
    private bool _initialized;
    private bool _pageTableInitialized;
    private bool _pending;
    private bool _disposed;
    private bool _pendingCacheReset;
    private int _committedViewCount;
    private int _pendingViewCount;
    private int _committedInstanceCount;
    private int _pendingInstanceCount;
    private ulong _committedGeometryRevision;
    private ulong _pendingGeometryRevision;
    private ulong _committedRasterRevision;
    private ulong _pendingRasterRevision;
    private uint _committedCacheGeneration;
    private uint _pendingCacheGeneration;

    public VirtualShadowMap(
        IGraphicsBackend backend,
        Device device,
        VirtualShadowMapShaders shaders,
        VirtualShadowMapSettings settings)
    {
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        _device = device ?? throw new ArgumentNullException(nameof(device));
        ArgumentNullException.ThrowIfNull(shaders);
        Settings = settings ?? throw new ArgumentNullException(nameof(settings));
        settings.Validate();
        LinkedComputePipeline? markPages = null;
        LinkedComputePipeline? markCoarsePages = null;
        LinkedComputePipeline? allocatePages = null;
        LinkedComputePipeline? clearPages = null;
        LinkedComputePipeline? initializeState = null;
        LinkedComputePipeline? invalidateCasters = null;
        LinkedComputePipeline? buildPagePool = null;
        LinkedComputePipeline? compactViews = null;
        LinkedComputePipeline? initializePageHiZ = null;
        LinkedComputePipeline? reducePageHiZ = null;
        Buffer? physicalPageOwners = null;
        Texture? atlas = null;
        try
        {
            markPages = LinkedComputePipeline.Create(
                _backend,
                _device,
                shaders.MarkPages,
                "Virtual shadow receiver page marking");
            markCoarsePages = LinkedComputePipeline.Create(
                _backend,
                _device,
                shaders.MarkCoarsePages,
                "Virtual shadow coarse page marking");
            allocatePages = LinkedComputePipeline.Create(
                _backend,
                _device,
                shaders.AllocatePages,
                "Virtual shadow physical-page allocation");
            clearPages = LinkedComputePipeline.Create(
                _backend,
                _device,
                shaders.ClearPages,
                "Virtual shadow active-page clear");
            initializeState = LinkedComputePipeline.Create(
                _backend,
                _device,
                shaders.InitializeState,
                "Virtual shadow state initialization");
            invalidateCasters = LinkedComputePipeline.Create(
                _backend,
                _device,
                shaders.InvalidateCasters,
                "Virtual shadow caster invalidation");
            buildPagePool = LinkedComputePipeline.Create(
                _backend,
                _device,
                shaders.BuildPagePool,
                "Virtual shadow page-pool build");
            compactViews = LinkedComputePipeline.Create(
                _backend,
                _device,
                shaders.CompactViews,
                "Virtual shadow active-view compaction");
            initializePageHiZ = LinkedComputePipeline.Create(
                _backend,
                _device,
                shaders.InitializePageHiZ,
                "Virtual shadow physical-page HiZ initialization");
            reducePageHiZ = LinkedComputePipeline.Create(
                _backend,
                _device,
                shaders.ReducePageHiZ,
                "Virtual shadow physical-page HiZ reduction");
            physicalPageOwners = _backend.CreateBuffer(
                _device,
                new BufferDesc(
                    checked((ulong)settings.MaxPhysicalPages * sizeof(uint)),
                    BufferUsages.ShaderRead | BufferUsages.ShaderWrite |
                        BufferUsages.CopyDestination,
                    "Virtual shadow physical-page owners"),
                MemoryType.DeviceLocal);
            atlas = _backend.CreateTexture(
                _device,
                new TextureDesc(
                    TextureDimension.Texture2D,
                    settings.AtlasSize,
                    settings.AtlasSize,
                    1,
                    1,
                    1,
                    1,
                    Format.R32UInt,
                    TextureUsages.Sampled | TextureUsages.Storage | TextureUsages.ColorAttachment,
                    label: "Virtual shadow physical atlas"));
        }
        catch (Exception primary)
        {
            List<Exception>? cleanupFailures = null;
            if (atlas is not null) TryDispose(atlas, ref cleanupFailures);
            if (physicalPageOwners is not null)
                TryDispose(physicalPageOwners, ref cleanupFailures);
            if (clearPages is not null) TryDispose(clearPages, ref cleanupFailures);
            if (compactViews is not null) TryDispose(compactViews, ref cleanupFailures);
            if (reducePageHiZ is not null) TryDispose(reducePageHiZ, ref cleanupFailures);
            if (initializePageHiZ is not null) TryDispose(initializePageHiZ, ref cleanupFailures);
            if (buildPagePool is not null) TryDispose(buildPagePool, ref cleanupFailures);
            if (invalidateCasters is not null) TryDispose(invalidateCasters, ref cleanupFailures);
            if (initializeState is not null) TryDispose(initializeState, ref cleanupFailures);
            if (allocatePages is not null) TryDispose(allocatePages, ref cleanupFailures);
            if (markCoarsePages is not null) TryDispose(markCoarsePages, ref cleanupFailures);
            if (markPages is not null) TryDispose(markPages, ref cleanupFailures);
            if (cleanupFailures is not null)
            {
                cleanupFailures.Insert(0, primary);
                throw new AggregateException(
                    "Virtual-shadow construction failed and cleanup also reported failures.",
                    cleanupFailures);
            }
            throw;
        }

        PageMarkPipeline = markPages;
        CoarsePageMarkPipeline = markCoarsePages;
        PageAllocatePipeline = allocatePages;
        PageClearPipeline = clearPages;
        StateInitializePipeline = initializeState;
        CasterInvalidationPipeline = invalidateCasters;
        PagePoolPipeline = buildPagePool;
        ViewCompactPipeline = compactViews;
        PageHiZInitializePipeline = initializePageHiZ;
        PageHiZReducePipeline = reducePageHiZ;
        _physicalPageOwners = physicalPageOwners;
        _atlas = atlas;

        _physicalPageOwnersEndpoint = [Initial(_physicalPageOwners)];
        _atlasEndpoint = [Initial(_atlas)];
        try
        {
            _pageCache = new VirtualShadowPageCacheResources(
                _backend,
                _device,
                settings);
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public VirtualShadowMapSettings Settings { get; }
    public LinkedComputePipeline PageMarkPipeline { get; }
    public LinkedComputePipeline CoarsePageMarkPipeline { get; }
    public LinkedComputePipeline PageAllocatePipeline { get; }
    public LinkedComputePipeline PageClearPipeline { get; }
    public LinkedComputePipeline StateInitializePipeline { get; }
    public LinkedComputePipeline CasterInvalidationPipeline { get; }
    public LinkedComputePipeline PagePoolPipeline { get; }
    public LinkedComputePipeline ViewCompactPipeline { get; }
    public LinkedComputePipeline PageHiZInitializePipeline { get; }
    public LinkedComputePipeline PageHiZReducePipeline { get; }
    public bool RequiresInitialization => !_pageTableInitialized;
    public bool RequiresCacheReset => _pendingCacheReset;
    public bool UsesPageGranularTransformInvalidation =>
        _initialized && _pendingViewCount != 0 && !_pendingCacheReset;
    public uint PendingCacheGeneration => _pendingCacheGeneration;
    public ulong PendingGeometryRevision => _pendingGeometryRevision;
    public ulong PendingRasterRevision => _pendingRasterRevision;
    public uint PendingDirectionalLightCount { get; private set; }
    public uint PendingPointLightCount { get; private set; }
    public uint PendingSpotLightCount { get; private set; }
    public uint PendingPointViewOffset { get; private set; }
    public uint PendingSpotViewOffset { get; private set; }

    public ReadOnlySpan<VirtualShadowView> PrepareViews(
        RenderLightSet lights,
        in RenderView receiverView,
        ulong geometryRevision,
        ulong rasterRevision,
        int instanceCount)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(lights);
        if (!_pending)
            throw new InvalidOperationException(
                "Import the virtual-shadow resources before preparing frame views.");
        int remainingLights = checked((int)Settings.MaxShadowLights);
        int directionalLightCount = Math.Min(lights.Directional.Count, remainingLights);
        remainingLights -= directionalLightCount;
        int pointLightCount = Math.Min(lights.Points.Count, remainingLights);
        remainingLights -= pointLightCount;
        int spotLightCount = Math.Min(lights.Spots.Count, remainingLights);
        int clipmapLevels = checked((int)Settings.DirectionalClipmapLevels);
        int directionalViewCount = checked(directionalLightCount * clipmapLevels);
        int pointViewOffset = directionalViewCount;
        int pointViewCount = checked(pointLightCount * 6);
        int spotViewOffset = checked(pointViewOffset + pointViewCount);
        int viewCount = checked(spotViewOffset + spotLightCount);
        EnsurePendingViewStorage(viewCount);
        PendingDirectionalLightCount = checked((uint)directionalLightCount);
        PendingPointLightCount = checked((uint)pointLightCount);
        PendingSpotLightCount = checked((uint)spotLightCount);
        PendingPointViewOffset = checked((uint)pointViewOffset);
        PendingSpotViewOffset = checked((uint)spotViewOffset);
        _pendingViewCount = viewCount;
        _pendingGeometryRevision = geometryRevision;
        _pendingRasterRevision = rasterRevision;
        _pendingInstanceCount = instanceCount;
        if (viewCount == 0)
        {
            VirtualShadowCacheDecision decision = ResolveCacheDecision(
                _initialized,
                viewChanged: _committedViewCount != 0,
                structuralContentChanged: false,
                viewCount,
                _committedCacheGeneration);
            _pendingCacheReset = decision.ResetCache;
            _pendingCacheGeneration = decision.CacheGeneration;
            return [];
        }
        if (!Matrix4x4.Invert(receiverView.View, out Matrix4x4 worldFromView))
            throw new ArgumentException("The receiver view must be invertible.", nameof(receiverView));
        Vector3 camera = new(worldFromView.M41, worldFromView.M42, worldFromView.M43);
        float projectionScale = MathF.Abs(receiverView.Projection.M11);
        if (projectionScale <= 1e-6f || receiverView.ViewportWidth == 0u)
        {
            throw new ArgumentException(
                "The receiver projection must have a finite non-zero horizontal scale and viewport.",
                nameof(receiverView));
        }
        float lodScale = 0.5f / projectionScale *
            Settings.VirtualResolution / receiverView.ViewportWidth;
        float resolutionLodBias = MathF.Max(
            0.0f,
            Settings.DirectionalResolutionLodBias + MathF.Log2(lodScale));
        uint physicalPagesPerRow = Settings.AtlasSize / Settings.PageSize;
        uint virtualPagesPerRow = Settings.VirtualResolution / Settings.PageSize;
        uint virtualPageCount = checked(virtualPagesPerRow * virtualPagesPerRow);
        uint coarseBaseOffset = checked((uint)viewCount * virtualPageCount);
        for (int lightIndex = 0; lightIndex < directionalLightCount; lightIndex++)
        {
            Vector3 direction = NormalizeOrZero(lights.Directional[lightIndex].Direction);
            if (direction == Vector3.Zero)
                direction = Vector3.UnitZ;
            Vector3 up = MathF.Abs(Vector3.Dot(direction, Vector3.UnitY)) > 0.95f
                ? Vector3.UnitX
                : Vector3.UnitY;
            Vector3 right = Vector3.Normalize(Vector3.Cross(up, direction));
            Vector3 vertical = Vector3.Normalize(Vector3.Cross(direction, right));
            float cameraRight = Vector3.Dot(camera, right);
            float cameraVertical = Vector3.Dot(camera, vertical);
            for (int clipmapLevel = 0; clipmapLevel < clipmapLevels; clipmapLevel++)
            {
                float worldExtent = Settings.DirectionalWorldExtent *
                    MathF.Pow(2.0f, clipmapLevel);
                float pageWorldSize = worldExtent / virtualPagesPerRow;
                int centerPageX = checked((int)MathF.Round(cameraRight / pageWorldSize));
                int centerPageY = checked((int)MathF.Round(cameraVertical / pageWorldSize));
                int pageOriginX = checked(centerPageX - (int)virtualPagesPerRow / 2);
                int pageOriginY = checked(centerPageY - (int)virtualPagesPerRow / 2);
                Vector3 snappedCenter =
                    right * (centerPageX * pageWorldSize) +
                    vertical * (centerPageY * pageWorldSize);
                Matrix4x4 lightView = Matrix4x4.CreateLookAt(
                    snappedCenter - direction * Settings.DirectionalLightDistance,
                    snappedCenter,
                    up);
                Matrix4x4 lightProjection = Matrix4x4.CreateOrthographic(
                    worldExtent,
                    worldExtent,
                    Settings.DirectionalNearPlane,
                    Settings.DirectionalFarPlane);
                int viewIndex = checked(lightIndex * clipmapLevels + clipmapLevel);
                _views[viewIndex] = new VirtualShadowView
                {
                    LightViewProjection = lightView * lightProjection,
                    VirtualResolution = Settings.VirtualResolution,
                    PageSize = Settings.PageSize,
                    AtlasSize = Settings.AtlasSize,
                    PhysicalPagesPerRow = physicalPagesPerRow,
                    MaxPhysicalPages = checked(physicalPagesPerRow * physicalPagesPerRow),
                    VirtualPageOriginX = pageOriginX,
                    DepthBias = Settings.DepthBias,
                    PageTableOffset = checked((uint)viewIndex * virtualPageCount),
                    VirtualPageOriginY = pageOriginY,
                    ClipmapLevel = checked(
                        Settings.DirectionalFirstClipmapLevel + clipmapLevel),
                    LightIndex = checked((uint)lightIndex),
                    ClipmapWorldOrigin = camera,
                    ResolutionLodBias = resolutionLodBias,
                    FirstClipmapLevel = Settings.DirectionalFirstClipmapLevel,
                    ClipmapLevelCount = Settings.DirectionalClipmapLevels,
                    CoarsePageShift = Settings.CoarsePageShift,
                    CoarsePageTableOffset = checked(
                        coarseBaseOffset + (uint)viewIndex * Settings.CoarsePagesPerView),
                    LightKind = VirtualShadowLightKind.Directional,
                    FaceIndex = checked((uint)clipmapLevel),
                };
                _preparedLightViews[viewIndex] = lightView;
                _preparedLightProjections[viewIndex] = lightProjection;
            }
        }
        for (int lightIndex = 0; lightIndex < pointLightCount; lightIndex++)
        {
            RenderPointLight light = lights.Points[lightIndex];
            ValidateLocalLight(light.Position, light.Range, $"point light {lightIndex}");
            Matrix4x4 projection = Matrix4x4.CreatePerspectiveFieldOfView(
                MathF.PI * 0.5f,
                1.0f,
                Settings.LocalNearPlane,
                light.Range);
            for (uint faceIndex = 0; faceIndex < 6u; faceIndex++)
            {
                GetPointLightFace(faceIndex, out Vector3 forward, out Vector3 up);
                Matrix4x4 lightView = Matrix4x4.CreateLookAt(
                    light.Position,
                    light.Position + forward,
                    up);
                int viewIndex = checked(pointViewOffset + lightIndex * 6 + (int)faceIndex);
                uint gpuLightIndex = checked((uint)(lights.Directional.Count + lightIndex));
                _views[viewIndex] = CreateLocalView(
                    viewIndex,
                    lightView,
                    projection,
                    gpuLightIndex,
                    VirtualShadowLightKind.Point,
                    faceIndex,
                    light.Position,
                    light.Range,
                    physicalPagesPerRow,
                    virtualPageCount,
                    coarseBaseOffset);
                _preparedLightViews[viewIndex] = lightView;
                _preparedLightProjections[viewIndex] = projection;
            }
        }
        for (int lightIndex = 0; lightIndex < spotLightCount; lightIndex++)
        {
            RenderSpotLight light = lights.Spots[lightIndex];
            ValidateLocalLight(light.Position, light.Range, $"spot light {lightIndex}");
            Vector3 direction = NormalizeOrZero(light.Direction);
            if (direction == Vector3.Zero)
                throw new ArgumentException($"Spot light {lightIndex} has a zero direction.", nameof(lights));
            if (!float.IsFinite(light.OuterConeCos) || light.OuterConeCos is <= -1.0f or >= 1.0f)
                throw new ArgumentException($"Spot light {lightIndex} has an invalid outer cone.", nameof(lights));
            Vector3 up = MathF.Abs(Vector3.Dot(direction, Vector3.UnitY)) > 0.95f
                ? Vector3.UnitX
                : Vector3.UnitY;
            Matrix4x4 lightView = Matrix4x4.CreateLookAt(
                light.Position,
                light.Position + direction,
                up);
            Matrix4x4 projection = Matrix4x4.CreatePerspectiveFieldOfView(
                2.0f * MathF.Acos(light.OuterConeCos),
                1.0f,
                Settings.LocalNearPlane,
                light.Range);
            int viewIndex = checked(spotViewOffset + lightIndex);
            uint gpuLightIndex = checked((uint)(
                lights.Directional.Count + lights.Points.Count + lightIndex));
            _views[viewIndex] = CreateLocalView(
                viewIndex,
                lightView,
                projection,
                gpuLightIndex,
                VirtualShadowLightKind.Spot,
                0u,
                light.Position,
                light.Range,
                physicalPagesPerRow,
                virtualPageCount,
                coarseBaseOffset);
            _preparedLightViews[viewIndex] = lightView;
            _preparedLightProjections[viewIndex] = projection;
        }

        bool viewChanged = !_initialized || viewCount != _committedViewCount;
        if (!viewChanged)
        {
            for (int index = 0; index < viewCount; index++)
            {
                if (HasSameClipmapConfiguration(_views[index], _committedViews[index]))
                    continue;
                viewChanged = true;
                break;
            }
        }
        bool structuralContentChanged =
            _initialized &&
            !viewChanged &&
            (geometryRevision != _committedGeometryRevision ||
             rasterRevision != _committedRasterRevision ||
             instanceCount != _committedInstanceCount);
        VirtualShadowCacheDecision cacheDecision = ResolveCacheDecision(
            _initialized,
            viewChanged,
            structuralContentChanged,
            viewCount,
            _committedCacheGeneration);
        _pendingCacheReset = cacheDecision.ResetCache;
        _pendingCacheGeneration = cacheDecision.CacheGeneration;
        for (int index = 0; index < viewCount; index++)
            _views[index].CacheGeneration = _pendingCacheGeneration;
        return _views.AsSpan(0, viewCount);
    }

    /// <summary>
    /// Copies feature-owned caster projections into generic virtual-depth render views.
    /// </summary>
    public int CopyPreparedRenderViews(Span<RenderView> destination)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_pending)
            throw new InvalidOperationException("Virtual-shadow views have not been prepared for this frame.");
        if (destination.Length < _pendingViewCount)
            throw new ArgumentException("The destination cannot hold all prepared shadow views.", nameof(destination));
        for (int index = 0; index < _pendingViewCount; index++)
        {
            VirtualShadowView shadow = _views[index];
            destination[index] = new RenderView(
                _preparedLightViews[index],
                _preparedLightProjections[index],
                shadow.VirtualResolution,
                shadow.VirtualResolution,
                CameraCut: false);
        }
        return _pendingViewCount;
    }

    /// <summary>
    /// Copies the last committed target mapping and projection used to build the persistent
    /// physical-page HZB. A count mismatch has no temporal correspondence and therefore returns
    /// false instead of manufacturing history for a different view set.
    /// </summary>
    public bool TryCopyCommittedViews(
        Span<VirtualShadowView> destination,
        Span<Matrix4x4> lightViewProjections)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_initialized || !_pending || _committedViewCount != _pendingViewCount)
            return false;
        if (destination.Length < _committedViewCount)
            throw new ArgumentException("The destination cannot hold all committed shadow views.", nameof(destination));
        if (lightViewProjections.Length < _committedViewCount)
        {
            throw new ArgumentException(
                "The projection destination cannot hold all committed shadow views.",
                nameof(lightViewProjections));
        }

        _committedViews.AsSpan(0, _committedViewCount).CopyTo(destination);
        for (int index = 0; index < _committedViewCount; index++)
            lightViewProjections[index] = _committedViews[index].LightViewProjection;
        return true;
    }

    public void BeginFrame()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_pending)
            throw new InvalidOperationException("A virtual-shadow frame is already active.");
        VirtualShadowPageCacheResources pageCache = _pageCache ??
            throw new InvalidOperationException("The virtual-shadow page cache was not created.");
        _pendingPageCacheResources = pageCache.BeginFrame();
        _pendingCacheReset = !_pageTableInitialized;
        _pendingViewCount = 0;
        _pendingInstanceCount = 0;
        _pendingGeometryRevision = 0ul;
        _pendingRasterRevision = 0ul;
        _pendingCacheGeneration = _committedCacheGeneration;
        _pending = true;
    }

    public VirtualShadowMapFrameResources AcquireFrameResources(out Buffer? retiredPageTable)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_pending || _pendingPageCacheResources is not { } cache)
            throw new InvalidOperationException("Begin and prepare the virtual-shadow frame first.");
        if (_pendingViewCount <= 0)
            throw new InvalidOperationException("Virtual-shadow frame resources require active views.");

        ulong requiredSize = checked(
            (ulong)_pendingViewCount *
            (Settings.VirtualPagesPerView + Settings.CoarsePagesPerView) *
            VirtualShadowView.PageTableEntrySizeInBytes);
        retiredPageTable = null;
        if (_pageTable is null || _pageTable.Info.Size != requiredSize)
        {
            Buffer replacement = _backend.CreateBuffer(
                _device,
                new BufferDesc(
                    requiredSize,
                    BufferUsages.ShaderRead | BufferUsages.ShaderWrite |
                        BufferUsages.CopySource | BufferUsages.CopyDestination,
                    "Virtual shadow active-view page table"),
                MemoryType.DeviceLocal);
            retiredPageTable = _pageTable;
            _pageTable = replacement;
            _pageTableEndpoint = [Initial(replacement)];
            _pageTableInitialized = false;
            _pendingCacheReset = true;
        }

        Buffer pageTable = _pageTable ??
            throw new InvalidOperationException("The virtual-shadow page table was not allocated.");
        return new VirtualShadowMapFrameResources(
            new VirtualShadowBufferResource(pageTable, _pageTableEndpoint),
            new VirtualShadowBufferResource(_physicalPageOwners, _physicalPageOwnersEndpoint),
            new VirtualShadowTextureResource(_atlas, _atlasEndpoint),
            cache.FreePages,
            cache.FreePageCount,
            cache.PageMetadata,
            cache.EvictionPages,
            cache.EvictionBucketCounts,
            !_pageTableInitialized);
    }

    public void Commit(ReadOnlySpan<QueueCompletion> completions)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_pending)
            throw new InvalidOperationException("No virtual-shadow frame is waiting for commit.");
        QueueCompletion computeCompletion = FindCompletion(completions, QueueType.Compute);
        QueueCompletion graphicsCompletion = FindCompletion(completions, QueueType.Graphics);
        _pageCache?.Commit(
            completions,
            poolProcessed: _pendingViewCount != 0,
            metadataInitialized: _pendingCacheReset);
        Buffer pageTable = _pageTable ??
            throw new InvalidOperationException("The virtual-shadow page table was not allocated.");
        _pageTableEndpoint[0] = new BufferBoundaryState(
            new BufferRange(0, pageTable.Info.Size),
            PipelineSync.AllShading,
            ResourceAccess.ShaderResource,
            ResourceContentState.Defined,
            graphicsCompletion.Queue,
            graphicsCompletion);
        _physicalPageOwnersEndpoint[0] = new BufferBoundaryState(
            new BufferRange(0, _physicalPageOwners.Info.Size),
            PipelineSync.ComputeShading,
            ResourceAccess.UnorderedAccess,
            ResourceContentState.Defined,
            computeCompletion.Queue,
            computeCompletion);
        _atlasEndpoint[0] = new TextureBoundaryState(
            new TextureSubresourceRange(0, 1, 0, 1, TextureAspects.Color),
            PipelineSync.AllShading,
            ResourceAccess.ShaderResource,
            TextureLayout.ShaderResource,
            ResourceContentState.Defined,
            graphicsCompletion.Queue,
            graphicsCompletion);
        _initialized = true;
        _pageTableInitialized = true;
        _committedCacheGeneration = _pendingCacheGeneration;
        _committedViewCount = _pendingViewCount;
        _committedInstanceCount = _pendingInstanceCount;
        _committedGeometryRevision = _pendingGeometryRevision;
        _committedRasterRevision = _pendingRasterRevision;
        if (_committedViews.Length != _pendingViewCount)
            _committedViews = new VirtualShadowView[_pendingViewCount];
        _views.AsSpan(0, _pendingViewCount).CopyTo(_committedViews);
        _pendingCacheReset = false;
        _pendingViewCount = 0;
        _pendingInstanceCount = 0;
        _pendingGeometryRevision = 0ul;
        _pendingRasterRevision = 0ul;
        _pendingCacheGeneration = 0u;
        _pendingPageCacheResources = null;
        _pending = false;
    }

    public void Discard()
    {
        _pageCache?.Discard();
        ObjectDisposedException.ThrowIf(_disposed, this);
        _pendingCacheReset = false;
        _pendingViewCount = 0;
        _pendingInstanceCount = 0;
        _pendingGeometryRevision = 0ul;
        _pendingRasterRevision = 0ul;
        _pendingCacheGeneration = 0u;
        _pendingPageCacheResources = null;
        _pending = false;
    }

    private static BufferBoundaryState Initial(Buffer buffer) => new(
        new BufferRange(0, buffer.Info.Size),
        buffer.InitialSync,
        buffer.InitialAccess,
        ResourceContentState.Undefined);

    private void EnsurePendingViewStorage(int viewCount)
    {
        if (_views.Length == viewCount)
            return;
        _views = new VirtualShadowView[viewCount];
        _preparedLightViews = new Matrix4x4[viewCount];
        _preparedLightProjections = new Matrix4x4[viewCount];
    }

    private static TextureBoundaryState Initial(Texture texture) => new(
        new TextureSubresourceRange(0, 1, 0, 1, TextureAspects.Color),
        texture.InitialSync,
        texture.InitialAccess,
        texture.InitialLayout,
        ResourceContentState.Undefined);

    private static QueueCompletion FindCompletion(
        ReadOnlySpan<QueueCompletion> completions,
        QueueType queueType)
    {
        foreach (ref readonly QueueCompletion completion in completions)
            if (completion.Queue.Type == queueType)
                return completion;
        throw new InvalidOperationException(
            $"Virtual shadows require a {queueType} Queue completion.");
    }

    private static Vector3 NormalizeOrZero(Vector3 value)
        => value.LengthSquared() > 1e-12f ? Vector3.Normalize(value) : Vector3.Zero;

    private VirtualShadowView CreateLocalView(
        int viewIndex,
        Matrix4x4 lightView,
        Matrix4x4 lightProjection,
        uint lightIndex,
        VirtualShadowLightKind lightKind,
        uint faceIndex,
        Vector3 lightPosition,
        float lightRange,
        uint physicalPagesPerRow,
        uint virtualPageCount,
        uint coarseBaseOffset) => new()
    {
        LightViewProjection = lightView * lightProjection,
        VirtualResolution = Settings.VirtualResolution,
        PageSize = Settings.PageSize,
        AtlasSize = Settings.AtlasSize,
        PhysicalPagesPerRow = physicalPagesPerRow,
        MaxPhysicalPages = Settings.MaxPhysicalPages,
        DepthBias = Settings.DepthBias,
        PageTableOffset = checked((uint)viewIndex * virtualPageCount),
        ClipmapLevel = -1,
        LightIndex = lightIndex,
        FirstClipmapLevel = -1,
        ClipmapLevelCount = 1u,
        CoarsePageShift = Settings.CoarsePageShift,
        CoarsePageTableOffset = checked(
            coarseBaseOffset + (uint)viewIndex * Settings.CoarsePagesPerView),
        LightKind = lightKind,
        FaceIndex = faceIndex,
        LightPosition = lightPosition,
        LightRange = lightRange,
    };

    private void ValidateLocalLight(Vector3 position, float range, string label)
    {
        if (!float.IsFinite(position.X) || !float.IsFinite(position.Y) ||
            !float.IsFinite(position.Z) || !float.IsFinite(range) ||
            range <= Settings.LocalNearPlane)
        {
            throw new ArgumentException(
                $"The {label} must have a finite position and a range greater than the local shadow near plane.");
        }
    }

    private static void GetPointLightFace(
        uint faceIndex,
        out Vector3 forward,
        out Vector3 up)
    {
        (forward, up) = faceIndex switch
        {
            0u => (Vector3.UnitX, Vector3.UnitY),
            1u => (-Vector3.UnitX, Vector3.UnitY),
            2u => (Vector3.UnitY, -Vector3.UnitZ),
            3u => (-Vector3.UnitY, Vector3.UnitZ),
            4u => (Vector3.UnitZ, Vector3.UnitY),
            5u => (-Vector3.UnitZ, Vector3.UnitY),
            _ => throw new ArgumentOutOfRangeException(nameof(faceIndex)),
        };
    }

    private static uint NextCacheGeneration(uint value)
    {
        uint next = value + 1u;
        return next == 0u ? 1u : next;
    }

    internal static VirtualShadowCacheDecision ResolveCacheDecision(
        bool initialized,
        bool viewChanged,
        bool structuralContentChanged,
        int viewCount,
        uint committedCacheGeneration)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(viewCount);
        bool reset = !initialized;
        bool advanceGeneration = reset || viewChanged || structuralContentChanged;
        return new VirtualShadowCacheDecision(
            reset,
            initialized && viewCount != 0,
            advanceGeneration
                ? NextCacheGeneration(committedCacheGeneration)
                : committedCacheGeneration);
    }

    private static bool HasSameClipmapConfiguration(
        in VirtualShadowView left,
        in VirtualShadowView right) =>
        (left.LightKind == VirtualShadowLightKind.Directional
            ? HasSameLinearProjection(left.LightViewProjection, right.LightViewProjection)
            : left.LightViewProjection.Equals(right.LightViewProjection)) &&
        left.VirtualResolution == right.VirtualResolution &&
        left.PageSize == right.PageSize &&
        left.AtlasSize == right.AtlasSize &&
        left.PhysicalPagesPerRow == right.PhysicalPagesPerRow &&
        left.MaxPhysicalPages == right.MaxPhysicalPages &&
        left.DepthBias.Equals(right.DepthBias) &&
        left.PageTableOffset == right.PageTableOffset &&
        left.ClipmapLevel == right.ClipmapLevel &&
        left.LightIndex == right.LightIndex &&
        left.FirstClipmapLevel == right.FirstClipmapLevel &&
        left.ClipmapLevelCount == right.ClipmapLevelCount &&
        left.CoarsePageShift == right.CoarsePageShift &&
        left.CoarsePageTableOffset == right.CoarsePageTableOffset &&
        left.LightKind == right.LightKind &&
        left.FaceIndex == right.FaceIndex &&
        left.LightPosition == right.LightPosition &&
        left.LightRange.Equals(right.LightRange);

    private static bool HasSameLinearProjection(Matrix4x4 left, Matrix4x4 right) =>
        left.M11.Equals(right.M11) && left.M12.Equals(right.M12) &&
        left.M13.Equals(right.M13) && left.M14.Equals(right.M14) &&
        left.M21.Equals(right.M21) && left.M22.Equals(right.M22) &&
        left.M23.Equals(right.M23) && left.M24.Equals(right.M24) &&
        left.M31.Equals(right.M31) && left.M32.Equals(right.M32) &&
        left.M33.Equals(right.M33) && left.M34.Equals(right.M34) &&
        left.M44.Equals(right.M44);

    public void Dispose()
    {
        if (_disposed)
            return;
        List<Exception>? failures = null;
        if (_pageCache is not null)
            TryDispose(_pageCache, ref failures);
        _pageCache = null;
        TryDispose(_atlas, ref failures);
        TryDispose(_physicalPageOwners, ref failures);
        if (_pageTable is not null)
            TryDispose(_pageTable, ref failures);
        _pageTable = null;
        TryDispose(PageClearPipeline, ref failures);
        TryDispose(PageHiZReducePipeline, ref failures);
        TryDispose(PageHiZInitializePipeline, ref failures);
        TryDispose(ViewCompactPipeline, ref failures);
        TryDispose(PagePoolPipeline, ref failures);
        TryDispose(CasterInvalidationPipeline, ref failures);
        TryDispose(StateInitializePipeline, ref failures);
        TryDispose(PageAllocatePipeline, ref failures);
        TryDispose(CoarsePageMarkPipeline, ref failures);
        TryDispose(PageMarkPipeline, ref failures);
        Array.Clear(_pageTableEndpoint);
        Array.Clear(_physicalPageOwnersEndpoint);
        Array.Clear(_atlasEndpoint);
        Array.Clear(_views);
        Array.Clear(_committedViews);
        Array.Clear(_preparedLightViews);
        Array.Clear(_preparedLightProjections);
        _pending = false;
        _pendingPageCacheResources = null;
        _pendingCacheReset = false;
        _pageTableInitialized = false;
        _pendingViewCount = 0;
        _committedViewCount = 0;
        _pendingInstanceCount = 0;
        _committedInstanceCount = 0;
        _pendingGeometryRevision = 0ul;
        _committedGeometryRevision = 0ul;
        _pendingRasterRevision = 0ul;
        _committedRasterRevision = 0ul;
        _pendingCacheGeneration = 0u;
        _committedCacheGeneration = 0u;
        _disposed = true;
        if (failures is not null)
            throw failures.Count == 1 ? failures[0] : new AggregateException(failures);
    }

    private static void TryDispose(IDisposable value, ref List<Exception>? failures)
    {
        try { value.Dispose(); }
        catch (Exception failure) { (failures ??= []).Add(failure); }
    }
}

internal readonly record struct VirtualShadowCacheDecision(
    bool ResetCache,
    bool PageGranularTransformInvalidation,
    uint CacheGeneration);

public readonly record struct VirtualShadowBufferResource(
    Buffer Resource,
    ReadOnlyMemory<BufferBoundaryState> BoundaryStates);

public readonly record struct VirtualShadowTextureResource(
    Texture Resource,
    ReadOnlyMemory<TextureBoundaryState> BoundaryStates);

public readonly record struct VirtualShadowMapFrameResources(
    VirtualShadowBufferResource PageTable,
    VirtualShadowBufferResource PhysicalPageOwners,
    VirtualShadowTextureResource Atlas,
    VirtualShadowBufferResource FreePages,
    VirtualShadowBufferResource FreePageCount,
    VirtualShadowBufferResource PageMetadata,
    VirtualShadowBufferResource EvictionPages,
    VirtualShadowBufferResource EvictionBucketCounts,
    bool RequiresInitialization);
