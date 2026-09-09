#ifndef CLUSTER_VIEW_ACCESS_SLANG
#define CLUSTER_VIEW_ACCESS_SLANG

#include "cluster_view.slang"

// Draw and traversal work keeps primary and policy-backed views in one 20-bit
// logical namespace without forcing their metadata into one maximum-sized buffer.
// The top bit selects the per-frame policy-view upload; the remaining bits are the
// dense index owned by that policy for this frame.
static const uint CLUSTER_POLICY_VIEW_BIT = 0x00080000u;
static const uint CLUSTER_RASTER_REGION_BIT = 0x00040000u;
static const uint CLUSTER_VIEW_INDEX_MASK = CLUSTER_RASTER_REGION_BIT - 1u;

uint ClusterEncodePolicyViewIndex(uint policyViewIndex)
{
    return CLUSTER_POLICY_VIEW_BIT | (policyViewIndex & CLUSTER_VIEW_INDEX_MASK);
}

bool ClusterIsPolicyViewIndex(uint viewIndex)
{
    return (viewIndex & CLUSTER_POLICY_VIEW_BIT) != 0u;
}

uint ClusterPhysicalViewIndex(uint viewIndex)
{
    return viewIndex & CLUSTER_VIEW_INDEX_MASK;
}

ClusterViewUniforms LoadClusterCullView(
    StructuredBuffer<ClusterViewUniforms> primaryViews,
    StructuredBuffer<ClusterViewUniforms> policyViews,
    uint viewIndex)
{
    return ClusterIsPolicyViewIndex(viewIndex)
        ? policyViews[ClusterPhysicalViewIndex(viewIndex)]
        : primaryViews[viewIndex];
}

// Defined beside the raster-region SRV so its binding stays with target resources.
ClusterViewUniforms LoadClusterView(
    StructuredBuffer<ClusterViewUniforms> primaryViews,
    StructuredBuffer<ClusterViewUniforms> policyViews,
    uint viewIndex);

#endif
