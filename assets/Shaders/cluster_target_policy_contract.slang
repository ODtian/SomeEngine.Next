#ifndef CLUSTER_TARGET_POLICY_CONTRACT_SLANG
#define CLUSTER_TARGET_POLICY_CONTRACT_SLANG

#include "cluster_bvh_cull_common.slang"

uint LoadTargetDepthCacheClass(uint instanceId);

// Cluster core code depends only on conservative policy operations. The shader
// asset that instantiates the core supplies the concrete target representation
// and every feature-specific resource.
bool ClusterTargetPolicyAccepts(
    ClusterProjectedBounds projectedBounds,
    ClusterViewUniforms view, uint depthCacheClass);

bool ClusterTargetPolicyWorldDepthAccepts(
    float3 worldMin,
    float3 worldMax,
    ClusterViewUniforms view,
    uint depthCacheClass);

bool ClusterTargetPolicyScreenBoundsOccluded(
    float2 minUv,
    float2 maxUv,
    float nearDepth,
    bool usePrevious,
    ClusterViewUniforms view,
    uint depthCacheClass = 1u);

bool ClusterTargetPolicyScreenBoundsAccepts(
    float2 minUv,
    float2 maxUv,
    ClusterViewUniforms view, uint depthCacheClass = 2u, uint requiredPageFlags = 0u);

bool ClusterTargetPolicyRasterBoundsWindow(
    float2 minUv,
    float2 maxUv,
    ClusterViewUniforms view,
    out uint2 rasterWindow, uint depthCacheClass = 0u, uint requiredPageFlags = 0u);

uint ClusterTargetPolicyRequiredPageFlags(float nearDepth, ClusterViewUniforms view);

bool ClusterTargetPolicyWorldBoundsOccluded(
    float3 worldMin,
    float3 worldMax,
    bool usePrevious,
    ClusterViewUniforms view,
    uint depthCacheClass = 1u);

#endif
