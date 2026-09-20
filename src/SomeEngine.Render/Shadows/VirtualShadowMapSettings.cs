using System.Numerics;

namespace SomeEngine.Render.Shadows;

/// <summary>Virtual address-space, page-pool and atlas quality settings.</summary>
public sealed record VirtualShadowMapSettings
{
    public bool Enabled { get; init; } = true;
    public uint VirtualResolution { get; init; } = 16_384;
    public uint AtlasSize { get; init; } = 4_096;
    public uint PageSize { get; init; } = 128;
    public uint CoarsePageShift => checked((uint)BitOperations.Log2(VirtualPagesPerRow));
    public uint MaxShadowLights { get; init; } = 4;
    public uint DirectionalClipmapLevels { get; init; } = 6;
    public int DirectionalFirstClipmapLevel { get; init; } = 6;
    public float DirectionalResolutionLodBias { get; init; } = -0.5f;
    public float DepthBias { get; init; } = 0.0005f;
    public float NormalBias { get; init; } = 0.01f;
    public float DirectionalWorldExtent { get; init; } = 256.0f;
    public float DirectionalLightDistance { get; init; } = 500.0f;
    public float DirectionalNearPlane { get; init; } = 0.1f;
    public float DirectionalFarPlane { get; init; } = 1000.0f;
    public float LocalNearPlane { get; init; } = 0.05f;
    public float BlockerSearchRadiusTexels { get; init; } = 4.0f;
    public float MaxFilterRadiusTexels { get; init; } = 8.0f;
    public uint BlockerSampleCount { get; init; } = 8;
    public uint FilterSampleCount { get; init; } = 16;

    public uint VirtualPagesPerRow => VirtualResolution / PageSize;
    public uint VirtualPagesPerView => checked(VirtualPagesPerRow * VirtualPagesPerRow);
    public uint CoarsePageScale => 1u << checked((int)CoarsePageShift);
    public uint CoarseActivePagesPerRow =>
        checked(((VirtualPagesPerRow + CoarsePageScale - 1u) / CoarsePageScale) + 1u);
    public uint CoarseTablePagesPerRow =>
        BitOperations.RoundUpToPowerOf2(CoarseActivePagesPerRow);
    public uint CoarsePagesPerView => checked(CoarseTablePagesPerRow * CoarseTablePagesPerRow);
    public uint PhysicalPagesPerRow => AtlasSize / PageSize;
    public uint MaxPhysicalPages => checked(PhysicalPagesPerRow * PhysicalPagesPerRow);

    public void Validate()
    {
        if (!BitOperations.IsPow2(VirtualResolution) ||
            !BitOperations.IsPow2(AtlasSize) ||
            !BitOperations.IsPow2(PageSize) ||
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
        if (!float.IsFinite(DirectionalWorldExtent) || DirectionalWorldExtent <= 0.0f)
            throw new ArgumentOutOfRangeException(nameof(DirectionalWorldExtent));
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
        if (DirectionalClipmapLevels is 0 or > 16)
            throw new ArgumentOutOfRangeException(nameof(DirectionalClipmapLevels));
        if (DirectionalFirstClipmapLevel is < -32 or > 32)
            throw new ArgumentOutOfRangeException(nameof(DirectionalFirstClipmapLevel));
        if (!float.IsFinite(DirectionalResolutionLodBias))
            throw new ArgumentOutOfRangeException(nameof(DirectionalResolutionLodBias));

        if (VirtualPagesPerRow < 2u)
            throw new ArgumentOutOfRangeException(nameof(VirtualResolution),
                "Virtual shadows require a finer level above the full-projection fallback page.");
    }
}
