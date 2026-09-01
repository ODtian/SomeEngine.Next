#ifndef FEATURE_VISIBLE_CLUSTERS_SLANG
#define FEATURE_VISIBLE_CLUSTERS_SLANG

#include "cluster_structures.slang"

struct DrawRequest
{
    uint PageOffset;
    uint ClusterID;
    uint InstanceID;
    uint ViewIndex;
};

uint ActiveVisibleClusterCount(
    ByteAddressBuffer phaseOneCounts,
    ByteAddressBuffer phaseTwoCounts,
    uint capacity)
{
    uint count = phaseOneCounts.Load(4u) + phaseOneCounts.Load(8u) +
                 phaseTwoCounts.Load(4u) + phaseTwoCounts.Load(8u);
    return min(count, capacity);
}

uint ActiveVisibleClusterIndex(
    ByteAddressBuffer phaseOneCounts,
    ByteAddressBuffer phaseTwoCounts,
    uint capacity,
    uint logicalIndex)
{
    uint sw1 = phaseOneCounts.Load(4u);
    uint hw1 = phaseOneCounts.Load(8u);
    uint sw2 = phaseTwoCounts.Load(4u);
    uint hw2 = phaseTwoCounts.Load(8u);
    if (logicalIndex < sw1)
        return logicalIndex;
    logicalIndex -= sw1;
    if (logicalIndex < sw2)
        return sw1 + logicalIndex;
    logicalIndex -= sw2;
    if (logicalIndex < hw1)
        return capacity - hw1 + logicalIndex;
    logicalIndex -= hw1;
    return capacity - hw1 - hw2 + logicalIndex;
}

#endif
