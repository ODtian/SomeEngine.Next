#include "vsm_sampling.slang"
#include "vsm_raster_work_common.slang"
#include "cluster_view.slang"
#include "cluster_raster_target_view.slang"
#include "resource_effects.slang"

ConstantBuffer<VirtualShadowRasterWorkUniforms> ViewCompact;
StructuredBuffer<uint4> UncachedPageRectBounds;
StructuredBuffer<uint> ActivePhysicalPageCount;
StructuredBuffer<VirtualShadowMapParameters> ShadowViews;
StructuredBuffer<ClusterViewUniforms> ClusterSourceViews;
[ResourceEffect(ResourceEffects.Write, ResourceQualifiers.None)]
RWStructuredBuffer<ClusterViewUniforms> RasterViews;
[ResourceEffect(ResourceEffects.Write, ResourceQualifiers.None)]
RWStructuredBuffer<ClusterRasterTargetView> RasterTargetViews;
[ResourceEffect(ResourceEffects.ReadWrite, ResourceQualifiers.Atomic)]
RWByteAddressBuffer ClusterDispatchArguments;
[ResourceEffect(ResourceEffects.Write, ResourceQualifiers.None)]
RWByteAddressBuffer ShadowPageWorkDispatchArguments;

[shader("compute")]
[numthreads(64, 1, 1)]
void CompactVirtualShadowViews(uint3 threadId : SV_DispatchThreadID)
{
    uint pairIndex = threadId.x;
    uint pairCount = ViewCompact.ViewCount * ViewCompact.MipLevelCount;
    if (pairIndex == 0u)
    {
        uint activePageCount = min(
            ActivePhysicalPageCount[0],
            ViewCompact.ActivePageCapacity);
        for (uint mip = 0u; mip < ViewCompact.PageHiZMipCount; mip++)
        {
            uint mipSize = max(max(ViewCompact.PageSize / 2u, 1u) >> mip, 1u);
            uint tiles = (mipSize + 7u) / 8u;
            uint offset = mip * 16u;
            ShadowPageWorkDispatchArguments.Store(offset, activePageCount);
            ShadowPageWorkDispatchArguments.Store(offset + 4u, tiles);
            ShadowPageWorkDispatchArguments.Store(offset + 8u, tiles);
            ShadowPageWorkDispatchArguments.Store(offset + 12u, 0u);
        }
        ClusterDispatchArguments.Store(0u, ViewCompact.InstanceGroupCount);
        ClusterDispatchArguments.Store(8u, 1u);
    }

    if (pairIndex >= pairCount)
        return;

    uint sourceViewIndex = pairIndex / ViewCompact.MipLevelCount;
    uint mipLevel = pairIndex % ViewCompact.MipLevelCount;
    uint4 uncachedRect = UncachedPageRectBounds[pairIndex];
    if (any(uncachedRect.zw < uncachedRect.xy))
        return;

    uint compactIndex;
    ClusterDispatchArguments.InterlockedAdd(12u, 1u, compactIndex);
    uint ignored;
    ClusterDispatchArguments.InterlockedMax(
        4u,
        (compactIndex + 32u) / 32u,
        ignored);

    VirtualShadowMapParameters source = ShadowViews[sourceViewIndex];
    ClusterViewUniforms view = ClusterSourceViews[sourceViewIndex];

    uint dimension = VirtualShadowMipPagesPerRow(source, mipLevel);
    uint mipResolution = VirtualShadowMipResolution(source, mipLevel);
    // A virtual-shadow mip lowers texel density over the same light frustum.
    // Keep the source projection unchanged and express the mip only through
    // its raster dimensions so culling and LOD see the correct pixel scale.
    view.ScreenWidth = mipResolution;
    view.ScreenHeight = mipResolution;
    view.HiZMipCount = ViewCompact.PageHiZMipCount;
    view.HiZInvSize = 2.0.xx / float(max(mipResolution, 2u));
    view.TargetPolicy = 1u;
    view.GeometryViewIndex = 1u;
    view.TargetViewIndex = compactIndex;
    view.CullingProjectionIndex = sourceViewIndex + 1u;
    // A cube-face far plane also includes corners beyond the local light's sphere.
    // Such casters cannot occlude receivers inside the light's actual influence.
    view.CullingRadius = (source.LightKind == VSM_LIGHT_POINT || source.LightKind == VSM_LIGHT_SPOT)
        ? source.LightRange : 0.0;
    if (view.CullingRadius > 0.0) view.CameraPos = source.LightPosition;
    view.HasPrevHistory =
        ViewCompact.HasPreviousHistory != 0u &&
        (source.Flags & VSM_VIEW_PREVIOUS_PROJECTION_VALID) != 0u;
    RasterViews[compactIndex] = view;

    int2 worldOrigin = int2(
        source.VirtualPageOriginX,
        source.VirtualPageOriginY) >> mipLevel;
    uint pageMask = dimension - 1u;

    ClusterRasterTargetView target;
    target.PageTableBase =
        source.PageTableOffset + VirtualShadowMipOffset(source, mipLevel);
    target.PageHierarchyBase =
        sourceViewIndex * ViewCompact.HierarchyWordsPerView +
        VirtualShadowPageHierarchyMipOffset(source, mipLevel);
    target.PagesPerRow = dimension;
    target.PageSize = source.PageSize;
    target.MipResolution = mipResolution;
    target.PhysicalPagesPerRow = source.PhysicalPagesPerRow;
    target.CacheGeneration = source.CacheGeneration;
    target.MipLevel = mipLevel;
    target.WorldPageOriginX = worldOrigin.x;
    target.WorldPageOriginY = worldOrigin.y;
    target.TableOriginX = asuint(worldOrigin.x) & pageMask;
    target.TableOriginY = asuint(worldOrigin.y) & pageMask;
    target.ReceiverMaskEnabled =
        (source.Flags & VSM_VIEW_USE_RECEIVER_MASK) != 0u ? 1u : 0u;
    target.RasterWindowPages = 8u;
    target.Padding = uint2(0, 0);
    target.UncachedPageRect = uncachedRect;
    RasterTargetViews[compactIndex] = target;
}
