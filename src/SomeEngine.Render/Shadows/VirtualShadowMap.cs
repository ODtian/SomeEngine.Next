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
    public const uint EvictionBucketCount = 8;
    private readonly IGraphicsBackend _backend;
    private readonly Device _device;
    private Buffer? _pageTable;
    private Buffer? _previousPageTable;
    private BufferBoundaryState[] _previousPageTableEndpoint = [];
    private bool _previousPageTableInitialized;
    private bool _pageTableChanged;
    private bool _frameResourcesAcquired;
    private readonly Buffer _physicalPageOwners;
    private readonly Texture _atlas;
    private VirtualShadowPageCacheResources? _pageCache;
    private VirtualShadowPageCacheFrameResources? _pendingPageCacheResources;
    private BufferBoundaryState[] _pageTableEndpoint = [];
    private BufferBoundaryState[] _physicalPageOwnersEndpoint;
    private TextureBoundaryState[] _atlasEndpoint;
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
        LinkedComputePipeline? resetFrame = null;
        LinkedComputePipeline? resetReceiverMasks = null;
        LinkedComputePipeline? markCoarsePages = null;
        LinkedComputePipeline? allocatePages = null;
        LinkedComputePipeline? clearPages = null;
        LinkedComputePipeline? initializeState = null;
        LinkedComputePipeline? invalidateCasters = null;
        LinkedComputePipeline? buildPagePool = null;
        LinkedComputePipeline? compactViews = null;
        LinkedComputePipeline? initializePageHiZ = null;
        LinkedComputePipeline? reducePageHiZ = null;
        LinkedComputePipeline? completePageDepth = null;
        LinkedComputePipeline? dynamicResolution = null;
        Buffer? physicalPageOwners = null;
        Texture? atlas = null;
        try
        {
            resetReceiverMasks = LinkedComputePipeline.Create(
                _backend, _device, shaders.ResetReceiverMasksShader, shaders.ResetReceiverMasks,
                "Virtual shadow receiver mask reset");
            resetFrame = LinkedComputePipeline.Create(
                _backend, _device, shaders.ResetFrameShader, shaders.ResetFrame,
                "Virtual shadow frame reset");
            markPages = LinkedComputePipeline.Create(
                _backend,
                _device,
                shaders.MarkPagesShader,
                shaders.MarkPages,
                "Virtual shadow receiver page marking");
            markCoarsePages = LinkedComputePipeline.Create(
                _backend,
                _device,
                shaders.MarkCoarsePagesShader,
                shaders.MarkCoarsePages,
                "Virtual shadow coarse page marking");
            allocatePages = LinkedComputePipeline.Create(
                _backend,
                _device,
                shaders.AllocatePagesShader,
                shaders.AllocatePages,
                "Virtual shadow physical-page allocation");
            clearPages = LinkedComputePipeline.Create(
                _backend,
                _device,
                shaders.ClearPagesShader,
                shaders.ClearPages,
                "Virtual shadow active-page clear");
            initializeState = LinkedComputePipeline.Create(
                _backend,
                _device,
                shaders.InitializeStateShader,
                shaders.InitializeState,
                "Virtual shadow state initialization");
            invalidateCasters = LinkedComputePipeline.Create(
                _backend,
                _device,
                shaders.InvalidateCastersShader,
                shaders.InvalidateCasters,
                "Virtual shadow caster invalidation");
            buildPagePool = LinkedComputePipeline.Create(
                _backend,
                _device,
                shaders.BuildPagePoolShader,
                shaders.BuildPagePool,
                "Virtual shadow page-pool build");
            compactViews = LinkedComputePipeline.Create(
                _backend,
                _device,
                shaders.CompactViewsShader,
                shaders.CompactViews,
                "Virtual shadow active-view compaction");
            initializePageHiZ = LinkedComputePipeline.Create(
                _backend,
                _device,
                shaders.InitializePageHiZShader,
                shaders.InitializePageHiZ,
                "Virtual shadow physical-page HiZ initialization");
            completePageDepth = LinkedComputePipeline.Create(_backend, _device,
                shaders.CompletePageDepthShader, shaders.CompletePageDepth, "Complete virtual shadow page depth");
            reducePageHiZ = LinkedComputePipeline.Create(
                _backend,
                _device,
                shaders.ReducePageHiZShader,
                shaders.ReducePageHiZ,
                "Virtual shadow physical-page HiZ reduction");
            dynamicResolution = LinkedComputePipeline.Create(
                _backend,
                _device,
                shaders.DynamicResolutionShader,
                shaders.DynamicResolution,
                "Virtual shadow dynamic resolution feedback");
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
            if (completePageDepth is not null) TryDispose(completePageDepth, ref cleanupFailures);
            if (reducePageHiZ is not null) TryDispose(reducePageHiZ, ref cleanupFailures);
            if (dynamicResolution is not null) TryDispose(dynamicResolution, ref cleanupFailures);
            if (initializePageHiZ is not null) TryDispose(initializePageHiZ, ref cleanupFailures);
            if (buildPagePool is not null) TryDispose(buildPagePool, ref cleanupFailures);
            if (invalidateCasters is not null) TryDispose(invalidateCasters, ref cleanupFailures);
            if (initializeState is not null) TryDispose(initializeState, ref cleanupFailures);
            if (allocatePages is not null) TryDispose(allocatePages, ref cleanupFailures);
            if (markCoarsePages is not null) TryDispose(markCoarsePages, ref cleanupFailures);
            if (markPages is not null) TryDispose(markPages, ref cleanupFailures);
            if (resetFrame is not null) TryDispose(resetFrame, ref cleanupFailures);
            if (resetReceiverMasks is not null) TryDispose(resetReceiverMasks, ref cleanupFailures);
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
        FrameResetPipeline = resetFrame;
        ReceiverMaskResetPipeline = resetReceiverMasks;
        CoarsePageMarkPipeline = markCoarsePages;
        PageAllocatePipeline = allocatePages;
        PageClearPipeline = clearPages;
        StateInitializePipeline = initializeState;
        CasterInvalidationPipeline = invalidateCasters;
        PagePoolPipeline = buildPagePool;
        ViewCompactPipeline = compactViews;
        PageHiZInitializePipeline = initializePageHiZ;
        PageHiZReducePipeline = reducePageHiZ;
        PageDepthCompletePipeline = completePageDepth;
        DynamicResolutionPipeline = dynamicResolution;
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
    public LinkedComputePipeline FrameResetPipeline { get; }
    public LinkedComputePipeline ReceiverMaskResetPipeline { get; }
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
    public LinkedComputePipeline PageDepthCompletePipeline { get; }
    public LinkedComputePipeline DynamicResolutionPipeline { get; }
    public bool RequiresInitialization => !_pageTableInitialized;
    public bool RequiresCacheReset => _pendingCacheReset;
    public bool UsesPageGranularTransformInvalidation =>
        _initialized && _pendingViewCount != 0 && !_pendingCacheReset;
    public uint PendingCacheGeneration => _pendingCacheGeneration;
    public ulong PendingGeometryRevision => _pendingGeometryRevision;
    public ulong PendingRasterRevision => _pendingRasterRevision;
    public uint PendingDirectionalLightCount { get; private set; }
    public uint DirectionalClipmapLevelCount { get; private set; }
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
        int remainingLights = Settings.Enabled ? checked((int)Settings.MaxShadowLights) : 0;
        int directionalLightCount = Math.Min(lights.Directional.Count, remainingLights);
        remainingLights -= directionalLightCount;
        int pointLightCount = Math.Min(lights.Points.Count, remainingLights);
        remainingLights -= pointLightCount;
        int spotLightCount = Math.Min(lights.Spots.Count, remainingLights);
        DirectionalCameraParameters directionalCamera = directionalLightCount != 0
            ? CalculateDirectionalCameraParameters(receiverView)
            : new(0f, Settings.DirectionalFirstClipmapLevel, Settings.DirectionalClipmapLevels);
        DirectionalClipmapLevelCount = directionalCamera.LevelCount;
        int clipmapLevels = checked((int)directionalCamera.LevelCount);
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
            _pendingCacheReset = !_initialized;
            _pendingCacheGeneration = _committedCacheGeneration;
            return [];
        }
        if (!Matrix4x4.Invert(receiverView.View, out Matrix4x4 worldFromView))
            throw new ArgumentException("The receiver view must be invertible.", nameof(receiverView));
        Vector3 camera = new(worldFromView.M41, worldFromView.M42, worldFromView.M43);
        bool orthographicReceiver = receiverView.Projection.M44 >= 1f;
        uint physicalPagesPerRow = Settings.AtlasSize / Settings.PageSize;
        uint virtualPagesPerRow = Settings.VirtualResolution / Settings.PageSize;
        uint pageTableEntriesPerView = Settings.PageTableEntriesPerView;
        for (int lightIndex = 0; lightIndex < directionalLightCount; lightIndex++)
        {
            RenderDirectionalLight light = lights.Directional[lightIndex];
            float resolutionLodBias = MathF.Max(0f, CalculateLightResolutionLodBias(
                Settings.DirectionalResolutionLodBias, Settings.DirectionalMovingResolutionLodBias,
                lights.GetDirectionalMobility(lightIndex), light.ShadowSettings.ResolutionLodBias) +
                directionalCamera.ResolutionLodBias);
            Vector3 direction = NormalizeOrZero(light.Direction);
            if (direction == Vector3.Zero)
                direction = Vector3.UnitZ;
            Vector3 up = MathF.Abs(Vector3.Dot(direction, Vector3.UnitY)) > 0.95f
                ? Vector3.UnitX
                : Vector3.UnitY;
            Vector3 right = Vector3.Normalize(Vector3.Cross(up, direction));
            Vector3 vertical = Vector3.Normalize(Vector3.Cross(direction, right));
            float cameraRight = Vector3.Dot(camera, right);
            float cameraVertical = Vector3.Dot(camera, vertical);
            float cameraDepth = Vector3.Dot(camera, direction);
            for (int clipmapLevel = 0; clipmapLevel < clipmapLevels; clipmapLevel++)
            {
                int absoluteClipmapLevel = checked(
                    directionalCamera.FirstLevel + clipmapLevel);
                // UE clipmaps snap by one virtual radius, i.e. one quarter of the
                // level-0 page table. Snapping every page destroys cache locality.
                float levelRadius = MathF.Pow(2.0f, absoluteClipmapLevel + 1.0f) *
                    Settings.WorldUnitsPerCentimeter;
                float worldExtent = 4.0f * levelRadius;
                int centerSnapX = checked((int)MathF.Round(cameraRight / levelRadius));
                int centerSnapY = checked((int)MathF.Round(cameraVertical / levelRadius));
                int pagesPerSnap = checked((int)virtualPagesPerRow / 4);
                int centerPageX = checked(centerSnapX * pagesPerSnap);
                int centerPageY = checked(centerSnapY * pagesPerSnap);
                // LookAt's horizontal axis is -right; UV's vertical flip is
                // -vertical. The world-page origin follows those same axes,
                // so scrolling preserves the address of fixed world geometry.
                int pageOriginX = checked(-centerPageX - (int)virtualPagesPerRow / 2);
                int pageOriginY = checked(-centerPageY - (int)virtualPagesPerRow / 2);
                Vector3 snappedCenter =
                    right * (centerSnapX * levelRadius) +
                    vertical * (centerSnapY * levelRadius) + direction * cameraDepth;
                float depthRadius = levelRadius * Settings.DirectionalDepthRangeScale;
                Matrix4x4 lightView = Matrix4x4.CreateLookAt(
                    snappedCenter - direction * depthRadius,
                    snappedCenter,
                    up);
                Matrix4x4 lightProjection = Matrix4x4.CreateOrthographic(
                    worldExtent,
                    worldExtent,
                    0.0f,
                    2.0f * depthRadius);
                int viewIndex = checked(lightIndex * clipmapLevels + clipmapLevel);
                bool coarseFallback =
                    absoluteClipmapLevel >= Settings.DirectionalFirstCoarseClipmapLevel &&
                    absoluteClipmapLevel <= Settings.DirectionalLastCoarseClipmapLevel ||
                    clipmapLevel == clipmapLevels - 1;
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
                    PageTableOffset = checked((uint)viewIndex * pageTableEntriesPerView),
                    VirtualPageOriginY = pageOriginY,
                    ClipmapLevel = absoluteClipmapLevel,
                    LightIndex = checked((uint)lightIndex),
                    ClipmapWorldOrigin = camera,
                    ResolutionLodBias = resolutionLodBias,
                    FirstClipmapLevel = directionalCamera.FirstLevel,
                    WorldUnitsPerCentimeter = Settings.WorldUnitsPerCentimeter,
                    ClipmapLevelCount = directionalCamera.LevelCount,
                    MipLevelCount = Settings.MipLevelCount,
                    MinMipLevel = 0u,
                    Flags =
                        (orthographicReceiver ? VirtualShadowViewFlags.OrthographicReceiver : 0u) |
                        (coarseFallback
                            ? VirtualShadowViewFlags.CoarseClipLevel |
                              VirtualShadowViewFlags.ForceCacheDynamicCoarse
                            : 0u) |
                        (Settings.DirectionalReceiverMask
                            ? VirtualShadowViewFlags.UseReceiverMask
                            : 0u),
                    LightKind = VirtualShadowLightKind.Directional,
                    FaceIndex = checked((uint)clipmapLevel),
                    GeometryLodScale = Settings.ShadowGeometryLodScale,
                    MaxPixelsPerEdge = Settings.ShadowMaxPixelsPerEdge,
                    MinPixelsPerEdgeHardware = Settings.ShadowMinPixelsPerEdgeHardware,
                    DicingRate = Settings.ShadowDicingRate,
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
                    CalculateLightResolutionLodBias(Settings.LocalResolutionLodBias, Settings.LocalMovingResolutionLodBias,
                        lights.GetPointMobility(lightIndex), light.ShadowSettings.ResolutionLodBias),
                    physicalPagesPerRow,
                    pageTableEntriesPerView);
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
                CalculateLightResolutionLodBias(Settings.LocalResolutionLodBias, Settings.LocalMovingResolutionLodBias,
                    lights.GetSpotMobility(lightIndex), light.ShadowSettings.ResolutionLodBias),
                physicalPagesPerRow,
                pageTableEntriesPerView);
            _preparedLightViews[viewIndex] = lightView;
            _preparedLightProjections[viewIndex] = projection;
        }

        bool viewLayoutChanged = !_initialized || viewCount != _committedViewCount;
        bool structuralContentChanged =
            _initialized &&
            (geometryRevision != _committedGeometryRevision ||
             rasterRevision != _committedRasterRevision ||
             instanceCount != _committedInstanceCount);
        _pendingCacheReset = !_initialized || viewLayoutChanged;

        uint generation = _committedCacheGeneration;
        if (_pendingCacheReset || structuralContentChanged)
        {
            generation = NextCacheGeneration(generation);
            for (int index = 0; index < viewCount; index++)
                _views[index].CacheGeneration = generation;
        }
        else
        {
            for (int index = 0; index < viewCount; index++)
            {
                if (HasSameClipmapConfiguration(_views[index], _committedViews[index]))
                {
                    _views[index].CacheGeneration = _committedViews[index].CacheGeneration;
                    continue;
                }

                // Moving or reconfigured lights invalidate only their own virtual address
                // space. A global generation bump would destroy every unrelated cached VSM.
                generation = NextCacheGeneration(generation);
                _views[index].CacheGeneration = generation;
            }
        }
        if (generation < _committedCacheGeneration)
        {
            _pendingCacheReset = true;
            for (int index = 0; index < viewCount; index++)
                _views[index].CacheGeneration = generation;
        }
        if (_initialized && viewCount == _committedViewCount)
        {
            for (int index = 0; index < viewCount; index++)
            {
                if (_views[index].LightViewProjection.Equals(
                        _committedViews[index].LightViewProjection))
                {
                    _views[index].Flags |= VirtualShadowViewFlags.PreviousProjectionValid;
                }
            }
        }
        _pendingCacheGeneration = generation;
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
                CameraCut: false,
                GeometryLodScale: shadow.GeometryLodScale);
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

    public VirtualShadowMapFrameResources AcquireFrameResources()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_pending || _pendingPageCacheResources is not { } cache)
            throw new InvalidOperationException("Begin and prepare the virtual-shadow frame first.");

        ulong requiredSize = checked(
            Math.Max(1ul, (ulong)_pendingViewCount *
            Settings.PageTableEntriesPerView) *
            VirtualShadowView.PageTableEntrySizeInBytes);
        if (_frameResourcesAcquired)
            throw new InvalidOperationException("Virtual-shadow frame resources were already acquired.");
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
            _previousPageTable = _pageTable;
            _previousPageTableEndpoint = _pageTableEndpoint;
            _previousPageTableInitialized = _pageTableInitialized;
            _pageTableChanged = true;
            _pageTable = replacement;
            _pageTableEndpoint = [Initial(replacement)];
            _pageTableInitialized = false;
            _pendingCacheReset = true;
        }

        Buffer pageTable = _pageTable ??
            throw new InvalidOperationException("The virtual-shadow page table was not allocated.");
        _frameResourcesAcquired = true;
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

    /// <summary>Accepts actual GPU work and returns a replaced page table for retirement.</summary>
    public Buffer? FinishFrame(RenderGraphFrameResult result, bool contentComplete)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_pending) throw new InvalidOperationException("No virtual-shadow frame is active.");
        if (result.SubmittedCompletions.IsEmpty) return Discard();
        Buffer? retiredPageTable = _pageTableChanged ? _previousPageTable : null;
        _pageTableChanged = false;
        _previousPageTable = null;
        _previousPageTableEndpoint = [];
        _frameResourcesAcquired = false;
        _pageCache?.FinishFrame(result);
        if (_pageTable is { } table)
        {
            ReadOnlySpan<BufferBoundaryState> states = result.GetBufferStates(table);
            if (!states.IsEmpty) _pageTableEndpoint = states.ToArray();
        }
        ReadOnlySpan<BufferBoundaryState> owners = result.GetBufferStates(_physicalPageOwners);
        if (!owners.IsEmpty) _physicalPageOwnersEndpoint = owners.ToArray();
        ReadOnlySpan<TextureBoundaryState> atlas = result.GetTextureStates(_atlas);
        if (!atlas.IsEmpty) _atlasEndpoint = atlas.ToArray();
        if (!contentComplete)
        {
            if (!result.SubmittedCompletions.IsEmpty)
            {
                _initialized = false;
                _pageTableInitialized = false;
            }
            _ = Discard();
            return retiredPageTable;
        }
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
        return retiredPageTable;
    }

    /// <summary>Cancels unpublished CPU changes and returns an unused new page table for disposal.</summary>
    public Buffer? Discard()
    {
        Buffer? discardedPageTable = null;
        if (_pageTableChanged)
        {
            discardedPageTable = _pageTable;
            _pageTable = _previousPageTable;
            _pageTableEndpoint = _previousPageTableEndpoint;
            _pageTableInitialized = _previousPageTableInitialized;
            _previousPageTable = null;
            _previousPageTableEndpoint = [];
            _pageTableChanged = false;
        }
        _frameResourcesAcquired = false;
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
        return discardedPageTable;
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

    private static Vector3 NormalizeOrZero(Vector3 value)
        => value.LengthSquared() > 1e-12f ? Vector3.Normalize(value) : Vector3.Zero;

    private readonly record struct DirectionalCameraParameters(float ResolutionLodBias, int FirstLevel, uint LevelCount);

    private DirectionalCameraParameters CalculateDirectionalCameraParameters(in RenderView view)
    {
        float viewportWidth = MathF.Max(view.ViewportWidth, Settings.DirectionalMinCameraViewportWidth);
        float projectionScale = MathF.Abs(view.Projection.M11);
        int firstLevel = Settings.DirectionalFirstClipmapLevel;
        uint levelCount = Settings.DirectionalClipmapLevels;
        if (view.Projection.M44 >= 1f)
        {
            float halfWidthCentimeters = 1f / MathF.Abs(view.Projection.M11) / Settings.WorldUnitsPerCentimeter;
            viewportWidth = MathF.Max(viewportWidth, MathF.Ceiling(2f * halfWidthCentimeters));
            projectionScale = 1f;
            int lastLevel = checked(firstLevel + (int)levelCount - 1);
            firstLevel = Math.Max(0, Math.Max(firstLevel, checked((int)MathF.Floor(MathF.Log2(halfWidthCentimeters)))));
            levelCount = checked((uint)(Math.Max(firstLevel, lastLevel) - firstLevel + 1));
        }
        float resolutionScale = .5f * Settings.VirtualResolution / (viewportWidth * projectionScale);
        return new(MathF.Log2(resolutionScale), firstLevel, levelCount);
    }

    private static float CalculateLightResolutionLodBias(float nonMovingBias, float movingBias,
        float mobilityFactor, float lightBias) =>
        nonMovingBias + mobilityFactor * (MathF.Max(nonMovingBias, movingBias) - nonMovingBias) + lightBias;

    private VirtualShadowView CreateLocalView(
        int viewIndex,
        Matrix4x4 lightView,
        Matrix4x4 lightProjection,
        uint lightIndex,
        VirtualShadowLightKind lightKind,
        uint faceIndex,
        Vector3 lightPosition,
        float lightRange,
        float resolutionLodBias,
        uint physicalPagesPerRow,
        uint pageTableEntriesPerView) => new()
    {
        LightViewProjection = lightView * lightProjection,
        VirtualResolution = Settings.VirtualResolution,
        PageSize = Settings.PageSize,
        AtlasSize = Settings.AtlasSize,
        PhysicalPagesPerRow = physicalPagesPerRow,
        MaxPhysicalPages = Settings.MaxPhysicalPages,
        DepthBias = Settings.DepthBias,
        PageTableOffset = checked((uint)viewIndex * pageTableEntriesPerView),
        ClipmapLevel = -1,
        LightIndex = lightIndex,
        ResolutionLodBias = resolutionLodBias,
        FirstClipmapLevel = -1,
        ClipmapLevelCount = 1u,
        MipLevelCount = Settings.MipLevelCount,
        MinMipLevel = 0u,
        Flags = VirtualShadowViewFlags.CoarseClipLevel |
            VirtualShadowViewFlags.ForceCacheDynamicCoarse |
            (Settings.LocalReceiverMask ? VirtualShadowViewFlags.UseReceiverMask : 0u),
        LightKind = lightKind,
        FaceIndex = faceIndex,
        LightPosition = lightPosition,
        LightRange = lightRange,
        GeometryLodScale = Settings.ShadowGeometryLodScale,
        MaxPixelsPerEdge = Settings.ShadowMaxPixelsPerEdge,
        MinPixelsPerEdgeHardware = Settings.ShadowMinPixelsPerEdgeHardware,
        DicingRate = Settings.ShadowDicingRate,
        WorldUnitsPerCentimeter = Settings.WorldUnitsPerCentimeter,
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
        // The high bit distinguishes completed receiver coverage from a whole page.
        return next >= 0x80000000u ? 1u : next;
    }

    private static bool HasSameClipmapConfiguration(
        in VirtualShadowView left,
        in VirtualShadowView right) =>
        (left.LightKind == VirtualShadowLightKind.Directional
            ? HasSameDirectionalDepthProjection(left.LightViewProjection, right.LightViewProjection)
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
        left.MipLevelCount == right.MipLevelCount &&
        left.MinMipLevel == right.MinMipLevel &&
        (left.Flags & ~VirtualShadowViewFlags.PreviousProjectionValid) ==
            (right.Flags & ~VirtualShadowViewFlags.PreviousProjectionValid) &&
        left.LightKind == right.LightKind &&
        left.FaceIndex == right.FaceIndex &&
        left.LightPosition == right.LightPosition &&
        left.LightRange.Equals(right.LightRange) &&
        left.GeometryLodScale.Equals(right.GeometryLodScale) &&
        left.MaxPixelsPerEdge.Equals(right.MaxPixelsPerEdge) &&
        left.MinPixelsPerEdgeHardware.Equals(right.MinPixelsPerEdgeHardware) &&
        left.DicingRate.Equals(right.DicingRate);

    private static bool HasSameDirectionalDepthProjection(Matrix4x4 left, Matrix4x4 right) =>
        left.M11.Equals(right.M11) && left.M12.Equals(right.M12) &&
        left.M13.Equals(right.M13) && left.M14.Equals(right.M14) &&
        left.M21.Equals(right.M21) && left.M22.Equals(right.M22) &&
        left.M23.Equals(right.M23) && left.M24.Equals(right.M24) &&
        left.M31.Equals(right.M31) && left.M32.Equals(right.M32) &&
        left.M33.Equals(right.M33) && left.M34.Equals(right.M34) &&
        left.M43.Equals(right.M43) && left.M44.Equals(right.M44);

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
        if (_previousPageTable is not null) TryDispose(_previousPageTable, ref failures);
        _previousPageTable = null;
        if (_pageTable is not null)
            TryDispose(_pageTable, ref failures);
        _pageTable = null;
        TryDispose(PageClearPipeline, ref failures);
        TryDispose(PageDepthCompletePipeline, ref failures);
        TryDispose(PageHiZReducePipeline, ref failures);
        TryDispose(DynamicResolutionPipeline, ref failures);
        TryDispose(PageHiZInitializePipeline, ref failures);
        TryDispose(ViewCompactPipeline, ref failures);
        TryDispose(PagePoolPipeline, ref failures);
        TryDispose(CasterInvalidationPipeline, ref failures);
        TryDispose(StateInitializePipeline, ref failures);
        TryDispose(PageAllocatePipeline, ref failures);
        TryDispose(CoarsePageMarkPipeline, ref failures);
        TryDispose(PageMarkPipeline, ref failures);
        TryDispose(FrameResetPipeline, ref failures);
        TryDispose(ReceiverMaskResetPipeline, ref failures);
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
