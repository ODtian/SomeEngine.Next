#ifndef VSM_WORK_THROTTLE_SLANG
#define VSM_WORK_THROTTLE_SLANG

float CalculateVirtualShadowWorkCost(uint2 clusters)
{
    return 0.00012176 * float(clusters.x) + 0.00002700 * float(clusters.y);
}

float CalculateVirtualShadowWorkPressure(uint2 totalClusters, float previousPressure,
    float loadBudget, float dynamicResolutionStrength)
{
    if (dynamicResolutionStrength >= 0.0)
        return dynamicResolutionStrength;
    float modifier = clamp((CalculateVirtualShadowWorkCost(totalClusters) - loadBudget) * 0.5,
        -0.01, 0.2);
    return saturate(previousPressure + modifier);
}

float CalculateVirtualShadowViewWorkPressure(uint2 clusters, uint2 maximumClusters,
    float globalPressure, float previousPressure, float historyWeight)
{
    if (clusters.x + clusters.y == 0u)
        return globalPressure;
    float contribution = CalculateVirtualShadowWorkCost(clusters) /
        CalculateVirtualShadowWorkCost(maximumClusters);
    return lerp(saturate(contribution * globalPressure), previousPressure, historyWeight);
}

#endif
