#ifndef CLUSTER_BUILD_SLANG
#define CLUSTER_BUILD_SLANG

struct ClusterVertexInput
{
    ByteAddressBuffer Positions;
    ByteAddressBuffer Attributes;
    uint Count;
    uint SourceIndices[64];
};

// The encoder and its paired IVertexEvaluate define every byte in the vertex blob.
interface IClusterVertexEncode
{
    uint encode(ClusterVertexInput vertices, RWByteAddressBuffer destination, uint byteOffset);
};

struct ClusterBuildUniforms
{
    uint PageOffset;
    uint PageBytes;
    uint PageStride;
    uint PageCount;
    uint BvhRoot;
    uint BvhNodeCount;
    uint EncodingLow;
    uint EncodingHigh;
    uint MaterialIndex;
    uint MaterialCount;
    uint HasTriangleMaterials;
};
ConstantBuffer<ClusterBuildUniforms> ClusterBuildParameters;
ByteAddressBuffer ClusterBuildPositions;
ByteAddressBuffer ClusterBuildAttributes;
ByteAddressBuffer ClusterBuildIndices;
ByteAddressBuffer ClusterBuildCounts;
ByteAddressBuffer ClusterBuildMaterials;
RWByteAddressBuffer ClusterBuildPages;
RWByteAddressBuffer ClusterBuildBvh;

void StoreClusterBuildBatch(inout uint4 words, uint batch, uint triangles)
{
    uint count = triangles - 1u;
    uint bit = 12u + batch * 5u;
    words[bit >> 5u] |= count << (bit & 31u);
    if ((bit & 31u) > 27u) words[(bit >> 5u) + 1u] |= count >> (32u - (bit & 31u));
}

void StoreClusterBuildNode(uint index, float3 lower, float3 upper,
    uint childPointer, uint childCount, uint nodeType)
{
    uint address = index * 64u;
    float3 center = (lower + upper) * 0.5;
    float radius = length(upper - lower) * 0.5;
    ClusterBuildBvh.Store4(address, uint4(asuint(lower), 0u));
    ClusterBuildBvh.Store4(address + 16u, uint4(asuint(upper), nodeType == 1u ? 0xFFFFFFFFu : 0u));
    ClusterBuildBvh.Store4(address + 32u, uint4(asuint(center), asuint(radius)));
    // These nodes contain full-detail geometry with no coarser replacement.
    ClusterBuildBvh.Store4(address + 48u, uint4(0x7F7FFFFFu, childPointer, childCount, nodeType));
}

void StoreClusterBuildPage<T : IClusterVertexEncode>(T encoder, ClusterVertexInput vertices,
    uint triangleIndices[372], uint triangleCount, uint pageIndex, uint materialIndex)
{
    uint page = pageIndex * ClusterBuildParameters.PageStride;
    float3 lower = asfloat(vertices.Positions.Load3(vertices.SourceIndices[0] * 12u));
    float3 upper = lower;
    for (uint vertex = 1u; vertex < vertices.Count; vertex++)
    {
        float3 position = asfloat(vertices.Positions.Load3(vertices.SourceIndices[vertex] * 12u));
        lower = min(lower, position);
        upper = max(upper, position);
    }
    float3 center = (lower + upper) * 0.5;
    float radius = length(upper - lower) * 0.5;
    uint blobBytes = encoder.encode(vertices, ClusterBuildPages, page + 160u);
    uint indicesOffset = (160u + blobBytes + 3u) & ~3u;
    ClusterBuildPages.Store4(page + 144u, uint4(16u, blobBytes, 16u, blobBytes));
    ClusterBuildPages.Store4(page, uint4(1u, vertices.Count, triangleCount, indicesOffset - 144u));
    ClusterBuildPages.Store4(page + 16u, uint4(48u, 144u, indicesOffset, 0u));
    ClusterBuildPages.Store2(page + 32u, uint2(ClusterBuildParameters.EncodingLow, ClusterBuildParameters.EncodingHigh));
    ClusterBuildPages.Store4(page + 48u, uint4(0u, 16u + blobBytes, 0xFFFFFFFFu, 0u));
    ClusterBuildPages.Store4(page + 64u, uint4(asuint(center), asuint(radius)));
    ClusterBuildPages.Store3(page + 80u, asuint(lower));
    ClusterBuildPages.Store3(page + 92u, asuint(upper));
    ClusterBuildPages.Store(page + 108u, vertices.Count | (triangleCount << 8u));
    ClusterBuildPages.Store(page + 112u, 0xFFFFu);
    ClusterBuildPages.Store(page + 116u, materialIndex);
    ClusterBuildPages.Store(page + 120u, triangleCount | (triangleCount << 8u));
    ClusterBuildPages.Store(page + 124u, 0xFFFFFFFFu);

    // Raster batches contain at most 32 triangles and 32 distinct vertices.
    uint batchCount = 0u, batchTriangles = 0u;
    uint4 batches = 0u.xxxx;
    uint2 batchVertices = 0u.xx;
    for (uint triangle = 0u; triangle < triangleCount; triangle++)
    {
        uint2 added = 0u.xx;
        for (uint corner = 0u; corner < 3u; corner++)
        {
            uint index = triangleIndices[triangle * 3u + corner];
            added[index >> 5u] |= 1u << (index & 31u);
        }
        uint2 combined = batchVertices | added;
        if (batchTriangles == 32u || countbits(combined.x) + countbits(combined.y) > 32u)
        {
            StoreClusterBuildBatch(batches, batchCount++, batchTriangles);
            batchVertices = added;
            batchTriangles = 1u;
        }
        else { batchVertices = combined; batchTriangles++; }
    }
    StoreClusterBuildBatch(batches, batchCount++, batchTriangles);
    batches.x |= batchCount;
    ClusterBuildPages.Store4(page + 128u, batches);
    uint indexCount = triangleCount * 3u;
    for (uint first = 0u; first < indexCount; first += 4u)
    {
        uint packed = 0u;
        for (uint lane = 0u; lane < 4u && first + lane < indexCount; lane++)
            packed |= triangleIndices[first + lane] << (lane * 8u);
        ClusterBuildPages.Store(page + indicesOffset + first, packed);
    }
    uint leafStart = ClusterBuildParameters.BvhNodeCount - ClusterBuildParameters.PageCount;
    StoreClusterBuildNode(leafStart + pageIndex, lower, upper,
        ClusterBuildParameters.PageOffset + page, 1u << 12u, 1u);
}

void BuildClusterHierarchy(uint pageCount)
{
    uint childCapacity = ClusterBuildParameters.PageCount;
    uint childStart = ClusterBuildParameters.BvhNodeCount - childCapacity;
    uint childCount = pageCount;
    while (childCapacity > 1u)
    {
        uint parentCapacity = (childCapacity + 15u) / 16u;
        uint parentCount = (childCount + 15u) / 16u;
        uint parentStart = childStart - parentCapacity;
        for (uint parent = 0u; parent < parentCount; parent++)
        {
            uint first = childStart + parent * 16u;
            uint count = min(16u, childCount - parent * 16u);
            float3 lower = asfloat(ClusterBuildBvh.Load3(first * 64u));
            float3 upper = asfloat(ClusterBuildBvh.Load3(first * 64u + 16u));
            for (uint child = 1u; child < count; child++)
            {
                lower = min(lower, asfloat(ClusterBuildBvh.Load3((first + child) * 64u)));
                upper = max(upper, asfloat(ClusterBuildBvh.Load3((first + child) * 64u + 16u)));
            }
            StoreClusterBuildNode(parentStart + parent, lower, upper,
                ClusterBuildParameters.BvhRoot + first, count, 0u);
        }
        childCapacity = parentCapacity;
        childStart = parentStart;
        childCount = parentCount;
    }
}

void BuildCluster<T : IClusterVertexEncode>(T encoder)
{
    for (uint byte = 0u; byte < ClusterBuildParameters.PageBytes; byte += 4u)
        ClusterBuildPages.Store(byte, 0u);
    for (uint byte = 0u; byte < ClusterBuildParameters.BvhNodeCount * 64u; byte += 4u)
        ClusterBuildBvh.Store(byte, 0u);
    uint vertexCount = ClusterBuildCounts.Load(0);
    uint triangleCount = ClusterBuildCounts.Load(4) / 3u;
    if (vertexCount == 0u || triangleCount == 0u) return;

    ClusterVertexInput vertices;
    vertices.Positions = ClusterBuildPositions;
    vertices.Attributes = ClusterBuildAttributes;
    uint triangleIndices[372];
    uint pageCount = 0u;
    uint materialCount = ClusterBuildParameters.HasTriangleMaterials != 0u ? ClusterBuildParameters.MaterialCount : 1u;
    for (uint group = 0u; group < materialCount; group++)
    {
        uint material = ClusterBuildParameters.HasTriangleMaterials != 0u ? group : ClusterBuildParameters.MaterialIndex;
        vertices.Count = 0u;
        uint pageTriangles = 0u, triangle = 0u;
        while (triangle < triangleCount)
        {
            if (ClusterBuildParameters.HasTriangleMaterials != 0u && ClusterBuildMaterials.Load(triangle * 4u) != material)
            {
                triangle++;
                continue;
            }
            uint3 source = ClusterBuildIndices.Load3(triangle * 12u);
            uint3 local;
            uint added[3];
            uint addedCount = 0u;
            for (uint corner = 0u; corner < 3u; corner++)
            {
                uint index = 0u;
                while (index < vertices.Count && vertices.SourceIndices[index] != source[corner]) index++;
                if (index == vertices.Count)
                {
                    uint addition = 0u;
                    while (addition < addedCount && added[addition] != source[corner]) addition++;
                    if (addition == addedCount) added[addedCount++] = source[corner];
                    index += addition;
                }
                local[corner] = index;
            }
            if (vertices.Count + addedCount > 64u || pageTriangles == 124u)
            {
                StoreClusterBuildPage(encoder, vertices, triangleIndices, pageTriangles, pageCount++, material);
                vertices.Count = 0u;
                pageTriangles = 0u;
                continue;
            }
            for (uint addition = 0u; addition < addedCount; addition++)
                vertices.SourceIndices[vertices.Count + addition] = added[addition];
            vertices.Count += addedCount;
            for (uint corner = 0u; corner < 3u; corner++)
                triangleIndices[pageTriangles * 3u + corner] = local[corner];
            pageTriangles++;
            triangle++;
        }
        if (pageTriangles != 0u)
            StoreClusterBuildPage(encoder, vertices, triangleIndices, pageTriangles, pageCount++, material);
    }
    BuildClusterHierarchy(pageCount);
}
#endif
