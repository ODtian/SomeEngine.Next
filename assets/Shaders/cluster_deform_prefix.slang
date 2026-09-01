#include "cluster_structures.slang"
#include "resource_effects.slang"

struct DeformPrefixUniforms
{
    uint MaxDeformCacheBytes;
    uint Pad0;
    uint Pad1;
    uint Pad2;
};

ConstantBuffer<DeformPrefixUniforms> Uniforms;
StructuredBuffer<uint> DeformReserveCounters;
StructuredBuffer<uint2> DeformBinnedClusterIndexBuffer;
[ResourceEffect(ResourceEffects.ReadWrite, ResourceQualifiers.None)]
RWStructuredBuffer<uint2> DeformCacheRequests;
[ResourceEffect(ResourceEffects.Write, ResourceQualifiers.None)]
RWStructuredBuffer<uint> DeformCachePrefixOffsets;
[ResourceEffect(ResourceEffects.ReadWrite, ResourceQualifiers.None)]
RWStructuredBuffer<uint2> DeformCachePrefixGroups;
[ResourceEffect(ResourceEffects.Write, ResourceQualifiers.None)]
RWStructuredBuffer<uint> CacheOffsetsWrite;
[ResourceEffect(ResourceEffects.ReadWrite, ResourceQualifiers.Atomic)]
RWStructuredBuffer<uint> CacheAllocationCounter;

static const uint CACHE_OFFSET_OVERFLOW = 0xFFFFFFFDu;
static const uint PREFIX_ITEMS_PER_THREAD = 16u;

groupshared uint PrefixScratch[DEFORM_CACHE_PREFIX_THREAD_COUNT];
groupshared uint CachedClusterFlags[DEFORM_CACHE_PREFIX_THREAD_COUNT];

uint SaturatingCacheAdd(uint left, uint right)
{
    uint overflow = Uniforms.MaxDeformCacheBytes + 1u;
    return left >= overflow || right >= overflow - left
        ? overflow
        : left + right;
}

void KeepPrefixBindingsAlive(uint lane)
{
    if (Uniforms.MaxDeformCacheBytes == 0xFFFFFFFFu && lane == 0u)
    {
        uint value = DeformReserveCounters[0] + DeformBinnedClusterIndexBuffer[0].x;
        uint2 request = DeformCacheRequests[0];
        DeformCacheRequests[0] = request + uint2(value, 0u);
        DeformCachePrefixOffsets[0] = value;
        DeformCachePrefixGroups[0] = uint2(value, value);
        CacheOffsetsWrite[0] = value;
        CacheAllocationCounter[0] = value;
    }
}

[shader("compute")]
[numthreads(DEFORM_CACHE_PREFIX_THREAD_COUNT, 1, 1)]
void PrefixDeformCacheLocal(
    uint3 groupID : SV_GroupID,
    uint3 dispatchThreadID : SV_DispatchThreadID,
    uint groupThreadIndex : SV_GroupIndex)
{
    KeepPrefixBindingsAlive(groupThreadIndex);
    uint requestCount = DeformReserveCounters[0];
    uint requestIndex = dispatchThreadID.x;
    uint reservationBytes = requestIndex < requestCount
        ? DeformCacheRequests[requestIndex].x
        : 0u;
    PrefixScratch[groupThreadIndex] = reservationBytes;
    GroupMemoryBarrierWithGroupSync();

    for (uint step = 1u; step < DEFORM_CACHE_PREFIX_THREAD_COUNT; step <<= 1u)
    {
        uint addend = groupThreadIndex >= step
            ? PrefixScratch[groupThreadIndex - step]
            : 0u;
        GroupMemoryBarrierWithGroupSync();
        PrefixScratch[groupThreadIndex] = SaturatingCacheAdd(
            PrefixScratch[groupThreadIndex],
            addend);
        GroupMemoryBarrierWithGroupSync();
    }

    if (requestIndex < requestCount)
    {
        DeformCachePrefixOffsets[requestIndex] = groupThreadIndex == 0u
            ? 0u
            : PrefixScratch[groupThreadIndex - 1u];
    }
    if (groupThreadIndex == DEFORM_CACHE_PREFIX_THREAD_COUNT - 1u)
        DeformCachePrefixGroups[groupID.x].x = PrefixScratch[groupThreadIndex];
}

[shader("compute")]
[numthreads(DEFORM_CACHE_PREFIX_THREAD_COUNT, 1, 1)]
void PrefixDeformCacheGroups(uint groupThreadIndex : SV_GroupIndex)
{
    KeepPrefixBindingsAlive(groupThreadIndex);
    uint requestCount = DeformReserveCounters[0];
    uint groupCount =
        (requestCount + DEFORM_CACHE_PREFIX_THREAD_COUNT - 1u) /
        DEFORM_CACHE_PREFIX_THREAD_COUNT;
    uint firstGroup = groupThreadIndex * PREFIX_ITEMS_PER_THREAD;
    uint subtotal = 0u;
    for (uint item = 0u; item < PREFIX_ITEMS_PER_THREAD; item++)
    {
        uint groupIndex = firstGroup + item;
        if (groupIndex < groupCount)
            subtotal = SaturatingCacheAdd(subtotal, DeformCachePrefixGroups[groupIndex].x);
    }
    PrefixScratch[groupThreadIndex] = subtotal;
    GroupMemoryBarrierWithGroupSync();

    for (uint step = 1u; step < DEFORM_CACHE_PREFIX_THREAD_COUNT; step <<= 1u)
    {
        uint addend = groupThreadIndex >= step
            ? PrefixScratch[groupThreadIndex - step]
            : 0u;
        GroupMemoryBarrierWithGroupSync();
        PrefixScratch[groupThreadIndex] = SaturatingCacheAdd(
            PrefixScratch[groupThreadIndex],
            addend);
        GroupMemoryBarrierWithGroupSync();
    }

    uint initialBase = CacheAllocationCounter[0];
    uint running = SaturatingCacheAdd(
        initialBase,
        groupThreadIndex == 0u ? 0u : PrefixScratch[groupThreadIndex - 1u]);
    for (uint item = 0u; item < PREFIX_ITEMS_PER_THREAD; item++)
    {
        uint groupIndex = firstGroup + item;
        if (groupIndex < groupCount)
        {
            DeformCachePrefixGroups[groupIndex].y = running;
            running = SaturatingCacheAdd(
                running,
                DeformCachePrefixGroups[groupIndex].x);
        }
    }

    if (groupThreadIndex == DEFORM_CACHE_PREFIX_THREAD_COUNT - 1u)
    {
        CacheAllocationCounter[0] = min(
            Uniforms.MaxDeformCacheBytes,
            SaturatingCacheAdd(initialBase, PrefixScratch[groupThreadIndex]));
    }
}

[shader("compute")]
[numthreads(DEFORM_CACHE_PREFIX_THREAD_COUNT, 1, 1)]
void WriteDeformCacheOffsets(
    uint3 groupID : SV_GroupID,
    uint3 dispatchThreadID : SV_DispatchThreadID,
    uint groupThreadIndex : SV_GroupIndex)
{
    KeepPrefixBindingsAlive(groupThreadIndex);
    uint requestCount = DeformReserveCounters[0];
    uint requestIndex = dispatchThreadID.x;
    bool cached = false;
    if (requestIndex < requestCount)
    {
        uint2 request = DeformCacheRequests[requestIndex];
        uint reservationBytes = request.x;
        uint alignment = request.y;
        uint slotBase = SaturatingCacheAdd(
            DeformCachePrefixGroups[groupID.x].y,
            DeformCachePrefixOffsets[requestIndex]);
        bool validRequest =
            reservationBytes != 0u &&
            alignment >= 4u &&
            (alignment & (alignment - 1u)) == 0u;
        cached = validRequest &&
            reservationBytes <= Uniforms.MaxDeformCacheBytes &&
            slotBase <= Uniforms.MaxDeformCacheBytes - reservationBytes;
        uint visibleIndex = DeformBinnedClusterIndexBuffer[requestIndex].x;
        CacheOffsetsWrite[visibleIndex] = cached
            ? (slotBase + alignment - 1u) & ~(alignment - 1u)
            : CACHE_OFFSET_OVERFLOW;
    }

    CachedClusterFlags[groupThreadIndex] = cached ? 1u : 0u;
    GroupMemoryBarrierWithGroupSync();
    for (uint step = DEFORM_CACHE_PREFIX_THREAD_COUNT / 2u; step != 0u; step >>= 1u)
    {
        if (groupThreadIndex < step)
            CachedClusterFlags[groupThreadIndex] += CachedClusterFlags[groupThreadIndex + step];
        GroupMemoryBarrierWithGroupSync();
    }
    if (groupThreadIndex == 0u && CachedClusterFlags[0] != 0u)
    {
        uint unused;
        InterlockedAdd(CacheAllocationCounter[1], CachedClusterFlags[0], unused);
    }
}
