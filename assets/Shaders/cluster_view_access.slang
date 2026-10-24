#ifndef CLUSTER_VIEW_ACCESS_SLANG
#define CLUSTER_VIEW_ACCESS_SLANG

#include "cluster_view.slang"

// Draw and traversal work keeps primary and policy-backed views in one 20-bit
// index space without forcing their metadata into one maximum-sized buffer.
// The top bit selects the per-frame policy-view upload; the remaining bits are
// the dense view index for this frame.
static const uint CLUSTER_POLICY_VIEW_BIT = 0x00080000u;
static const uint CLUSTER_VIEW_INDEX_MASK = CLUSTER_POLICY_VIEW_BIT - 1u;

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
    uint viewIndex, bool targetFilters = true)
{
    // Views without target filters are uploaded with TargetPolicy zero.
    if (!targetFilters)
    {
        ClusterViewUniforms view = primaryViews[viewIndex];
        view.TargetPolicy = 0u;
        return view;
    }
    return ClusterIsPolicyViewIndex(viewIndex)
        ? policyViews[ClusterPhysicalViewIndex(viewIndex)]
        : primaryViews[viewIndex];
}

ClusterViewUniforms LoadClusterView(
    StructuredBuffer<ClusterViewUniforms> primaryViews,
    StructuredBuffer<ClusterViewUniforms> policyViews,
    uint viewIndex)
{
    return LoadClusterCullView(primaryViews, policyViews, viewIndex);
}

#endif
