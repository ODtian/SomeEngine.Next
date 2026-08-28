using System.Numerics;

namespace SomeEngine.Render.Shadows;

/// <summary>Persistent virtual address-space and physical-atlas policy for virtual shadows.</summary>
public sealed record VirtualShadowMapSettings
{
    public bool Enabled { get; init; } = true;
    public uint VirtualResolution { get; init; } = 16_384;
    public uint AtlasSize { get; init; } = 4_096;
    public uint PageSize { get; init; } = 128;
    public uint MaxShadowLights { get; init; } = 4;
    public uint DirectionalClipmapLevels { get; init; } = 6;
    public int DirectionalFirstClipmapLevel { get; init; } = 6;
    public float DirectionalResolutionLodBias { get; init; } = -0.5f;
    public float DepthBias { get; init; } = 0.0005f;
    public float DirectionalWorldExtent { get; init; } = 256.0f;
    public float DirectionalLightDistance { get; init; } = 500.0f;
    public float DirectionalNearPlane { get; init; } = 0.1f;
    public float DirectionalFarPlane { get; init; } = 1000.0f;
    /// <summary>
    /// Upper bound for uncached physical pages admitted for clear and raster in one frame.
    /// Keeping this below the complete atlas prevents a cold cache from turning one frame into an
    /// unbounded caster traversal; requested pages that miss the budget remain unmapped and are
    /// naturally retried by receiver feedback on following frames.
    /// </summary>
    public uint MaxPagesToRasterPerFrame { get; init; } = 256;

    /// <summary>
    /// Cold-cache/reset budget. Resetting the page table already discards cache reuse, so spreading
    /// a 1024-page default atlas across hundreds of frames only creates visible holes and repeated
    /// traversal. The full default pool is therefore admitted in one reset frame.
    /// </summary>
    public uint ColdStartPagesToRasterPerFrame { get; init; } = 1_024;
    /// <summary>
    /// Maximum stale-owner probes performed by one failed physical-page allocation. Unreal's VSM
    /// implementation uses compact available/LRU lists; this bounded probe is the equivalent
    /// invariant for the smaller allocator and avoids O(requests * physical-pages) cold-start work.
    /// </summary>
    public uint PhysicalPageEvictionProbeCount { get; init; } = 16;

    public uint VirtualPagesPerRow => VirtualResolution / PageSize;
    public uint VirtualPagesPerView => checked(VirtualPagesPerRow * VirtualPagesPerRow);
    public uint MaxShadowViews => checked(MaxShadowLights * DirectionalClipmapLevels);
    public uint PageTableEntryCount => checked(VirtualPagesPerView * MaxShadowViews);
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
        if (!float.IsFinite(DepthBias) || DepthBias < 0.0f)
            throw new ArgumentOutOfRangeException(nameof(DepthBias));
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
        if (MaxShadowLights is 0 or > 16)
            throw new ArgumentOutOfRangeException(nameof(MaxShadowLights));
        if (DirectionalClipmapLevels is 0 or > 16 || MaxShadowViews > 32)
        {
            throw new ArgumentOutOfRangeException(
                nameof(DirectionalClipmapLevels),
                "Directional shadow clipmaps must fit the 32-view caster mask.");
        }
        if (DirectionalFirstClipmapLevel is < -32 or > 32)
            throw new ArgumentOutOfRangeException(nameof(DirectionalFirstClipmapLevel));
        if (!float.IsFinite(DirectionalResolutionLodBias))
            throw new ArgumentOutOfRangeException(nameof(DirectionalResolutionLodBias));
        if (MaxPagesToRasterPerFrame is 0 || MaxPagesToRasterPerFrame > MaxPhysicalPages)
            throw new ArgumentOutOfRangeException(nameof(MaxPagesToRasterPerFrame));
        if (ColdStartPagesToRasterPerFrame is 0 ||
            ColdStartPagesToRasterPerFrame > MaxPhysicalPages ||
            ColdStartPagesToRasterPerFrame < MaxPagesToRasterPerFrame)
        {
            throw new ArgumentOutOfRangeException(nameof(ColdStartPagesToRasterPerFrame));
        }
        if (PhysicalPageEvictionProbeCount is 0 ||
            PhysicalPageEvictionProbeCount > MaxPhysicalPages)
        {
            throw new ArgumentOutOfRangeException(nameof(PhysicalPageEvictionProbeCount));
        }
        if (MaxPhysicalPages > 2047u)
        {
            throw new ArgumentOutOfRangeException(
                nameof(AtlasSize),
                "The virtual-shadow page entry reserves eleven bits for the physical page.");
        }
    }
}
