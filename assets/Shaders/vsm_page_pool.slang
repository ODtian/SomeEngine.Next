#include "resource_effects.slang"
#include "wave_aggregation.slang"

struct VirtualShadowPagePoolUniforms
{
    uint MaxPhysicalPages;
    uint PageTableEntryCount;
    uint FinePageTableEntryCount;
    uint FrameIndex;
    uint EvictionBucketCount;
    uint Pad0;
    uint Pad1;
    uint Pad2;
};

ConstantBuffer<VirtualShadowPagePoolUniforms> PagePool;
StructuredBuffer<uint> RequestedShadowPages;
[ResourceEffect(ResourceEffects.ReadWrite, ResourceQualifiers.Atomic)]
RWStructuredBuffer<uint> PhysicalPageOwners;
[ResourceEffect(ResourceEffects.ReadWrite, ResourceQualifiers.Atomic)]
RWStructuredBuffer<uint4> PageMetadata;
[ResourceEffect(ResourceEffects.Write, ResourceQualifiers.None)]
RWStructuredBuffer<uint> FreePages;
[ResourceEffect(ResourceEffects.ReadWrite, ResourceQualifiers.Atomic)]
RWStructuredBuffer<uint> FreePageCount;
[ResourceEffect(ResourceEffects.Write, ResourceQualifiers.None)]
RWStructuredBuffer<uint> EvictionPages;
[ResourceEffect(ResourceEffects.ReadWrite, ResourceQualifiers.Atomic)]
RWStructuredBuffer<uint> EvictionBucketCounts;

static const uint VSM_UNOWNED_PAGE = 0xFFFFFFFFu;

[shader("compute")]
[numthreads(64, 1, 1)]
void BuildVirtualShadowPagePool(uint3 threadId : SV_DispatchThreadID)
{
    uint atlasPage = threadId.x;
    if (atlasPage >= PagePool.MaxPhysicalPages)
        return;
    uint owner = PhysicalPageOwners[atlasPage];
    if (owner == VSM_UNOWNED_PAGE || owner >= PagePool.PageTableEntryCount)
    {
        uint index = 0u;
        uint count = WaveActiveCountBits(true);
        if (WaveIsFirstLane())
            InterlockedAdd(FreePageCount[0], count, index);
        index = WaveReadLaneFirst(index) + WavePrefixCountBits(true);
        if (index < PagePool.MaxPhysicalPages)
            FreePages[index] = atlasPage;
        return;
    }

    bool requested = RequestedShadowPages[owner] == PagePool.FrameIndex;
    if (requested)
        return;

    uint lastUsed = PageMetadata[atlasPage].x;
    uint age = PagePool.FrameIndex - lastUsed;
    uint bucket = min(PagePool.EvictionBucketCount - 1u, age >> 2u);
    uint4 matching = WaveMatchingLanes(bucket);
    uint leader = WaveMaskFirstLane(matching);
    uint local = 0u;
    if (WaveGetLaneIndex() == leader)
        InterlockedAdd(EvictionBucketCounts[bucket], WaveMaskBitCount(matching), local);
    local = WaveReadLaneAt(local, leader) + WaveMaskPrefix(matching, WaveGetLaneIndex());
    if (local < PagePool.MaxPhysicalPages)
        EvictionPages[bucket * PagePool.MaxPhysicalPages + local] = atlasPage;
}
