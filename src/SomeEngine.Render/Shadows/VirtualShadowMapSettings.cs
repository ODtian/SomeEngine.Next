using System.Numerics;

namespace SomeEngine.Render.Shadows;

/// <summary>Virtual address-space, page-pool and atlas quality settings.</summary>
public sealed record VirtualShadowMapSettings
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
    public int DirectionalFirstCoarseClipmapLevel { get; init; } = 15;
    public int DirectionalLastCoarseClipmapLevel { get; init; } = 18;
    public float DirectionalResolutionLodBias { get; init; } = -2.0f;
    public float LocalResolutionLodBias { get; init; }
    public bool DynamicResolutionEnabled { get; init; }
    /// <summary>Raster budget in 8×8-page material-batch equivalents. HW contributes
    /// cropped page-batch area / 64; each bounded SW material batch contributes one.
    /// Four-page partitioning alone therefore cannot raise resolution pressure.</summary>
    public uint DynamicResolutionTargetRasterWork { get; init; } = 250_000;
    public float DynamicResolutionPagePoolLoadStart { get; init; } = 0.85f;
    public float DynamicResolutionMaxDirectionalLodBias { get; init; } = 2.0f;
    public float DynamicResolutionMaxLocalLodBias { get; init; } = 2.0f;
    public bool DirectionalReceiverMask { get; init; } = true;
    public bool LocalReceiverMask { get; init; } = true;
    // The receiver-plane comparison handles texel offsets. This remaining
    // normalized-depth allowance covers one millimeter over the default directional range.
    public float DepthBias { get; init; } = 0.000001f;
    public float NormalBias { get; init; } = 0.001f;
    public float DirectionalLightDistance { get; init; } = 500.0f;
    public float DirectionalNearPlane { get; init; } = 0.1f;
    public float DirectionalFarPlane { get; init; } = 1000.0f;
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
        if (!float.IsFinite(DirectionalLightDistance) || DirectionalLightDistance <= 0.0f)
            throw new ArgumentOutOfRangeException(nameof(DirectionalLightDistance));
        if (!float.IsFinite(DirectionalNearPlane) ||
            !float.IsFinite(DirectionalFarPlane) ||
            DirectionalNearPlane <= 0.0f ||
            DirectionalFarPlane <= DirectionalNearPlane)
        {
            throw new ArgumentOutOfRangeException(nameof(DirectionalFarPlane));
        }
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
        if (DynamicResolutionTargetRasterWork == 0u)
            throw new ArgumentOutOfRangeException(nameof(DynamicResolutionTargetRasterWork));
        if (!float.IsFinite(DynamicResolutionPagePoolLoadStart) ||
            DynamicResolutionPagePoolLoadStart < 0.0f ||
            DynamicResolutionPagePoolLoadStart >= 1.0f)
            throw new ArgumentOutOfRangeException(nameof(DynamicResolutionPagePoolLoadStart));
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
