#ifndef CLUSTER_STRUCTURES_SLANG
#define CLUSTER_STRUCTURES_SLANG

static const uint PACKED_CLUSTER_STRIDE_BYTES = 96;
static const uint SLOT_INVALID = 0xFFFFu;
static const uint BIN_INVALID = 0xFFFFFFFFu;

struct PackedCluster
{
    uint VertexBlobOffset;              //  0 — relative to the page header's VertexBlobsOffset
    uint VertexBlobSize;                //  4
    uint ExtensionOffset;               //  8 — page-relative, 0xFFFFFFFF when absent
    uint ExtensionSize;                 // 12
    float3 LODCenter;                   // 16
    float LODRadius;                    // 28
    float3 BoundMin;                    // 32 — conservative object-space AABB min
    float3 BoundMax;                    // 44 — conservative object-space AABB max
    uint PackedLodErrorTriangleStart;   // 56 — LODErrorHalf:16 | TriangleStart:16
    uint PackedCounts;                  // 60 — [VertexCount:8][TriangleCount:8][LODLevel:8][Reserved:8]
    uint PackedGroup;                   // 64 — GroupId(i16):16 | Reserved:16
    uint PackedMaterials;               // 68 — [mat0:8][mat1:8][mat2:8][Reserved:8]
    uint PackedRanges;                  // 72 — [range0End:8][range1End:8][Reserved:16]
    uint MaterialTableOffset;           // 76 — must be 0xFFFFFFFF
    uint VertReuseBatchWord0;           // 80
    uint VertReuseBatchWord1;           // 84
    uint VertReuseBatchWord2;           // 88
    uint VertReuseBatchWord3;           // 92
};

float3 GetClusterCenter(PackedCluster c)
{
    return (c.BoundMin + c.BoundMax) * 0.5;
}

float GetClusterRadius(PackedCluster c)
{
    return length(c.BoundMax - c.BoundMin) * 0.5;
}

float DecodeClusterLODError(PackedCluster c)
{
    uint lodErrBits = c.PackedLodErrorTriangleStart & 0xFFFF;
    return f16tof32(lodErrBits);
}

uint DecodeTriangleStart(PackedCluster c)
{
    return c.PackedLodErrorTriangleStart >> 16;
}

int DecodeGroupId(PackedCluster c)
{
    // Sign-extend from 16-bit
    int raw = int(c.PackedGroup & 0xFFFFu);
    return (raw << 16) >> 16;
}

uint4 VertReuseBatchWords(PackedCluster cluster)
{
    return uint4(
        cluster.VertReuseBatchWord0,
        cluster.VertReuseBatchWord1,
        cluster.VertReuseBatchWord2,
        cluster.VertReuseBatchWord3);
}

uint ReadVertReuseBits(uint4 words, uint bitOffset, uint bitCount)
{
    uint wordIndex = bitOffset >> 5u;
    uint wordBit = bitOffset & 31u;
    uint lowBits = min(bitCount, 32u - wordBit);
    uint value = words[wordIndex] >> wordBit;
    if (lowBits < bitCount)
        value |= words[wordIndex + 1u] << lowBits;
    return value & ((1u << bitCount) - 1u);
}

uint VertReuseBatchCount(uint4 words, uint materialRangeIndex)
{
    return ReadVertReuseBits(words, materialRangeIndex * 4u, 4u);
}

uint VertReuseBatchTriangleCount(uint4 words, uint flatBatchIndex)
{
    return ReadVertReuseBits(words, 12u + flatBatchIndex * 5u, 5u) + 1u;
}

uint MaterialRangeCount(uint packedRanges, uint triangleCount)
{
    uint range0End = packedRanges & 0xFFu;
    uint range1End = (packedRanges >> 8u) & 0xFFu;
    return range0End == triangleCount ? 1u : (range1End == triangleCount ? 2u : 3u);
}

uint MaterialRangeEnd(uint packedRanges, uint triangleCount, uint rangeIndex)
{
    if (rangeIndex == 0u)
        return min(packedRanges & 0xFFu, triangleCount);
    if (rangeIndex == 1u)
        return min((packedRanges >> 8u) & 0xFFu, triangleCount);
    return triangleCount;
}
uint DecodeTriangleCount(PackedCluster c)
{
    return (c.PackedCounts >> 8) & 0xFF;
}

struct ClusterBVHNode
{
    float4 BoundMin;
    float4 BoundMax;
    float4 LODSphere; // xyz: Center, w: Radius
    float LODError;
    uint ChildPointer;
    uint ChildCount;
    uint NodeType; // 0=Internal, 1=Leaf
};

// Each stage owns an independent dense material-slot mapping. The Cluster instance ABI stores
// only the assignment-sequence offset; stage compatibility never enters the instance record.
uint GetStageSlot(
    StructuredBuffer<uint> stageSlots,
    uint slotOffset,
    uint localMaterialIndex,
    uint slotCapacity)
{
    // SLOT_INVALID is the fixed instance-property sentinel while RenderWorld material-slot
    // composition is unavailable. Check before addition so it cannot wrap into a valid slot.
    if (slotOffset >= slotCapacity)
        return SLOT_INVALID;
    if (localMaterialIndex >= slotCapacity - slotOffset)
        return SLOT_INVALID;

    return stageSlots[slotOffset + localMaterialIndex];
}

uint LocalMaterial(uint packedMaterials, uint packedRanges, uint triStart)
{
    uint range0End = packedRanges & 0xFFu;
    uint range1End = (packedRanges >> 8) & 0xFFu;
    uint lane = triStart < range0End ? 0u : (triStart < range1End ? 1u : 2u);
    return (packedMaterials >> (lane * 8u)) & 0xFFu;
}

uint MaterialEnd(uint packedRanges, uint triStart, uint triangleCount)
{
    uint range0End = min(packedRanges & 0xFFu, triangleCount);
    uint range1End = min((packedRanges >> 8) & 0xFFu, triangleCount);
    return triStart < range0End ? range0End : (triStart < range1End ? range1End : triangleCount);
}

struct AABB
{
    float3 Min;
    float3 Max;
    uint Color; // Packed RGBA
    uint Pad;
};

// ─── GPU-side typed struct definitions for eliminating manual byte offsets ───

struct DispatchArgs
{
    uint GroupX;
    uint GroupY;
    uint GroupZ;
};

struct DrawInstancedArgs
{
    uint VertexCount;
    uint InstanceCount;
    uint StartVertex;
    uint StartInstance;
};

// Cull output draw args: [0]=vertex count per cluster, [1]=swCount, [2]=hwCount.
// The raw buffer slot at byte 12 is used as an internal total reservation counter.
struct CullDrawArgs
{
    uint VertexCountPerCluster;
    uint SWCount;
    uint HWCount;
};

// BVH traverse dispatch: {GroupX, GroupY, GroupZ, NodeCount}
struct BVHDispatchArgs
{
    uint GroupX;
    uint GroupY;
    uint GroupZ;
    uint NodeCount;
};

DispatchArgs LoadDispatchArgs(ByteAddressBuffer buffer, uint byteOffset)
{
    return DispatchArgs(
        buffer.Load(byteOffset + 0u),
        buffer.Load(byteOffset + 4u),
        buffer.Load(byteOffset + 8u));
}

DispatchArgs LoadDispatchArgs(RWByteAddressBuffer buffer, uint byteOffset)
{
    return DispatchArgs(
        buffer.Load(byteOffset + 0u),
        buffer.Load(byteOffset + 4u),
        buffer.Load(byteOffset + 8u));
}

void StoreDispatchArgs(RWByteAddressBuffer buffer, uint byteOffset, DispatchArgs value)
{
    buffer.Store(byteOffset + 0u, value.GroupX);
    buffer.Store(byteOffset + 4u, value.GroupY);
    buffer.Store(byteOffset + 8u, value.GroupZ);
}

DrawInstancedArgs LoadDrawInstancedArgs(ByteAddressBuffer buffer, uint byteOffset)
{
    return DrawInstancedArgs(
        buffer.Load(byteOffset + 0u),
        buffer.Load(byteOffset + 4u),
        buffer.Load(byteOffset + 8u),
        buffer.Load(byteOffset + 12u));
}

DrawInstancedArgs LoadDrawInstancedArgs(RWByteAddressBuffer buffer, uint byteOffset)
{
    return DrawInstancedArgs(
        buffer.Load(byteOffset + 0u),
        buffer.Load(byteOffset + 4u),
        buffer.Load(byteOffset + 8u),
        buffer.Load(byteOffset + 12u));
}

void StoreDrawInstancedArgs(RWByteAddressBuffer buffer, uint byteOffset, DrawInstancedArgs value)
{
    buffer.Store(byteOffset + 0u, value.VertexCount);
    buffer.Store(byteOffset + 4u, value.InstanceCount);
    buffer.Store(byteOffset + 8u, value.StartVertex);
    buffer.Store(byteOffset + 12u, value.StartInstance);
}

CullDrawArgs LoadCullDrawArgs(ByteAddressBuffer buffer, uint byteOffset)
{
    return CullDrawArgs(
        buffer.Load(byteOffset + 0u),
        buffer.Load(byteOffset + 4u),
        buffer.Load(byteOffset + 8u));
}

CullDrawArgs LoadCullDrawArgs(RWByteAddressBuffer buffer, uint byteOffset)
{
    return CullDrawArgs(
        buffer.Load(byteOffset + 0u),
        buffer.Load(byteOffset + 4u),
        buffer.Load(byteOffset + 8u));
}

void StoreCullDrawArgs(RWByteAddressBuffer buffer, uint byteOffset, CullDrawArgs value)
{
    buffer.Store(byteOffset + 0u, value.VertexCountPerCluster);
    buffer.Store(byteOffset + 4u, value.SWCount);
    buffer.Store(byteOffset + 8u, value.HWCount);
}

// Raster Bin metadata (32B = 8×uint)
struct RasterBinEntry
{
    uint ClusterOffset;
    uint BinCapacity;
    uint BinSWCount;
    uint BinHWCount;
    uint SWCursor;
    uint HWCursor;
    uint SWOffset;     // Exclusive prefix sum of SW clusters only across bins
    uint HardwareOffset;
};

// 12-byte geometry work item. Cluster ids are bounded to 12 bits by the packed-page ABI;
// the remaining 20 bits carry the renderer-global view index without a fourth word.
struct DrawRequest
{
    uint PageOffset;
    uint InstanceID;
    uint ClusterAndView;
};

DrawRequest MakeDrawRequest(
    uint pageOffset,
    uint clusterID,
    uint instanceID,
    uint viewIndex)
{
    DrawRequest request;
    request.PageOffset = pageOffset;
    request.InstanceID = instanceID;
    request.ClusterAndView = (clusterID & 0xFFFu) | (viewIndex << 12u);
    return request;
}

uint DrawRequestClusterID(DrawRequest request)
{
    return request.ClusterAndView & 0xFFFu;
}

uint DrawRequestViewIndex(DrawRequest request)
{
    return request.ClusterAndView >> 12u;
}

// One node carries up to 32 views through the hierarchy. Geometry and instance
// data are fetched once; projection, LOD and occlusion remain specific to each view.
struct ClusterTraversalNode
{
    uint NodeIndex;
    uint InstanceID;
    uint ViewIndex;
    uint ViewMask;
};

ClusterTraversalNode MakeClusterTraversalNode(
    uint nodeIndex, uint instanceID, uint viewIndex, uint viewMask)
{
    ClusterTraversalNode result;
    result.NodeIndex = nodeIndex;
    result.InstanceID = instanceID;
    result.ViewIndex = viewIndex;
    result.ViewMask = viewMask;
    return result;
}

uint ClusterTraversalViewIndex(ClusterTraversalNode node)
{
    return node.ViewIndex;
}

// The phase-two buffer is a compact tagged union shared by hierarchy and leaf work. A
// hierarchy item reserves the top bit of the first word; resident page offsets and BVH node
// indices are both constrained below 2 GiB by the cooked Cluster address ABI.
static const uint CLUSTER_DEFERRED_NODE_BIT = 0x80000000u;
static const uint CLUSTER_DEFERRED_INDEX_MASK = 0x7FFFFFFFu;

DrawRequest MakeDeferredTraversalNode(ClusterTraversalNode node)
{
    DrawRequest result;
    result.PageOffset = CLUSTER_DEFERRED_NODE_BIT |
        (node.NodeIndex & CLUSTER_DEFERRED_INDEX_MASK);
    result.InstanceID = node.InstanceID;
    result.ClusterAndView = node.ViewIndex;
    return result;
}

bool IsDeferredTraversalNode(DrawRequest work)
{
    return (work.PageOffset & CLUSTER_DEFERRED_NODE_BIT) != 0u;
}

ClusterTraversalNode DecodeDeferredTraversalNode(DrawRequest work)
{
    ClusterTraversalNode result;
    result.NodeIndex = work.PageOffset & CLUSTER_DEFERRED_INDEX_MASK;
    result.InstanceID = work.InstanceID;
    result.ViewIndex = work.ClusterAndView;
    result.ViewMask = 1u;
    return result;
}

struct ClusterMeshVertex
{
    float4 Pos : SV_POSITION;
};

struct ClusterRasterVertexData<TVarying>
{
    float4 Pos : SV_POSITION;
    TVarying Varying;
};

struct ClusterRasterPixelInput<TVarying>
{
    float4 Pos : SV_POSITION;
    TVarying Varying;
    nointerpolation uint VisibleClusterIndex : VIS_CLUSTER_IDX;
    nointerpolation uint TriangleID : TRIANGLE_ID;
    nointerpolation uint ViewIndex : VIEW_INDEX;
    uint RenderTargetIndex : SV_RenderTargetArrayIndex;
};

struct ClusterMeshPrimitive
{
    nointerpolation uint VisibleClusterIndex : VIS_CLUSTER_IDX;
    nointerpolation uint TriangleID : TRIANGLE_ID;
    nointerpolation uint ViewIndex : VIEW_INDEX;
    uint RenderTargetIndex : SV_RenderTargetArrayIndex;
};

#endif
