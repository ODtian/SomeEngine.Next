using System.Numerics;
using SomeEngine.ECS.Entities;
using SomeEngine.Render.Components;
using SomeEngine.Render.Shadows;
using SomeEngine.Render.Lighting;
using SomeEngine.Render.Systems;

namespace SomeEngine.Render.Features.Shadows;

/// <summary>Maintains shadow projections in the rendering world for geometry view collection.</summary>
public sealed class VirtualShadowViews : IDisposable
{
    private readonly RenderWorld _world;
    private readonly List<Entity> _entities;
    public VirtualShadowMapSettings Settings { get; private set; }

    public VirtualShadowViews(RenderWorld world, VirtualShadowMapSettings settings)
        : this(world, settings, [], [], [], [], [], [], new VirtualShadowDistantLightUpdates()) { }

    private VirtualShadowViews(RenderWorld world, VirtualShadowMapSettings settings, List<Entity> entities,
        Dictionary<VirtualShadowLocalLight, DistantLightHistory> distantLights,
        Dictionary<VirtualShadowLocalLight, DistantLightHistory> frameDistantLights,
        HashSet<VirtualShadowLocalLight> distantUpdates, HashSet<VirtualShadowLocalLight> eligibleDistantLights,
        Dictionary<VirtualShadowLightKey, int> historyKeyIndices,
        VirtualShadowDistantLightUpdates distantSchedule)
    {
        _world = world;
        Settings = settings;
        _entities = entities;
        _distantLights = distantLights;
        _frameDistantLights = frameDistantLights;
        _distantUpdates = distantUpdates;
        _eligibleDistantLights = eligibleDistantLights;
        _historyKeyIndices = historyKeyIndices;
        _distantSchedule = distantSchedule;
    }

    internal void SetSettings(VirtualShadowMapSettings settings)
    {
        Settings = settings;
    }
    private VirtualShadowView[] _views = [];
    private VirtualShadowView[] _historyViews = [];
    private VirtualShadowLightKey[] _keys = [];
    private readonly Dictionary<VirtualShadowLightKey, int> _historyKeyIndices;
    private Matrix4x4[] _lightViews = [];
    private Matrix4x4[] _lightProjections = [];
    private bool _initialized;
    private bool _disposed;
    private bool _cacheReset;
    private int _historyViewCount;
    private int _viewCount;
    private uint _historyCacheGeneration;
    private uint _cacheGeneration;
    private uint _framePageTableEntryCount;
    private WorldOrigin _worldOrigin;
    private WorldOrigin _historyWorldOrigin;
    private uint _viewPlayerId;
    private uint _historyViewPlayerId;



    public bool RequiresCacheReset => _cacheReset;
    public bool UsesPageGranularTransformInvalidation =>
        _initialized && _viewCount != 0 && !_cacheReset;
    public uint CacheGeneration => _cacheGeneration;
    public uint PageTableEntryCount => _framePageTableEntryCount;
    public int PreviousViewCount => _historyViewCount;
    internal ReadOnlyMemory<VirtualShadowLightKey> Keys => _keys.AsMemory(0, _viewCount);
    public uint DirectionalLightCount { get; private set; }
    public uint DirectionalClipmapLevelCount { get; private set; }
    public uint PointLightCount { get; private set; }
    public uint SpotLightCount { get; private set; }
    public uint PointViewOffset { get; private set; }
    public uint SpotViewOffset { get; private set; }
    public uint FirstPersonViewOffset { get; private set; } = uint.MaxValue;

    internal VirtualShadowViews CreateFrame() => new(_world, Settings, _entities, _distantLights,
        _frameDistantLights, _distantUpdates, _eligibleDistantLights, _historyKeyIndices, _distantSchedule)
    {
        _historyViews = _historyViews,
        _historyViewCount = _historyViewCount,
        _historyCacheGeneration = _historyCacheGeneration,
        _historyWorldOrigin = _historyWorldOrigin,
        _historyViewPlayerId = _historyViewPlayerId,
        _initialized = _initialized,
        GlobalLocalResolutionLodBias = GlobalLocalResolutionLodBias,
    };

    public ReadOnlySpan<VirtualShadowView> Views => _views.AsSpan(0, _viewCount);

    public ReadOnlySpan<VirtualShadowView> PrepareViews(
        RenderLightSet lights,
        IReadOnlyList<RenderLightIndex> uploadOrder,
        in RenderView receiverView,
        bool resourcesRebuilt = false,
        bool hasFirstPersonCasters = false,
        uint receiverMaskColumns = 1u)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(lights);
        int directionalLightCount = Settings.Enabled ? lights.Directional.Count : 0;
        int pointLightCount = Settings.Enabled ? lights.Points.Count : 0;
        int spotLightCount = Settings.Enabled ? lights.Spots.Count : 0;
        DirectionalCameraParameters directionalCamera = directionalLightCount != 0
            ? CalculateDirectionalCameraParameters(receiverView)
            : new(0f, Settings.DirectionalFirstClipmapLevel, Settings.DirectionalClipmapLevels);
        DirectionalClipmapLevelCount = directionalCamera.LevelCount;
        int clipmapLevels = checked((int)directionalCamera.LevelCount);
        int directionalViewCount = checked(directionalLightCount * clipmapLevels);
        int pointViewOffset = directionalViewCount;
        int pointViewCount = checked(pointLightCount * 6);
        int spotViewOffset = checked(pointViewOffset + pointViewCount);
        int firstPersonViewOffset = checked(spotViewOffset + spotLightCount);
        bool firstPersonShadows = Settings.FirstPersonShadowsEnabled && hasFirstPersonCasters && directionalLightCount != 0;
        int firstPersonLevels = firstPersonShadows ? checked(Settings.FirstPersonLastClipmapLevel - Settings.FirstPersonFirstClipmapLevel + 1) : 0;
        int viewCount = checked(firstPersonViewOffset + directionalLightCount * firstPersonLevels);
        FirstPersonViewOffset = firstPersonShadows ? checked((uint)firstPersonViewOffset) : uint.MaxValue;
        ResizeViewStorage(viewCount);
        DirectionalLightCount = checked((uint)directionalLightCount);
        PointLightCount = checked((uint)pointLightCount);
        SpotLightCount = checked((uint)spotLightCount);
        PointViewOffset = checked((uint)pointViewOffset);
        SpotViewOffset = checked((uint)spotViewOffset);
        _viewCount = viewCount;
        _cacheReset = resourcesRebuilt || !_initialized;
        _worldOrigin = receiverView.WorldOrigin;
        _viewPlayerId = receiverView.PlayerId;
        _frameDistantLights.Clear();
        _distantUpdates.Clear();
        _eligibleDistantLights.Clear();
        if (viewCount == 0)
        {
            _cacheGeneration = _historyCacheGeneration;
            return [];
        }
        if (!Matrix4x4.Invert(receiverView.View, out Matrix4x4 worldFromView))
            throw new ArgumentException("The receiver view must be invertible.", nameof(receiverView));
        Vector3 camera = new(worldFromView.M41, worldFromView.M42, worldFromView.M43);
        bool orthographicReceiver = receiverView.Projection.M44 >= 1f;
        Vector3 clipmapOrigin = orthographicReceiver && Settings.OrthographicProjectCameraTarget &&
            receiverView.CameraTarget is { } target ? target : camera;
        uint atlasPagesPerRow = Settings.AtlasSize / Settings.PageSize;
        uint virtualPagesPerRow = Settings.VirtualResolution / Settings.PageSize;
        uint pageTableEntriesPerView = Settings.PageTableEntriesPerView;
        for (int family = 0; family < (firstPersonShadows ? 2 : 1); family++)
        {
            bool firstPerson = family != 0;
            DirectionalCameraParameters familyCamera = firstPerson
                ? new(directionalCamera.ResolutionLodBias, Settings.FirstPersonFirstClipmapLevel, checked((uint)firstPersonLevels))
                : directionalCamera;
            int familyLevels = checked((int)familyCamera.LevelCount);
            for (int lightIndex = 0; lightIndex < directionalLightCount; lightIndex++)
            {
                RenderDirectionalLight light = lights.Directional[lightIndex];
                float resolutionLodBias = MathF.Max(0f, CalculateLightResolutionLodBias(
                    Settings.DirectionalResolutionLodBias, Settings.DirectionalMovingResolutionLodBias,
                    firstPerson ? 1f : lights.GetDirectionalMobility(lightIndex), light.ShadowSettings.ResolutionLodBias) +
                    directionalCamera.ResolutionLodBias);
                Vector3 direction = NormalizeOrZero(light.Direction);
                if (direction == Vector3.Zero)
                    direction = Vector3.UnitZ;
                Vector3 up = MathF.Abs(Vector3.Dot(direction, Vector3.UnitY)) > 0.95f
                    ? Vector3.UnitX
                    : Vector3.UnitY;
                Vector3 right = Vector3.Normalize(Vector3.Cross(up, direction));
                Vector3 vertical = Vector3.Normalize(Vector3.Cross(direction, right));
                double originRight = receiverView.WorldOrigin.Dot(right);
                double originVertical = receiverView.WorldOrigin.Dot(vertical);
                double cameraRight = originRight + Vector3.Dot(clipmapOrigin, right);
                double cameraVertical = originVertical + Vector3.Dot(clipmapOrigin, vertical);
                float cameraDepth = Vector3.Dot(clipmapOrigin, direction);
                double coarsestRadius = Math.Pow(2.0, familyCamera.FirstLevel + familyLevels) *
                    Settings.WorldUnitsPerCentimeter;
                long referenceSnapX = checked((long)Math.Round(cameraRight / coarsestRadius));
                long referenceSnapY = checked((long)Math.Round(cameraVertical / coarsestRadius));
                for (int clipmapLevel = 0; clipmapLevel < familyLevels; clipmapLevel++)
                {
                    int absoluteClipmapLevel = checked(
                        familyCamera.FirstLevel + clipmapLevel);
                    // UE clipmaps snap by one virtual radius, i.e. one quarter of the
                    // level-0 page table. Snapping every page destroys cache locality.
                    float levelRadius = MathF.Pow(2.0f, absoluteClipmapLevel + 1.0f) *
                        Settings.WorldUnitsPerCentimeter;
                    float worldExtent = 4.0f * levelRadius;
                    long centerSnapX = checked((long)Math.Round(cameraRight / levelRadius));
                    long centerSnapY = checked((long)Math.Round(cameraVertical / levelRadius));
                    int pagesPerSnap = checked((int)virtualPagesPerRow / 4);
                    long centerPageX = checked(centerSnapX * pagesPerSnap);
                    long centerPageY = checked(centerSnapY * pagesPerSnap);
                    // LookAt's horizontal axis is -right; UV's vertical flip is
                    // -vertical. The world-page origin follows those same axes,
                    // so scrolling preserves the address of fixed world geometry.
                    long pageOriginX = checked(-centerPageX - (int)virtualPagesPerRow / 2);
                    long pageOriginY = checked(-centerPageY - (int)virtualPagesPerRow / 2);
                    Vector3 snappedCenter =
                        right * (float)(centerSnapX * (double)levelRadius - originRight) +
                        vertical * (float)(centerSnapY * (double)levelRadius - originVertical) + direction * cameraDepth;
                    long referenceScale = 1L << (familyLevels - clipmapLevel - 1);
                    float cullingEndDistance = levelRadius * MathF.Pow(2f, -resolutionLodBias);
                    float depthRadius = MathF.Max(levelRadius * Settings.DirectionalDepthRangeScale, cullingEndDistance);
                    Matrix4x4 lightView = Matrix4x4.CreateLookAt(
                        Vector3.Zero,
                        direction,
                        up);
                    Matrix4x4 lightProjection = Matrix4x4.CreateOrthographic(
                        worldExtent,
                        worldExtent,
                        -depthRadius,
                        depthRadius);
                    int viewIndex = checked((firstPerson ? firstPersonViewOffset : 0) + lightIndex * familyLevels + clipmapLevel);
                    var key = new VirtualShadowLightKey(VirtualShadowLightKind.Directional,
                        lights.GetDirectionalEntity(lightIndex), firstPerson ? 1u : 0u, absoluteClipmapLevel);
                    _keys[viewIndex] = key;
                    float depthCenter = firstPerson ? cameraDepth : SelectDirectionalDepthCenter(
                        key, absoluteClipmapLevel,
                        cameraDepth, depthRadius, MathF.Max(levelRadius, cullingEndDistance), lightView * lightProjection);
                    // The view tracks current world position while this offset keeps
                    // cached pages in their adopted light-space depth coordinates.
                    lightProjection.M43 -= (cameraDepth - depthCenter) * lightProjection.M33;
                    bool coarseFallback =
                        absoluteClipmapLevel >= Settings.DirectionalFirstCoarseClipmapLevel &&
                        absoluteClipmapLevel <= Settings.DirectionalLastCoarseClipmapLevel ||
                        clipmapLevel == familyLevels - 1;
                    _views[viewIndex] = new VirtualShadowView
                    {
                        LightViewProjection = lightView * lightProjection,
                        WorldTranslation = -snappedCenter,
                        VirtualResolution = Settings.VirtualResolution,
                        PageSize = Settings.PageSize,
                        AtlasSize = Settings.AtlasSize,
                        AtlasPagesPerRow = atlasPagesPerRow,
                        MaxPages = checked(atlasPagesPerRow * atlasPagesPerRow),
                        VirtualPageOriginX = unchecked((int)pageOriginX),
                        DepthBias = Settings.DepthBias,
                        PageTableOffset = checked((uint)viewIndex * pageTableEntriesPerView),
                        VirtualPageOriginY = unchecked((int)pageOriginY),
                        WorldPageOriginX = pageOriginX,
                        WorldPageOriginY = pageOriginY,
                        ClipmapCornerRelativeOffsetX = checked((int)(centerSnapX - referenceSnapX * referenceScale + 2)),
                        ClipmapCornerRelativeOffsetY = checked((int)(centerSnapY - referenceSnapY * referenceScale + 2)),
                        ClipmapLevel = absoluteClipmapLevel,
                        LightIndex = checked((uint)lightIndex),
                        ClipmapWorldOrigin = clipmapOrigin,
                        ResolutionLodBias = resolutionLodBias,
                        FirstClipmapLevel = familyCamera.FirstLevel,
                        WorldUnitsPerCentimeter = Settings.WorldUnitsPerCentimeter,
                        ClipmapLevelCount = familyCamera.LevelCount,
                        MipLevelCount = 1u,
                        MinMipLevel = 0u,
                        Flags =
                            (orthographicReceiver ? VirtualShadowViewFlags.OrthographicReceiver : 0u) |
                            (firstPerson ? VirtualShadowViewFlags.FirstPersonShadow : CalculateCoarsePageFlags(Settings.DirectionalCoarsePageMode, coarseFallback)) |
                            (Settings.DirectionalReceiverMask
                                ? VirtualShadowViewFlags.UseReceiverMask
                                : 0u),
                        LightKind = VirtualShadowLightKind.Directional,
                        FaceIndex = checked((uint)clipmapLevel),
                        GeometryLodScale = Settings.ShadowGeometryLodScale,
                        MaxPixelsPerEdge = Settings.ShadowMaxPixelsPerEdge,
                        MinPixelsPerEdgeHardware = Settings.ShadowMinPixelsPerEdgeHardware,
                        DicingRate = Settings.ShadowDicingRate,
                        DepthCenterZ = depthCenter,
                        DepthRadius = depthRadius,
                        DepthCullRange = !firstPerson && Settings.PrefilteredDistantEnabled
                            ? new Vector2(lightProjection.M43 + (clipmapLevel == 0 ? 0f : cullingEndDistance * .5f) * lightProjection.M33,
                                lightProjection.M43 + cullingEndDistance * lightProjection.M33)
                            : !firstPerson && Settings.DirectionalReceiverMask
                                ? new Vector2(-float.MaxValue, lightProjection.M43 - cullingEndDistance * lightProjection.M33)
                                : new Vector2(-float.MaxValue, float.MaxValue),
                    };
                    _views[viewIndex].Flags = Settings.ApplyDirectionalDynamicReceiverRefresh(
                        _views[viewIndex].Flags, VirtualShadowLightKind.Directional);
                    if (!firstPerson && Settings.PrefilteredDistantEnabled) _views[viewIndex].Flags |= VirtualShadowViewFlags.PrefilteredDistant;
                    _lightViews[viewIndex] = lightView;
                    _lightProjections[viewIndex] = lightProjection;
                }
            }
        }
        for (int ordinal = 0; ordinal < uploadOrder.Count; ordinal++)
        {
            RenderLightIndex reference = uploadOrder[ordinal];
            int lightIndex = reference.Index;
            uint gpuLightIndex = checked((uint)(lights.Directional.Count + ordinal));
            if (reference.Kind == RenderLightKind.Point)
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
                        Vector3.Zero,
                        forward,
                        up);
                    int viewIndex = checked(pointViewOffset + lightIndex * 6 + (int)faceIndex);
                    _keys[viewIndex] = new(VirtualShadowLightKind.Point, lights.GetPointEntity(lightIndex), faceIndex, 0);
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
                        atlasPagesPerRow,
                        pageTableEntriesPerView);
                    ConfigureLocalRepresentation(ref _views[viewIndex], receiverView, projection.M11);
                    _lightViews[viewIndex] = lightView;
                    _lightProjections[viewIndex] = projection;
                }
            }
            else
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
                    Vector3.Zero,
                    direction,
                    up);
                Matrix4x4 projection = Matrix4x4.CreatePerspectiveFieldOfView(
                    2.0f * MathF.Acos(light.OuterConeCos),
                    1.0f,
                    Settings.LocalNearPlane,
                    light.Range);
                int viewIndex = checked(spotViewOffset + lightIndex);
                _keys[viewIndex] = new(VirtualShadowLightKind.Spot, lights.GetSpotEntity(lightIndex), 0, 0);
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
                    atlasPagesPerRow,
                    pageTableEntriesPerView);
                ConfigureLocalRepresentation(ref _views[viewIndex], receiverView, projection.M11);
                _lightViews[viewIndex] = lightView;
                _lightProjections[viewIndex] = projection;
            }
        }

        for (int index = 0; index < viewCount; index++)
        {
            Vector3 viewCenter = -_views[index].WorldTranslation;
            WorldOrigin viewOrigin = new(_worldOrigin.X + viewCenter.X,
                _worldOrigin.Y + viewCenter.Y, _worldOrigin.Z + viewCenter.Z);
            viewOrigin.Split(out Vector3 worldOriginHigh, out Vector3 worldOriginLow);
            _views[index].WorldOriginHigh = worldOriginHigh;
            _views[index].WorldOriginLow = worldOriginLow;
            _views[index].PageDilationBorder = _views[index].LightKind == VirtualShadowLightKind.Directional
                ? Settings.DirectionalPageDilationBorder : Settings.LocalPageDilationBorder;
            VirtualShadowView shadow = _views[index];
            LightShadowSettings source;
            if (shadow.LightKind == VirtualShadowLightKind.Directional)
            {
                RenderDirectionalLight light = lights.Directional[checked((int)shadow.LightIndex)];
                source = light.ShadowSettings;
            }
            else if (shadow.LightKind == VirtualShadowLightKind.Point)
            {
                int sourceIndex = uploadOrder[checked((int)shadow.LightIndex - lights.Directional.Count)].Index;
                RenderPointLight light = lights.Points[sourceIndex];
                source = light.ShadowSettings;
            }
            else
            {
                int sourceIndex = uploadOrder[checked((int)shadow.LightIndex - lights.Directional.Count)].Index;
                RenderSpotLight light = lights.Spots[sourceIndex];
                source = light.ShadowSettings;
            }
            Matrix4x4 lightView = _lightViews[index];
            _views[index].LightDirection = new(-lightView.M13, -lightView.M23, -lightView.M33);
            _views[index].SourceRadius = source.SourceRadius;
            _views[index].SourceAngleRadians = shadow.LightKind == VirtualShadowLightKind.Directional
                ? (source.SourceAngleDegrees ?? .5357f) * (MathF.PI / 180f) : 0f;
            _views[index].ProjectionDepthScale = _lightProjections[index].M33;
            _views[index].ProjectionDepthOffset = _lightProjections[index].M43;
            _views[index].TexelDitherScale = 1f;
        }

        uint generation = _historyCacheGeneration;
        for (int index = 0; index < viewCount; index++)
        {
            if (!_cacheReset && _historyKeyIndices.TryGetValue(_keys[index], out int previous))
            {
                VirtualShadowView cached = RebaseCommittedView(_historyViews[previous]);
                if (HasSameClipmapConfiguration(_views[index], cached))
                {
                    _views[index].CacheGeneration = cached.CacheGeneration;
                    if (_viewPlayerId == _historyViewPlayerId &&
                        _views[index].LightViewProjection.Equals(cached.LightViewProjection) &&
                        _views[index].WorldTranslation == cached.WorldTranslation)
                        _views[index].Flags |= VirtualShadowViewFlags.PreviousProjectionValid;
                    continue;
                }
            }
            generation = NextCacheGeneration(generation);
            _views[index].CacheGeneration = generation;
        }
        SelectDistantUpdates(lights, uploadOrder, viewCount, ref generation);
        _cacheGeneration = generation;
        for (int index = 0; index < viewCount; index++)
        {
            _views[index].ReceiverMaskOriginX = (uint)index % receiverMaskColumns * Settings.VirtualPagesPerRow;
            _views[index].ReceiverMaskOriginY = (uint)index / receiverMaskColumns * Settings.VirtualPagesPerRow * 3u;
        }
        return _views.AsSpan(0, viewCount);
    }

    internal void SetPageTableRanges(ReadOnlySpan<PageTableRange> ranges, uint entryCount)
    {
        for (int index = 0; index < _viewCount; index++)
        {
            _views[index].PageTableOffset = ranges[index].Offset;
            _views[index].RangeId = ranges[index].RangeId;
            _views[index].RangeGeneration = ranges[index].RangeGeneration;
        }
        _framePageTableEntryCount = entryCount;
    }

    internal void InvalidateResourceHistory()
    {
        _cacheReset = true;
        for (int index = 0; index < _viewCount; index++)
            _views[index].Flags &= ~VirtualShadowViewFlags.PreviousProjectionValid;
    }

    /// <summary>
    /// Copies feature-owned caster projections into generic virtual-depth render views.
    /// </summary>
    public int CopyRenderViews(Span<RenderView> destination)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (destination.Length < _viewCount)
            throw new ArgumentException("The destination cannot hold all prepared shadow views.", nameof(destination));
        for (int index = 0; index < _viewCount; index++)
        {
            VirtualShadowView shadow = _views[index];
            destination[index] = new RenderView(
                _lightViews[index],
                _lightProjections[index],
                shadow.VirtualResolution,
                shadow.VirtualResolution,
                CameraCut: false,
                GeometryLodScale: shadow.GeometryLodScale,
                WorldOrigin: new WorldOrigin(
                    (double)shadow.WorldOriginHigh.X + shadow.WorldOriginLow.X,
                    (double)shadow.WorldOriginHigh.Y + shadow.WorldOriginLow.Y,
                    (double)shadow.WorldOriginHigh.Z + shadow.WorldOriginLow.Z),
                PlayerId: _viewPlayerId,
                Family: (shadow.Flags & VirtualShadowViewFlags.FirstPersonShadow) != 0u
                    ? RenderViewFamily.FirstPersonShadow : RenderViewFamily.Shadow);
        }
        return _viewCount;
    }

    /// <summary>
    /// Copies the last committed target mapping and projection used to build the persistent
    /// physical-page HZB. A count mismatch has no temporal correspondence and therefore returns
    /// false instead of manufacturing history for a different view set.
    /// </summary>
    public bool TryCopyHistoryViews(
        Span<VirtualShadowView> destination,
        Span<Matrix4x4> lightViewProjections)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_initialized || _cacheReset)
            return false;
        if (destination.Length < _historyViewCount)
            throw new ArgumentException("The destination cannot hold all committed shadow views.", nameof(destination));
        if (lightViewProjections.Length < _viewCount)
        {
            throw new ArgumentException(
                "The projection destination cannot hold all committed shadow views.",
                nameof(lightViewProjections));
        }

        for (int index = 0; index < _historyViewCount; index++)
        {
            VirtualShadowView previous = RebaseCommittedView(_historyViews[index]);
            destination[index] = previous;
        }
        for (int index = 0; index < _viewCount; index++)
        {
            if ((_views[index].Flags & VirtualShadowViewFlags.PreviousProjectionValid) == 0u) continue;
            VirtualShadowView previous = RebaseCommittedView(_historyViews[_historyKeyIndices[_keys[index]]]);
            Vector3 translation = previous.WorldTranslation - _views[index].WorldTranslation;
            lightViewProjections[index] = Matrix4x4.CreateTranslation(translation) * previous.LightViewProjection;
        }
        return true;
    }

    public void Submit()
    {
        _initialized = true;
        _historyCacheGeneration = _cacheGeneration;
        _historyViewCount = _viewCount;
        CommitDistantLights();
        _historyWorldOrigin = _worldOrigin;
        _historyViewPlayerId = _viewPlayerId;
        _historyViews = _views.AsSpan(0, _viewCount).ToArray();
        _historyKeyIndices.Clear();
        for (int index = 0; index < _viewCount; index++) _historyKeyIndices.Add(_keys[index], index);
    }

    private void ResizeViewStorage(int viewCount)
    {
        if (_views.Length == viewCount)
            return;
        _views = new VirtualShadowView[viewCount];
        _keys = new VirtualShadowLightKey[viewCount];
        _lightViews = new Matrix4x4[viewCount];
        _lightProjections = new Matrix4x4[viewCount];
    }

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
            if (Settings.OrthographicEstimateClipmapLevels)
                firstLevel = Math.Max(0, checked(Math.Max(firstLevel,
                    (int)MathF.Floor(MathF.Log2(halfWidthCentimeters))) + Settings.OrthographicClipmapLodBias));
            levelCount = checked((uint)(Math.Max(firstLevel, lastLevel) - firstLevel + 1));
        }
        float resolutionScale = .5f * Settings.VirtualResolution / (viewportWidth * projectionScale);
        return new(MathF.Log2(resolutionScale), firstLevel, levelCount);
    }

    private static float CalculateLightResolutionLodBias(float nonMovingBias, float movingBias,
        float mobilityFactor, float lightBias) =>
        nonMovingBias + mobilityFactor * (MathF.Max(nonMovingBias, movingBias) - nonMovingBias) + lightBias;

    private static uint CalculateCoarsePageFlags(VirtualShadowCoarsePageMode mode, bool coarseLevel) =>
        (coarseLevel && mode != VirtualShadowCoarsePageMode.Disabled ? VirtualShadowViewFlags.CoarseClipLevel : 0u) |
        (mode == VirtualShadowCoarsePageMode.CachedDynamic ? VirtualShadowViewFlags.ForceCacheDynamicCoarse : 0u);

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
        uint atlasPagesPerRow,
        uint pageTableEntriesPerView) => new()
    {
        LightViewProjection = lightView * lightProjection,
        WorldTranslation = -lightPosition,
        VirtualResolution = Settings.VirtualResolution,
        PageSize = Settings.PageSize,
        AtlasSize = Settings.AtlasSize,
        AtlasPagesPerRow = atlasPagesPerRow,
        MaxPages = Settings.MaxPages,
        DepthBias = Settings.DepthBias,
        PageTableOffset = checked((uint)viewIndex * pageTableEntriesPerView),
        ClipmapLevel = -1,
        LightIndex = lightIndex,
        ResolutionLodBias = resolutionLodBias,
        FirstClipmapLevel = -1,
        ClipmapLevelCount = 1u,
        MipLevelCount = Settings.MipLevelCount,
        MinMipLevel = 0u,
        Flags = CalculateCoarsePageFlags(Settings.LocalCoarsePageMode, true) |
            (Settings.LocalMipMode switch
            {
                VirtualShadowLocalMipMode.Even => VirtualShadowViewFlags.EvenMipLevels,
                VirtualShadowLocalMipMode.Odd => VirtualShadowViewFlags.OddMipLevels,
                _ => 0u,
            }) |
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

    private float SelectDirectionalDepthCenter(
        VirtualShadowLightKey key, int clipmapLevel,
        float center, float depthRadius, float levelRadius, Matrix4x4 projection)
    {
        if (!_initialized || _cacheReset || !_historyKeyIndices.TryGetValue(key, out int previous))
            return center;
        VirtualShadowView cached = RebaseCommittedView(_historyViews[previous]);
        bool sameLevel = cached.LightKind == VirtualShadowLightKind.Directional &&
            cached.ClipmapLevel == clipmapLevel &&
            cached.DepthRadius.Equals(depthRadius) &&
            HasSameDirectionalProjectionAxes(projection, cached.LightViewProjection);
        return sameLevel && MathF.Abs(center - cached.DepthCenterZ) + levelRadius <= .9f * cached.DepthRadius
            ? cached.DepthCenterZ : center;
    }

    private VirtualShadowView RebaseCommittedView(VirtualShadowView view)
    {
        Vector3 translation = _historyWorldOrigin.RelativeTo(_worldOrigin);
        view.LightPosition += translation;
        view.ClipmapWorldOrigin += translation;
        view.WorldTranslation -= translation;
        view.DepthCenterZ += Vector3.Dot(translation, view.LightDirection);
        return view;
    }

    private static bool HasSameClipmapConfiguration(
        in VirtualShadowView left,
        in VirtualShadowView right) =>
        (left.LightKind == VirtualShadowLightKind.Directional
            ? HasSameDirectionalProjectionAxes(left.LightViewProjection, right.LightViewProjection)
            : left.LightViewProjection.Equals(right.LightViewProjection)) &&
        left.VirtualResolution == right.VirtualResolution &&
        left.PageSize == right.PageSize &&
        left.AtlasSize == right.AtlasSize &&
        left.AtlasPagesPerRow == right.AtlasPagesPerRow &&
        left.MaxPages == right.MaxPages &&
        left.DepthBias.Equals(right.DepthBias) &&
        left.ClipmapLevel == right.ClipmapLevel &&
        left.FirstClipmapLevel == right.FirstClipmapLevel &&
        left.ClipmapLevelCount == right.ClipmapLevelCount &&
        left.MipLevelCount == right.MipLevelCount &&
        left.MinMipLevel == right.MinMipLevel &&
        (left.Flags & ~(VirtualShadowViewFlags.PreviousProjectionValid | VirtualShadowViewFlags.CachedDistantLight)) ==
            (right.Flags & ~(VirtualShadowViewFlags.PreviousProjectionValid | VirtualShadowViewFlags.CachedDistantLight)) &&
        left.LightKind == right.LightKind &&
        left.FaceIndex == right.FaceIndex &&
        left.LightPosition == right.LightPosition &&
        left.LightRange.Equals(right.LightRange) &&
        left.GeometryLodScale.Equals(right.GeometryLodScale) &&
        left.MaxPixelsPerEdge.Equals(right.MaxPixelsPerEdge) &&
        left.MinPixelsPerEdgeHardware.Equals(right.MinPixelsPerEdgeHardware) &&
        left.DicingRate.Equals(right.DicingRate) &&
        left.DepthCenterZ.Equals(right.DepthCenterZ) && left.DepthRadius.Equals(right.DepthRadius);

    private static bool HasSameDirectionalProjectionAxes(Matrix4x4 left, Matrix4x4 right) =>
        left.M11.Equals(right.M11) && left.M12.Equals(right.M12) &&
        left.M13.Equals(right.M13) && left.M14.Equals(right.M14) &&
        left.M21.Equals(right.M21) && left.M22.Equals(right.M22) &&
        left.M23.Equals(right.M23) && left.M24.Equals(right.M24) &&
        left.M31.Equals(right.M31) && left.M32.Equals(right.M32) &&
        left.M33.Equals(right.M33) && left.M34.Equals(right.M34) &&
        left.M44.Equals(right.M44);


    private readonly Dictionary<VirtualShadowLocalLight, DistantLightHistory> _distantLights;
    private readonly Dictionary<VirtualShadowLocalLight, DistantLightHistory> _frameDistantLights;
    private readonly HashSet<VirtualShadowLocalLight> _distantUpdates;
    private readonly HashSet<VirtualShadowLocalLight> _eligibleDistantLights;
    private readonly VirtualShadowDistantLightUpdates _distantSchedule;
    private readonly record struct DistantLightHistory(int FirstView, int ViewCount);
    public float GlobalLocalResolutionLodBias { get; private set; }

    /// <summary>Accepts the completed global page-pressure result used for local shadow requests.</summary>
    public void UpdateGlobalLocalResolutionLodBias(float bias) => GlobalLocalResolutionLodBias = bias;

    private void ConfigureLocalRepresentation(ref VirtualShadowView view, in RenderView receiver, float projectionScale)
    {
        uint minimumMip = Settings.CalculateConservativeLocalMip(receiver, view.LightPosition, view.LightRange,
            projectionScale, view.ResolutionLodBias, GlobalLocalResolutionLodBias);
        view.MinMipLevel = minimumMip;
        bool distant = Settings.DistantLightMode == VirtualShadowDistantLightMode.All ||
            (Settings.DistantLightMode == VirtualShadowDistantLightMode.Conservative && minimumMip == Settings.MipLevelCount - 1u);
        if (distant)
        {
            view.MinMipLevel = Settings.MipLevelCount - 1u;
            view.Flags |= VirtualShadowViewFlags.SinglePage | VirtualShadowViewFlags.DistantLight;
        }
    }

    private void SelectDistantUpdates(RenderLightSet lights, IReadOnlyList<RenderLightIndex> uploadOrder, int viewCount, ref uint generation)
    {
        for (int index = 0; index < viewCount; index++)
        {
            VirtualShadowView view = _views[index];
            if (view.LightKind == VirtualShadowLightKind.Directional || view.FaceIndex != 0u ||
                (view.Flags & VirtualShadowViewFlags.DistantLight) == 0u) continue;
            int row = uploadOrder[checked((int)view.LightIndex - lights.Directional.Count)].Index;
            Entity entity = view.LightKind == VirtualShadowLightKind.Point
                ? lights.GetPointEntity(row)
                : lights.GetSpotEntity(row);
            var key = new VirtualShadowLocalLight(view.LightKind, entity, entity == Entity.Null ? checked((uint)row) : 0u);
            int faces = view.LightKind == VirtualShadowLightKind.Point ? 6 : 1;
            if (!_cacheReset && _distantLights.TryGetValue(key, out DistantLightHistory previous) &&
                previous.ViewCount == faces && previous.FirstView + faces <= _historyViews.Length &&
                view.CacheGeneration == _historyViews[previous.FirstView].CacheGeneration &&
                HasSameDistantProjection(view, _historyViews[previous.FirstView]))
            {
                _eligibleDistantLights.Add(key);
            }
            _frameDistantLights.Add(key, new DistantLightHistory(index, faces));
        }
        _distantSchedule.WriteUpdates(_eligibleDistantLights, Settings.MaximumDistantUpdatesPerFrame, _distantUpdates);
        foreach ((VirtualShadowLocalLight light, DistantLightHistory history) in _frameDistantLights)
        {
            bool cached = _eligibleDistantLights.Contains(light);
            bool update = _distantUpdates.Contains(light);
            for (int face = 0; face < history.ViewCount; face++)
            {
                ref VirtualShadowView view = ref _views[history.FirstView + face];
                if (cached && !update) view.Flags |= VirtualShadowViewFlags.CachedDistantLight;
                else
                {
                    view.Flags &= ~VirtualShadowViewFlags.CachedDistantLight;
                    if (cached && update)
                    {
                        generation = NextCacheGeneration(generation);
                        view.CacheGeneration = generation;
                    }
                }
            }
        }
    }

    private static bool HasSameDistantProjection(VirtualShadowView current, VirtualShadowView previous) =>
        current.LightKind == previous.LightKind && current.WorldOriginHigh == previous.WorldOriginHigh &&
        current.WorldOriginLow == previous.WorldOriginLow &&
        current.LightRange == previous.LightRange && current.LightViewProjection == previous.LightViewProjection &&
        current.VirtualResolution == previous.VirtualResolution && current.PageSize == previous.PageSize &&
        (previous.Flags & VirtualShadowViewFlags.DistantLight) != 0u;

    private void CommitDistantLights()
    {
        _distantSchedule.CompleteFrame(_frameDistantLights, _distantUpdates);
        _distantLights.Clear();
        foreach ((VirtualShadowLocalLight light, DistantLightHistory history) in _frameDistantLights)
            _distantLights.Add(light, history);
        _frameDistantLights.Clear();
    }

    public void Update(ReadOnlySpan<RenderView> views, ReadOnlySpan<Matrix4x4> previous)
    {
        TrimViewEntities(views.Length);
        for (int index = 0; index < views.Length; index++)
            UpdateViewEntity(index, views[index], previous.IsEmpty ? null : previous[index]);
    }

    internal void UpdateCurrentViews(ReadOnlySpan<RenderView> views, ReadOnlySpan<Matrix4x4> previous)
    {
        TrimViewEntities(views.Length);
        for (int index = 0; index < views.Length; index++)
            UpdateViewEntity(index, views[index],
                (_views[index].Flags & VirtualShadowViewFlags.PreviousProjectionValid) != 0u ? previous[index] : null);
    }

    private void TrimViewEntities(int count)
    {
        while (_entities.Count > count)
        {
            _world.DestroyEntity(_entities[^1]);
            _entities.RemoveAt(_entities.Count - 1);
        }
    }

    private void UpdateViewEntity(int index, in RenderView view, Matrix4x4? previous)
    {
        var target = new RenderViewTarget(ViewTargetKind.ShadowPages, checked((uint)index));
        if (index == _entities.Count)
        {
            Entity entity = _world.CreateEntity(view);
            _world.Add(entity, target);
            if (previous is { } projection) _world.Add(entity, new RenderPreviousView(projection));
            _entities.Add(entity);
        }
        else
        {
            Entity entity = _entities[index];
            _world.Replace(entity, view);
            _world.Replace(entity, target);
            if (previous is { } projection)
            {
                if (_world.Has<RenderPreviousView>(entity)) _world.Replace(entity, new RenderPreviousView(projection));
                else _world.Add(entity, new RenderPreviousView(projection));
            }
            else if (_world.Has<RenderPreviousView>(entity)) _world.Remove<RenderPreviousView>(entity);
        }
    }

    public void Dispose()
    {
        foreach (Entity entity in _entities) _world.DestroyEntity(entity);
        _entities.Clear();
        _disposed = true;
    }
}
