#include "resource_effects.slang"

[ResourceEffect(ResourceEffects.Write, ResourceQualifiers.None)]
RWByteAddressBuffer ActivePageCount;
[ResourceEffect(ResourceEffects.Write, ResourceQualifiers.None)]
RWStructuredBuffer<uint4> UncachedPageRectBounds;
[ResourceEffect(ResourceEffects.Write, ResourceQualifiers.None)]
RWStructuredBuffer<uint4> AllocatedPageRectBounds;
[ResourceEffect(ResourceEffects.Write, ResourceQualifiers.None)]
RWByteAddressBuffer RasterPageMasks;
[ResourceEffect(ResourceEffects.Write, ResourceQualifiers.None)]
RWByteAddressBuffer AllocatedPageHierarchy;
[ResourceEffect(ResourceEffects.Write, ResourceQualifiers.None)]
RWByteAddressBuffer ClusterDispatchArguments;
[ResourceEffect(ResourceEffects.Write, ResourceQualifiers.None)]
RWByteAddressBuffer PageDispatchArguments;
[ResourceEffect(ResourceEffects.Write, ResourceQualifiers.None)]
RWByteAddressBuffer PageRequestDispatchArguments;
[ResourceEffect(ResourceEffects.Write, ResourceQualifiers.None)]
RWByteAddressBuffer AllocationFailures;
[ResourceEffect(ResourceEffects.Write, ResourceQualifiers.None)]
RWByteAddressBuffer PolicyRasterWorkCount;

void ClearWord(RWByteAddressBuffer buffer, uint offset)
{
    uint bytes;
    buffer.GetDimensions(bytes);
    if (offset < bytes)
        buffer.Store(offset, 0u);
}

[shader("compute")]
[numthreads(256, 1, 1)]
void ResetVirtualShadowFrame(uint3 groupId : SV_GroupID, uint threadIndex : SV_GroupIndex)
{
    uint index = (groupId.y * 65535u + groupId.x) * 256u + threadIndex;
    uint offset = index * 4u;
    ClearWord(ActivePageCount, offset);
    ClearWord(RasterPageMasks, offset);
    ClearWord(AllocatedPageHierarchy, offset);
    ClearWord(ClusterDispatchArguments, offset);
    ClearWord(PageDispatchArguments, offset);
    ClearWord(PageRequestDispatchArguments, offset);
    ClearWord(AllocationFailures, offset);
    ClearWord(PolicyRasterWorkCount, offset);

    uint rectCount;
    uint rectStride;
    UncachedPageRectBounds.GetDimensions(rectCount, rectStride);
    if (index < rectCount)
    {
        UncachedPageRectBounds[index] = uint4(0xFFFFFFFFu, 0xFFFFFFFFu, 0u, 0u);
        AllocatedPageRectBounds[index] = uint4(0xFFFFFFFFu, 0xFFFFFFFFu, 0u, 0u);
    }
}
