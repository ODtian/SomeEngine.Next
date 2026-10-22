#ifndef CLUSTER_BUILD_SLANG
#define CLUSTER_BUILD_SLANG

// The encoder and its paired IVertexEvaluate define every byte in the vertex blob.
interface IClusterVertexEncode
{
    uint encode(ByteAddressBuffer positions, ByteAddressBuffer attributes, uint vertexCount,
        RWByteAddressBuffer destination, uint byteOffset);
};

struct ClusterBuildUniforms
{
    uint PageOffset;
    uint PageBytes;
    uint EncodingLow;
    uint EncodingHigh;
    uint MaterialIndex;
};
ConstantBuffer<ClusterBuildUniforms> ClusterBuildParameters;
ByteAddressBuffer ClusterBuildPositions;
ByteAddressBuffer ClusterBuildAttributes;
ByteAddressBuffer ClusterBuildIndices;
ByteAddressBuffer ClusterBuildCounts;
RWByteAddressBuffer ClusterBuildPages;
RWByteAddressBuffer ClusterBuildBvh;

void StoreClusterBuildBatch(inout uint4 words, uint batch, uint triangles)
{
    uint count = triangles - 1u;
    uint bit = 12u + batch * 5u;
    words[bit >> 5u] |= count << (bit & 31u);
    if ((bit & 31u) > 27u) words[(bit >> 5u) + 1u] |= count >> (32u - (bit & 31u));
}

void BuildCluster<T : IClusterVertexEncode>(T encoder)
{
    uint vertexCount = ClusterBuildCounts.Load(0);
    uint indexCount = ClusterBuildCounts.Load(4);
    uint triangleCount = indexCount / 3u;
    for (uint byte = 0u; byte < ClusterBuildParameters.PageBytes; byte += 4u)
        ClusterBuildPages.Store(byte, 0u);
    float3 lower = asfloat(ClusterBuildPositions.Load3(0));
    float3 upper = lower;
    for (uint vertex = 1u; vertex < vertexCount; vertex++)
    {
        float3 position = asfloat(ClusterBuildPositions.Load3(vertex * 12u));
        lower = min(lower, position);
        upper = max(upper, position);
    }
    float3 center = (lower + upper) * 0.5;
    float radius = length(upper - lower) * 0.5;
    uint blobBytes = encoder.encode(ClusterBuildPositions, ClusterBuildAttributes, vertexCount, ClusterBuildPages, 144u);
    uint indicesOffset = (144u + blobBytes + 3u) & ~3u;
    ClusterBuildPages.Store4(0u, uint4(1u, vertexCount, triangleCount, blobBytes));
    ClusterBuildPages.Store4(16u, uint4(48u, 144u, indicesOffset, 0u));
    ClusterBuildPages.Store2(32u, uint2(ClusterBuildParameters.EncodingLow, ClusterBuildParameters.EncodingHigh));
    ClusterBuildPages.Store4(48u, uint4(0u, blobBytes, 0xFFFFFFFFu, 0u));
    ClusterBuildPages.Store4(64u, uint4(asuint(center), asuint(radius)));
    ClusterBuildPages.Store3(80u, asuint(lower));
    ClusterBuildPages.Store3(92u, asuint(upper));
    ClusterBuildPages.Store(108u, vertexCount | (triangleCount << 8u));
    ClusterBuildPages.Store(112u, 0xFFFFu);
    ClusterBuildPages.Store(116u, ClusterBuildParameters.MaterialIndex);
    ClusterBuildPages.Store(120u, triangleCount | (triangleCount << 8u));
    ClusterBuildPages.Store(124u, 0xFFFFFFFFu);
    // Raster batches contain at most 32 triangles and 32 distinct vertices.
    uint batchCount = 0u, batchTriangles = 0u;
    uint4 batches = 0u.xxxx;
    uint2 vertices = 0u.xx;
    for (uint triangle = 0u; triangle < triangleCount; triangle++)
    {
        uint3 indices = ClusterBuildIndices.Load3(triangle * 12u);
        uint2 added = 0u.xx;
        for (uint corner = 0u; corner < 3u; corner++)
            added[indices[corner] >> 5u] |= 1u << (indices[corner] & 31u);
        uint2 combined = vertices | added;
        if (batchTriangles == 32u || countbits(combined.x) + countbits(combined.y) > 32u)
        {
            StoreClusterBuildBatch(batches, batchCount++, batchTriangles);
            vertices = added;
            batchTriangles = 1u;
        }
        else { vertices = combined; batchTriangles++; }
    }
    StoreClusterBuildBatch(batches, batchCount++, batchTriangles);
    batches.x |= batchCount;
    ClusterBuildPages.Store4(128u, batches);
    for (uint first = 0u; first < indexCount; first += 4u)
    {
        uint packed = 0u;
        for (uint lane = 0u; lane < 4u && first + lane < indexCount; lane++)
            packed |= ClusterBuildIndices.Load((first + lane) * 4u) << (lane * 8u);
        ClusterBuildPages.Store(indicesOffset + first, packed);
    }
    ClusterBuildBvh.Store4(0u, uint4(asuint(lower), 0u));
    ClusterBuildBvh.Store4(16u, uint4(asuint(upper), 0xFFFFFFFFu));
    ClusterBuildBvh.Store4(32u, uint4(asuint(center), asuint(radius)));
    // A root has no coarser replacement, so its parent-error bound spans every view threshold.
    ClusterBuildBvh.Store4(48u, uint4(0x7F7FFFFFu, ClusterBuildParameters.PageOffset, 1u << 12u, 1u));
}
#endif
