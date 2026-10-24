#ifndef CLUSTER_DEFORM_SLANG
#define CLUSTER_DEFORM_SLANG

#include "vertex_deform_cache.slang"
#include "cluster_bin_common.slang"

struct ClusterDeformUniforms
{
    uint Capacity;
    uint MaxVisibleClusters;
    uint CurrentBin;
    uint Padding;
};

ConstantBuffer<ClusterDeformUniforms> DeformUniforms;
ByteAddressBuffer DeformDrawArgs;
ByteAddressBuffer DeformReadOffset0;
ByteAddressBuffer DeformReadOffset1;
ByteAddressBuffer DeformReadOffset2;
StructuredBuffer<uint> DeformRasterBins;
StructuredBuffer<DrawRequest> DeformVisibleClusters;
RWByteAddressBuffer DeformCacheWrite;
RWStructuredBuffer<uint2> DeformRegionsWrite;
RWStructuredBuffer<uint2> DeformRequests;
RWStructuredBuffer<uint> DeformPrefixOffsets;
RWStructuredBuffer<uint2> DeformPrefixGroups;
RWByteAddressBuffer DeformArguments;
RWStructuredBuffer<uint> DeformAllocation;

static const uint DEFORM_PREFIX_THREADS = 256u;
groupshared uint DeformPrefixScratch[DEFORM_PREFIX_THREADS];

uint DeformVisibleCount()
{
    CullDrawArgs args = LoadCullDrawArgs(DeformDrawArgs, 0u);
    return args.SWCount + args.HWCount;
}

uint DeformVisibleIndex(uint index)
{
    uint visible;
    bool software;
    DecodeVisibleCluster(DeformDrawArgs, DeformReadOffset0, DeformReadOffset1,
        DeformReadOffset2, DeformUniforms.MaxVisibleClusters, index, visible, software);
    return visible;
}

bool DeformMatches(DrawRequest request)
{
    uint clusterAddress = request.PageOffset + PageHeap.Load(request.PageOffset + 16u) +
        DrawRequestClusterID(request) * PACKED_CLUSTER_STRIDE_BYTES;
    uint material = LoadClusterMaterials(PageHeap, request.PageOffset, clusterAddress).x;
    uint first = LoadClusterMaterialSlotOffset(InstanceData,
        LoadRenderInstanceMetadata(InstanceProperties, CLUSTER_MATERIAL_SLOT_OFFSET_METADATA_WORD),
        request.InstanceID);
    return (DeformRasterBins[first + material] & 0x1FFFFFFFu) == DeformUniforms.CurrentBin;
}

uint AddDeformBytes(uint left, uint right)
{
    uint overflow = DeformUniforms.Capacity + 1u;
    return left >= overflow || right >= overflow - left ? overflow : left + right;
}

#ifndef CLUSTER_DEFORM_VERTEX_ENCODING
#define CLUSTER_DEFORM_VERTEX_ENCODING "someengine.cluster.positions.uint16.v1+someengine.cluster.attributes.standard.v1"
#endif

[VertexEncoding(CLUSTER_DEFORM_VERTEX_ENCODING)]
[shader("compute")]
[numthreads(1, 1, 1)]
void ClusterDeformArguments()
{
    uint count = DeformVisibleCount();
    StoreDispatchArgs(DeformArguments, 0u, DispatchArgs((count + 255u) / 256u, 1u, 1u));
    StoreDispatchArgs(DeformArguments, 12u,
        DispatchArgs(min(count, 65535u), max(1u, (count + 65534u) / 65535u), 1u));
}

[VertexEncoding(CLUSTER_DEFORM_VERTEX_ENCODING)]
[shader("compute")]
[numthreads(256, 1, 1)]
void ClusterDeformSize(uniform DeformEvaluate deform, uint3 id : SV_DispatchThreadID)
{
    uint index = id.x;
    if (index >= DeformVisibleCount()) return;
    uint visible = DeformVisibleIndex(index);
    DrawRequest request = DeformVisibleClusters[visible];
    uint2 allocation = 0u.xx;
    if (DeformMatches(request))
    {
        uint address, bytes, triangle, counts;
        LoadClusterInfo(PageHeap, request.PageOffset, DrawRequestClusterID(request),
            address, bytes, triangle, counts);
        uint count = counts & 255u;
        uint size = deform.getCacheByteSize(count);
        uint alignment = deform.getCacheByteAlignment();
        bool fits = alignment >= 4u && alignment <= DeformUniforms.Capacity &&
            (alignment & (alignment - 1u)) == 0u && size != 0u &&
            size <= DeformUniforms.Capacity - (alignment - 1u);
        allocation = fits ? uint2(size + alignment - 1u, alignment) : 0u.xx;
        DeformRegionsWrite[visible] = uint2(DEFORM_CACHE_OVERFLOW, count);
    }
    DeformRequests[index] = allocation;
}

// Reuse the size -> global prefix -> write allocator. Each phase appends regions;
// earlier views keep their bytes until every graph consumer has completed.
[VertexEncoding(CLUSTER_DEFORM_VERTEX_ENCODING)]
[shader("compute")]
[numthreads(256, 1, 1)]
void ClusterDeformPrefixLocal(uint3 group : SV_GroupID,
    uint3 id : SV_DispatchThreadID, uint lane : SV_GroupIndex)
{
    uint count = DeformVisibleCount();
    DeformPrefixScratch[lane] = id.x < count ? DeformRequests[id.x].x : 0u;
    GroupMemoryBarrierWithGroupSync();
    for (uint step = 1u; step < DEFORM_PREFIX_THREADS; step <<= 1u)
    {
        uint addend = lane >= step ? DeformPrefixScratch[lane - step] : 0u;
        GroupMemoryBarrierWithGroupSync();
        DeformPrefixScratch[lane] = AddDeformBytes(DeformPrefixScratch[lane], addend);
        GroupMemoryBarrierWithGroupSync();
    }
    if (id.x < count) DeformPrefixOffsets[id.x] = lane == 0u ? 0u : DeformPrefixScratch[lane - 1u];
    if (lane == 255u) DeformPrefixGroups[group.x].x = DeformPrefixScratch[lane];
}

[VertexEncoding(CLUSTER_DEFORM_VERTEX_ENCODING)]
[shader("compute")]
[numthreads(256, 1, 1)]
void ClusterDeformPrefixGroups(uint lane : SV_GroupIndex)
{
    uint count = (DeformVisibleCount() + 255u) / 256u;
    uint first = lane * 64u;
    uint sum = 0u;
    for (uint item = 0u; item < 64u; item++)
        if (first + item < count) sum = AddDeformBytes(sum, DeformPrefixGroups[first + item].x);
    DeformPrefixScratch[lane] = sum;
    GroupMemoryBarrierWithGroupSync();
    for (uint step = 1u; step < DEFORM_PREFIX_THREADS; step <<= 1u)
    {
        uint addend = lane >= step ? DeformPrefixScratch[lane - step] : 0u;
        GroupMemoryBarrierWithGroupSync();
        DeformPrefixScratch[lane] = AddDeformBytes(DeformPrefixScratch[lane], addend);
        GroupMemoryBarrierWithGroupSync();
    }
    uint initial = DeformAllocation[0];
    uint offset = AddDeformBytes(initial, lane == 0u ? 0u : DeformPrefixScratch[lane - 1u]);
    for (uint item = 0u; item < 64u; item++)
    {
        uint index = first + item;
        if (index < count)
        {
            DeformPrefixGroups[index].y = offset;
            offset = AddDeformBytes(offset, DeformPrefixGroups[index].x);
        }
    }
    if (lane == 255u)
        DeformAllocation[0] = min(DeformUniforms.Capacity, AddDeformBytes(initial, DeformPrefixScratch[lane]));
}

[VertexEncoding(CLUSTER_DEFORM_VERTEX_ENCODING)]
[shader("compute")]
[numthreads(256, 1, 1)]
void ClusterDeformOffsets(uint3 group : SV_GroupID, uint3 id : SV_DispatchThreadID)
{
    if (id.x >= DeformVisibleCount()) return;
    uint2 request = DeformRequests[id.x];
    if (request.x == 0u) return;
    uint offset = AddDeformBytes(DeformPrefixGroups[group.x].y, DeformPrefixOffsets[id.x]);
    if (offset > DeformUniforms.Capacity - request.x) return;
    uint visible = DeformVisibleIndex(id.x);
    DeformRegionsWrite[visible].x = (offset + request.y - 1u) & ~(request.y - 1u);
}

[VertexEncoding(CLUSTER_DEFORM_VERTEX_ENCODING)]
[shader("compute")]
[numthreads(64, 1, 1)]
void ClusterDeformWrite(uniform DeformEvaluate deform,
    uint3 group : SV_GroupID, uint lane : SV_GroupIndex)
{
    uint index = group.x + group.y * 65535u;
    if (index >= DeformVisibleCount() || DeformRequests[index].x == 0u) return;
    uint visible = DeformVisibleIndex(index);
    uint2 region = DeformRegionsWrite[visible];
    if (region.x == DEFORM_CACHE_OVERFLOW || lane >= region.y) return;
    DrawRequest request = DeformVisibleClusters[visible];
    uint address, bytes, triangle, counts;
    LoadClusterInfo(PageHeap, request.PageOffset, DrawRequestClusterID(request),
        address, bytes, triangle, counts);
    VertexEvalContext context;
    context.pageHeap = PageHeap;
    LoadVertexData(context, address);
    context.localVertexIndex = lane;
    context.visibleClusterIndex = visible;
    context.instanceID = request.InstanceID;
    context.transform = GetCurrentInstanceTransform(request.InstanceID);
    context.IsPreviousFrame = 0u;
    DeformEvaluate.Vertex current = deform.evaluate(context);
    context.transform = GetPreviousInstanceTransform(request.InstanceID);
    context.IsPreviousFrame = 1u;
    DeformEvaluate.Vertex previous = deform.evaluate(context);
    deform.writeCache(DeformCacheWrite, region.x, region.y, lane, current, previous);
}

#endif
