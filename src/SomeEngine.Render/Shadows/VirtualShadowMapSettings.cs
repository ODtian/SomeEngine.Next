using System.Numerics;

namespace SomeEngine.Render.Shadows;

public enum VirtualShadowLocalMipMode
{
    All,
    Even,
    Odd,
}

public enum VirtualShadowCoarsePageMode
{
    Disabled = 0,
    /// <summary>Coarse pages receive dynamic caster updates.</summary>
    Dynamic = 1,
    /// <summary>Dynamic coarse pages retain their cache until fine receiver detail requests an update.</summary>
    CachedDynamic = 2,
}

/// <summary>Virtual address-space, page-pool and atlas quality settings.</summary>
public sealed partial record VirtualShadowMapSettings
{
    public bool Enabled { get; init; } = true;
    public uint VirtualResolution { get; init; } = 16_384;
    public uint AtlasSize { get; init; } = 8_192;
    public uint PageSize { get; init; } = 128;
    public uint MaxShadowLights { get; init; } = 4;
    /// <summary>Scene distance represented by one native centimeter. Meter scenes use 0.01.</summary>
    public float WorldUnitsPerCentimeter { get; init; } = 0.01f;
    public uint DirectionalClipmapLevels { get; init; } = 17;
    public int DirectionalFirstClipmapLevel { get; init; } = 6;
    public uint DirectionalMinCameraViewportWidth { get; init; }
    public bool OrthographicEstimateClipmapLevels { get; init; } = true;
    public int OrthographicClipmapLodBias { get; init; }
    public bool OrthographicProjectCameraTarget { get; init; } = true;
    public int DirectionalFirstCoarseClipmapLevel { get; init; } = 15;
    public int DirectionalLastCoarseClipmapLevel { get; init; } = 18;
    public VirtualShadowCoarsePageMode DirectionalCoarsePageMode { get; init; } = VirtualShadowCoarsePageMode.CachedDynamic;
    public VirtualShadowCoarsePageMode LocalCoarsePageMode { get; init; } = VirtualShadowCoarsePageMode.CachedDynamic;
    public float DirectionalResolutionLodBias { get; init; } = -2.0f;
    public float DirectionalMovingResolutionLodBias { get; init; }
    public float LocalResolutionLodBias { get; init; }
    public float LocalMovingResolutionLodBias { get; init; } = 1.0f;
    public float ShadowGeometryLodBias { get; init; } = 1.0f;
    public float ShadowMaxPixelsPerEdge { get; init; } = 1.0f;
    public float ShadowMinPixelsPerEdgeHardware { get; init; } = 32.0f;
    public float ShadowDicingRate { get; init; } = 2.0f;
    public float ShadowGeometryLodScale => MathF.Pow(2.0f, -ShadowGeometryLodBias);
    public VirtualShadowLocalMipMode LocalMipMode { get; init; }
    public float WorkLoadBudget { get; init; }
    /// <summary>Pages refreshed for streamed LOD changes each frame; -1 consumes all deferred changes.</summary>
    public int DeferredInvalidationBudget { get; init; } = -1;
    public float WorkTimeBudgetMilliseconds { get; init; }
    public float WorkHistoryWeight { get; init; } = 0.9f;
    public float WorkMaxDirectionalLodBias { get; init; } = 99999f;
    public float WorkMaxLocalLodBias { get; init; } = 99999f;
    public bool WorkThrottlingEnabled => WorkLoadBudget > 0f || WorkTimeBudgetMilliseconds > 0f;
    public bool DynamicResolutionEnabled { get; init; }
    public float DynamicResolutionMaxLodBias { get; init; } = 2.0f;
    public float DynamicResolutionPagePoolLoadStart { get; init; } = 0.85f;
    public float DynamicResolutionMaxDirectionalLodBias { get; init; } = 2.0f;
    public float DynamicResolutionMaxLocalLodBias { get; init; } = 2.0f;
    public bool DirectionalReceiverMask { get; init; } = true;
    public bool LocalReceiverMask { get; init; } = true;
    // The receiver-plane comparison handles texel offsets. This remaining
    // normalized-depth allowance covers one millimeter over the default directional range.
    public float DepthBias { get; init; } = 0.000001f;
    public float NormalBias { get; init; } = 0.0005f;
    public uint DirectionalRayCount { get; init; } = 7;
    public uint LocalRayCount { get; init; } = 7;
    public uint DirectionalSamplesPerRay { get; init; } = 8;
    public uint LocalSamplesPerRay { get; init; } = 8;
    public uint AdaptiveRayCount { get; init; } = 1;
    public float DirectionalExtrapolateMaxSlope { get; init; } = 5f;
    public float LocalExtrapolateMaxSlope { get; init; } = .05f;
    public float DirectionalTexelDitherScale { get; init; } = 2f;
    public float LocalTexelDitherScale { get; init; } = 2f;
    public float DirectionalRayLengthScale { get; init; } = 1.5f;
    public float LocalMaxRayAngle { get; init; } = .03f;
    public float LocalMaxSlopeBias { get; init; } = 50f;
    public float DirectionalDepthRangeScale { get; init; } = 1000.0f;
    public float LocalNearPlane { get; init; } = 0.05f;
    public float BlockerSearchRadiusTexels { get; init; }
    /// <summary>Zero selects one depth comparison without blocker search or filtering.</summary>
    public float MaxFilterRadiusTexels { get; init; }
    public uint BlockerSampleCount { get; init; } = 8;
    public uint FilterSampleCount { get; init; } = 16;

    public uint VirtualPagesPerRow => VirtualResolution / PageSize;
    public uint VirtualPagesPerView => checked(VirtualPagesPerRow * VirtualPagesPerRow);
    public uint MipLevelCount => checked((uint)BitOperations.Log2(VirtualPagesPerRow) + 1u);
    public uint PageTableEntriesPerView
    {
        get
        {
            uint result = 0u;
            for (uint mip = 0u; mip < MipLevelCount; mip++)
            {
                uint dimension = PageTableMipPagesPerRow(mip);
                result = checked(result + dimension * dimension);
            }
            return result;
        }
    }
    public uint PageHierarchyWordsPerView
    {
        get
        {
            uint result = 0u;
            for (uint mip = 0u; mip < MipLevelCount; mip++)
                result = checked(result + PageHierarchyMipWordCount(mip));
            return result;
        }
    }
    public uint PhysicalPagesPerRow => AtlasSize / PageSize;
    public uint MaxPhysicalPages => checked(PhysicalPagesPerRow * PhysicalPagesPerRow);

    public uint PageTableMipPagesPerRow(uint mip)
    {
        if (mip >= MipLevelCount)
            throw new ArgumentOutOfRangeException(nameof(mip));
        return Math.Max(VirtualPagesPerRow >> checked((int)mip), 1u);
    }

    public uint PageTableMipOffset(uint mip)
    {
        if (mip >= MipLevelCount)
            throw new ArgumentOutOfRangeException(nameof(mip));
        uint result = 0u;
        for (uint level = 0u; level < mip; level++)
        {
            uint dimension = PageTableMipPagesPerRow(level);
            result = checked(result + dimension * dimension);
        }
        return result;
    }

    public uint PageHierarchyMipOffset(uint mip)
    {
        if (mip >= MipLevelCount)
            throw new ArgumentOutOfRangeException(nameof(mip));
        uint result = 0u;
        for (uint level = 0u; level < mip; level++)
            result = checked(result + PageHierarchyMipWordCount(level));
        return result;
    }

    public uint PageHierarchyLevelOffset(uint mip, uint hierarchyLevel)
    {
        if (mip >= MipLevelCount)
            throw new ArgumentOutOfRangeException(nameof(mip));
        uint dimension = PageTableMipPagesPerRow(mip);
        uint levelCount = checked((uint)BitOperations.Log2(dimension) + 1u);
        if (hierarchyLevel >= levelCount)
            throw new ArgumentOutOfRangeException(nameof(hierarchyLevel));

        uint result = PageHierarchyMipOffset(mip);
        for (uint level = 0u; level < hierarchyLevel; level++)
        {
            result = checked(result +
                dimension * ((dimension + 31u) / 32u));
            dimension = Math.Max(dimension >> 1, 1u);
        }
        return result;
    }

    private uint PageHierarchyMipWordCount(uint mip)
    {
        uint dimension = PageTableMipPagesPerRow(mip);
        uint result = 0u;
        while (true)
        {
            result = checked(result +
                dimension * ((dimension + 31u) / 32u));
            if (dimension == 1u)
                return result;
            dimension >>= 1;
        }
    }

    public void Validate()
    {
        if (!float.IsFinite(WorldUnitsPerCentimeter) || WorldUnitsPerCentimeter <= 0.0f)
            throw new ArgumentOutOfRangeException(nameof(WorldUnitsPerCentimeter));
        if (!BitOperations.IsPow2(VirtualResolution) ||
            !BitOperations.IsPow2(AtlasSize) ||
            !BitOperations.IsPow2(PageSize) ||
            PageSize < 8u ||
            PageSize > AtlasSize ||
            AtlasSize > VirtualResolution ||
            VirtualResolution % PageSize != 0 ||
            AtlasSize % PageSize != 0)
        {
            throw new ArgumentException(
                "Virtual-shadow dimensions must be power-of-two page multiples ordered as page <= atlas <= virtual resolution.");
        }
        if (MaxPhysicalPages > (1u << 18))
            throw new ArgumentOutOfRangeException(nameof(AtlasSize), "Raster region indices require at most 262144 physical pages.");
        if (!float.IsFinite(DepthBias) || DepthBias < 0.0f)
            throw new ArgumentOutOfRangeException(nameof(DepthBias));
        if (!float.IsFinite(NormalBias) || NormalBias < 0.0f)
            throw new ArgumentOutOfRangeException(nameof(NormalBias));
        if (!float.IsFinite(DirectionalDepthRangeScale) || DirectionalDepthRangeScale <= 0.0f)
            throw new ArgumentOutOfRangeException(nameof(DirectionalDepthRangeScale));
        if (!float.IsFinite(LocalNearPlane) || LocalNearPlane <= 0.0f)
            throw new ArgumentOutOfRangeException(nameof(LocalNearPlane));
        if (!float.IsFinite(BlockerSearchRadiusTexels) || BlockerSearchRadiusTexels < 0.0f)
            throw new ArgumentOutOfRangeException(nameof(BlockerSearchRadiusTexels));
        if (!float.IsFinite(MaxFilterRadiusTexels) || MaxFilterRadiusTexels < 0.0f)
            throw new ArgumentOutOfRangeException(nameof(MaxFilterRadiusTexels));
        if (BlockerSampleCount is 0 or > 16)
            throw new ArgumentOutOfRangeException(nameof(BlockerSampleCount));
        if (FilterSampleCount is 0 or > 16)
            throw new ArgumentOutOfRangeException(nameof(FilterSampleCount));
        if (MaxShadowLights is 0 or > 16)
            throw new ArgumentOutOfRangeException(nameof(MaxShadowLights));
        if (DirectionalClipmapLevels is 0 or > 32)
            throw new ArgumentOutOfRangeException(nameof(DirectionalClipmapLevels));
        if (DirectionalFirstClipmapLevel is < -32 or > 32)
            throw new ArgumentOutOfRangeException(nameof(DirectionalFirstClipmapLevel));
        if (DirectionalFirstCoarseClipmapLevel is < -32 or > 32)
            throw new ArgumentOutOfRangeException(nameof(DirectionalFirstCoarseClipmapLevel));
        if (DirectionalLastCoarseClipmapLevel is < -32 or > 32 ||
            DirectionalLastCoarseClipmapLevel < DirectionalFirstCoarseClipmapLevel)
            throw new ArgumentOutOfRangeException(nameof(DirectionalLastCoarseClipmapLevel));
        if (!float.IsFinite(DirectionalResolutionLodBias))
            throw new ArgumentOutOfRangeException(nameof(DirectionalResolutionLodBias));
        if (!float.IsFinite(LocalResolutionLodBias))
            throw new ArgumentOutOfRangeException(nameof(LocalResolutionLodBias));
        if (!float.IsFinite(ShadowGeometryLodBias) || !float.IsFinite(ShadowGeometryLodScale) || ShadowGeometryLodScale <= 0.0f)
            throw new ArgumentOutOfRangeException(nameof(ShadowGeometryLodBias));
        if (!float.IsFinite(ShadowMaxPixelsPerEdge) || ShadowMaxPixelsPerEdge <= 0.0f)
            throw new ArgumentOutOfRangeException(nameof(ShadowMaxPixelsPerEdge));
        if (!float.IsFinite(ShadowMinPixelsPerEdgeHardware) || ShadowMinPixelsPerEdgeHardware <= 0.0f)
            throw new ArgumentOutOfRangeException(nameof(ShadowMinPixelsPerEdgeHardware));
        if (!float.IsFinite(ShadowDicingRate) || ShadowDicingRate <= 0.0f)
            throw new ArgumentOutOfRangeException(nameof(ShadowDicingRate));
        if (!float.IsFinite(WorkTimeBudgetMilliseconds) || WorkTimeBudgetMilliseconds < 0f)
            throw new ArgumentOutOfRangeException(nameof(WorkTimeBudgetMilliseconds));
        if (!float.IsFinite(WorkLoadBudget) || WorkLoadBudget < 0f)
            throw new ArgumentOutOfRangeException(nameof(WorkLoadBudget));
        if (!float.IsFinite(WorkHistoryWeight) || WorkHistoryWeight is < 0f or > 1f)
            throw new ArgumentOutOfRangeException(nameof(WorkHistoryWeight));
        if (!float.IsFinite(WorkMaxDirectionalLodBias) || WorkMaxDirectionalLodBias < 0f)
            throw new ArgumentOutOfRangeException(nameof(WorkMaxDirectionalLodBias));
        if (!float.IsFinite(WorkMaxLocalLodBias) || WorkMaxLocalLodBias < 0f)
            throw new ArgumentOutOfRangeException(nameof(WorkMaxLocalLodBias));
        if (!float.IsFinite(DynamicResolutionPagePoolLoadStart) ||
            DynamicResolutionPagePoolLoadStart < 0.0f ||
            DynamicResolutionPagePoolLoadStart >= 1.0f)
            throw new ArgumentOutOfRangeException(nameof(DynamicResolutionPagePoolLoadStart));
        if (!float.IsFinite(DynamicResolutionMaxLodBias) || DynamicResolutionMaxLodBias < 0f)
            throw new ArgumentOutOfRangeException(nameof(DynamicResolutionMaxLodBias));
        if (!float.IsFinite(DynamicResolutionMaxDirectionalLodBias) ||
            DynamicResolutionMaxDirectionalLodBias < 0.0f)
            throw new ArgumentOutOfRangeException(nameof(DynamicResolutionMaxDirectionalLodBias));
        if (!float.IsFinite(DynamicResolutionMaxLocalLodBias) ||
            DynamicResolutionMaxLocalLodBias < 0.0f)
            throw new ArgumentOutOfRangeException(nameof(DynamicResolutionMaxLocalLodBias));

        if (MipLevelCount > 16u)
            throw new ArgumentOutOfRangeException(nameof(VirtualResolution),
                "Virtual-shadow mip masks support at most 16 levels.");
        if (VirtualPagesPerRow < 4u || (VirtualPagesPerRow & 3u) != 0u)
            throw new ArgumentOutOfRangeException(nameof(VirtualResolution),
                "Directional clipmap scrolling requires a virtual page dimension divisible by four.");
    }
}
