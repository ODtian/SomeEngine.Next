using System.Numerics;
using SomeEngine.Assets;
using SomeEngine.Assets.Schema;
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
    private readonly Buffer _pageTable;
    private readonly Buffer _pageAllocator;
    private readonly Buffer _physicalPageOwners;
    private readonly Texture _atlas;
    private VirtualShadowPageCacheResources? _pageCache;
    private readonly BufferBoundaryState[] _pageTableEndpoint;
    private readonly BufferBoundaryState[] _allocatorEndpoint;
    private readonly BufferBoundaryState[] _physicalPageOwnersEndpoint;
    private readonly TextureBoundaryState[] _atlasEndpoint;
    private readonly VirtualShadowView[] _views;
    private readonly VirtualShadowView[] _committedViews;
    private bool _initialized;
    private bool _pending;
    private bool _disposed;
    private bool _pendingCacheReset;
    private int _committedViewCount;
    private int _pendingViewCount;
    private int _committedInstanceCount;
    private int _pendingInstanceCount;
    private ulong _committedGeometryRevision;
    private ulong _pendingGeometryRevision;
    private ulong _committedMaterialMappingRevision;
    private ulong _pendingMaterialMappingRevision;
    private uint _committedCacheGeneration;
    private uint _pendingCacheGeneration;

    public VirtualShadowMap(
        IGraphicsBackend backend,
        Device device,
        AssetLoader assets,
        VirtualShadowMapShaders shaders,
        VirtualShadowMapSettings settings)
    {
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        _device = device ?? throw new ArgumentNullException(nameof(device));
        ArgumentNullException.ThrowIfNull(assets);
        ArgumentNullException.ThrowIfNull(shaders);
        Settings = settings ?? throw new ArgumentNullException(nameof(settings));
        settings.Validate();
        _views = new VirtualShadowView[settings.MaxShadowViews];
        _committedViews = new VirtualShadowView[settings.MaxShadowViews];

        LinkedComputePipeline? markPages = null;
        LinkedComputePipeline? allocatePages = null;
        LinkedComputePipeline? clearPages = null;
        Buffer? pageTable = null;
        Buffer? pageAllocator = null;
        Buffer? physicalPageOwners = null;
        Texture? atlas = null;
        try
        {
            markPages = LinkedComputePipeline.Create(
                _backend,
                _device,
                assets,
                shaders.MarkPages,
                "Virtual shadow receiver page marking");
            allocatePages = LinkedComputePipeline.Create(
                _backend,
                _device,
                assets,
                shaders.AllocatePages,
                "Virtual shadow physical-page allocation");
            clearPages = LinkedComputePipeline.Create(
                _backend,
                _device,
                assets,
                shaders.ClearPages,
                "Virtual shadow active-page clear");
            pageTable = _backend.CreateBuffer(
                _device,
                new BufferDesc(
                    checked((ulong)settings.PageTableEntryCount *
                        VirtualShadowView.PageTableEntrySizeInBytes),
                    BufferUsages.ShaderRead | BufferUsages.ShaderWrite | BufferUsages.CopyDestination,
                    "Virtual shadow page table"),
                MemoryType.DeviceLocal);
            pageAllocator = _backend.CreateBuffer(
                _device,
                new BufferDesc(
                    2u * sizeof(uint),
                    BufferUsages.ShaderRead | BufferUsages.ShaderWrite | BufferUsages.CopyDestination,
                    "Virtual shadow physical-page allocator"),
                MemoryType.DeviceLocal);
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
            if (pageAllocator is not null) TryDispose(pageAllocator, ref cleanupFailures);
            if (pageTable is not null) TryDispose(pageTable, ref cleanupFailures);
            if (clearPages is not null) TryDispose(clearPages, ref cleanupFailures);
            if (allocatePages is not null) TryDispose(allocatePages, ref cleanupFailures);
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
        PageAllocatePipeline = allocatePages;
        PageClearPipeline = clearPages;
        _pageTable = pageTable;
        _pageAllocator = pageAllocator;
        _physicalPageOwners = physicalPageOwners;
        _atlas = atlas;

        _pageTableEndpoint = [Initial(_pageTable)];
        _allocatorEndpoint = [Initial(_pageAllocator)];
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
    public LinkedComputePipeline PageAllocatePipeline { get; }
    public LinkedComputePipeline PageClearPipeline { get; }
    public bool RequiresInitialization => !_initialized;
    public bool RequiresCacheReset => _pendingCacheReset;
    public bool UsesPageGranularTransformInvalidation =>
        _initialized && _pendingViewCount != 0 && !_pendingCacheReset;
    public uint PendingCacheGeneration => _pendingCacheGeneration;
    public ulong PendingGeometryRevision => _pendingGeometryRevision;
    public ulong PendingMaterialMappingRevision => _pendingMaterialMappingRevision;

    public ReadOnlySpan<VirtualShadowView> PrepareViews(
        RenderLightSet lights,
        in RenderView receiverView,
        ulong geometryRevision,
        ulong materialMappingRevision,
        int instanceCount)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(lights);
        if (!_pending)
            throw new InvalidOperationException(
                "Import the virtual-shadow resources before preparing frame views.");
        int lightCount = Math.Min(
            lights.Directional.Count,
            checked((int)Settings.MaxShadowLights));
        int clipmapLevels = checked((int)Settings.DirectionalClipmapLevels);
        int viewCount = checked(lightCount * clipmapLevels);
        _pendingViewCount = viewCount;
        _pendingGeometryRevision = geometryRevision;
        _pendingMaterialMappingRevision = materialMappingRevision;
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
        for (int lightIndex = 0; lightIndex < lightCount; lightIndex++)
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
                };
            }
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
             materialMappingRevision != _committedMaterialMappingRevision ||
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

    public VirtualShadowMapFrameResources BeginFrame()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        VirtualShadowPageCacheResources pageCache = _pageCache ??
            throw new InvalidOperationException("The virtual-shadow page cache was not created.");
        VirtualShadowPageCacheFrameResources cache = pageCache.BeginFrame();
        VirtualShadowMapFrameResources result = new(
            new VirtualShadowBufferResource(_pageTable, _pageTableEndpoint),
            new VirtualShadowBufferResource(_pageAllocator, _allocatorEndpoint),
            new VirtualShadowBufferResource(_physicalPageOwners, _physicalPageOwnersEndpoint),
            new VirtualShadowTextureResource(_atlas, _atlasEndpoint),
            cache.FreePages,
            cache.FreePageCount,
            cache.PageMetadata,
            cache.CompactRequests,
            cache.CompactRequestCount,
            !_initialized);
        if (!_pending)
        {
            _pendingCacheReset = !_initialized;
            _pendingViewCount = 0;
            _pendingInstanceCount = 0;
            _pendingGeometryRevision = 0ul;
            _pendingMaterialMappingRevision = 0ul;
            _pendingCacheGeneration = _committedCacheGeneration;
            _pending = true;
        }
        return result;
    }

    public void Commit(ReadOnlySpan<QueueCompletion> completions)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_pending)
            throw new InvalidOperationException("No virtual-shadow frame is waiting for commit.");
        QueueCompletion completion = FindGraphicsCompletion(completions);
        _pageCache?.Commit(completion);
        _pageTableEndpoint[0] = new BufferBoundaryState(
            new BufferRange(0, _pageTable.Info.Size),
            PipelineSync.AllShading,
            ResourceAccess.ShaderResource,
            ResourceContentState.Defined,
            completion.Queue,
            completion);
        _allocatorEndpoint[0] = new BufferBoundaryState(
            new BufferRange(0, _pageAllocator.Info.Size),
            PipelineSync.ComputeShading,
            ResourceAccess.UnorderedAccess,
            ResourceContentState.Defined,
            completion.Queue,
            completion);
        _physicalPageOwnersEndpoint[0] = new BufferBoundaryState(
            new BufferRange(0, _physicalPageOwners.Info.Size),
            PipelineSync.ComputeShading,
            ResourceAccess.UnorderedAccess,
            ResourceContentState.Defined,
            completion.Queue,
            completion);
        _atlasEndpoint[0] = new TextureBoundaryState(
            new TextureSubresourceRange(0, 1, 0, 1, TextureAspects.Color),
            PipelineSync.AllShading,
            ResourceAccess.ShaderResource,
            TextureLayout.ShaderResource,
            ResourceContentState.Defined,
            completion.Queue,
            completion);
        _initialized = true;
        _committedCacheGeneration = _pendingCacheGeneration;
        _committedViewCount = _pendingViewCount;
        _committedInstanceCount = _pendingInstanceCount;
        _committedGeometryRevision = _pendingGeometryRevision;
        _committedMaterialMappingRevision = _pendingMaterialMappingRevision;
        if (_pendingViewCount != 0)
            _views.AsSpan(0, _pendingViewCount).CopyTo(_committedViews);
        _pendingCacheReset = false;
        _pendingViewCount = 0;
        _pendingInstanceCount = 0;
        _pendingGeometryRevision = 0ul;
        _pendingMaterialMappingRevision = 0ul;
        _pendingCacheGeneration = 0u;
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
        _pendingMaterialMappingRevision = 0ul;
        _pendingCacheGeneration = 0u;
        _pending = false;
    }

    private static BufferBoundaryState Initial(Buffer buffer) => new(
        new BufferRange(0, buffer.Info.Size),
        buffer.InitialSync,
        buffer.InitialAccess,
        ResourceContentState.Undefined);

    private static TextureBoundaryState Initial(Texture texture) => new(
        new TextureSubresourceRange(0, 1, 0, 1, TextureAspects.Color),
        texture.InitialSync,
        texture.InitialAccess,
        texture.InitialLayout,
        ResourceContentState.Undefined);

    private static QueueCompletion FindGraphicsCompletion(ReadOnlySpan<QueueCompletion> completions)
    {
        foreach (ref readonly QueueCompletion completion in completions)
            if (completion.Queue.Type == QueueType.Graphics)
                return completion;
        throw new InvalidOperationException("Virtual shadows require a Graphics Queue completion.");
    }

    private static Vector3 NormalizeOrZero(Vector3 value)
        => value.LengthSquared() > 1e-12f ? Vector3.Normalize(value) : Vector3.Zero;

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
        bool reset = !initialized || viewChanged || structuralContentChanged;
        return new VirtualShadowCacheDecision(
            reset,
            initialized && viewCount != 0 && !reset,
            reset
                ? NextCacheGeneration(committedCacheGeneration)
                : committedCacheGeneration);
    }

    private static bool HasSameClipmapConfiguration(
        in VirtualShadowView left,
        in VirtualShadowView right) =>
        HasSameLinearProjection(left.LightViewProjection, right.LightViewProjection) &&
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
        left.ClipmapLevelCount == right.ClipmapLevelCount;

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
        TryDispose(_pageAllocator, ref failures);
        TryDispose(_pageTable, ref failures);
        TryDispose(PageClearPipeline, ref failures);
        TryDispose(PageAllocatePipeline, ref failures);
        TryDispose(PageMarkPipeline, ref failures);
        Array.Clear(_pageTableEndpoint);
        Array.Clear(_allocatorEndpoint);
        Array.Clear(_physicalPageOwnersEndpoint);
        Array.Clear(_atlasEndpoint);
        Array.Clear(_views);
        Array.Clear(_committedViews);
        _pending = false;
        _pendingCacheReset = false;
        _pendingViewCount = 0;
        _committedViewCount = 0;
        _pendingInstanceCount = 0;
        _committedInstanceCount = 0;
        _pendingGeometryRevision = 0ul;
        _committedGeometryRevision = 0ul;
        _pendingMaterialMappingRevision = 0ul;
        _committedMaterialMappingRevision = 0ul;
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
    VirtualShadowBufferResource PageAllocator,
    VirtualShadowBufferResource PhysicalPageOwners,
    VirtualShadowTextureResource Atlas,
    VirtualShadowBufferResource FreePages,
    VirtualShadowBufferResource FreePageCount,
    VirtualShadowBufferResource PageMetadata,
    VirtualShadowBufferResource CompactRequests,
    VirtualShadowBufferResource CompactRequestCount,
    bool RequiresInitialization);
