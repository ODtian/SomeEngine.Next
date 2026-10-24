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

void StoreClusterBuildSlice(uint node, uint child, float3 lower, float3 upper, uint reference, uint pageRange, uint count)
{
    uint address = node * 240u;
    float3 center = (lower + upper) * 0.5;
    float radius = length(upper - lower) * 0.5;
    ClusterBuildBvh.Store4(address + child * 16u, uint4(asuint(center), asuint(radius)));
    ClusterBuildBvh.Store4(address + 64u + child * 16u, uint4(asuint(center), 0x00007C00u));
    ClusterBuildBvh.Store4(address + 128u + child * 16u, uint4(asuint((upper - lower) * 0.5), reference));
    ClusterBuildBvh.Store3(address + 192u + child * 12u, uint3(pageRange, count << 16u, 0u));
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
    ClusterBuildPages.Store(page + 44u, pageIndex << 1u);
    ClusterBuildPages.Store4(page + 48u, uint4(0u, 16u + blobBytes, 0xFFFFFFFFu, 0u));
    ClusterBuildPages.Store4(page + 64u, uint4(asuint(center), asuint(radius)));
    ClusterBuildPages.Store3(page + 80u, asuint(lower));
    ClusterBuildPages.Store3(page + 92u, asuint(upper));
    ClusterBuildPages.Store(page + 108u, vertices.Count | (triangleCount << 8u) | (6u << 24u));
    float maximumEdge = 0.0;
    for (uint triangle = 0u; triangle < triangleCount; triangle++)
        for (uint corner = 0u; corner < 3u; corner++)
        {
            float3 first = asfloat(vertices.Positions.Load3(vertices.SourceIndices[triangleIndices[triangle * 3u + corner]] * 12u));
            float3 second = asfloat(vertices.Positions.Load3(vertices.SourceIndices[triangleIndices[triangle * 3u + (corner + 1u) % 3u]] * 12u));
            maximumEdge = max(maximumEdge, length(first - second));
        }
    uint edge = f32tof16(maximumEdge);
    if (f16tof32(edge) < maximumEdge) edge++;
    ClusterBuildPages.Store(page + 112u, edge << 16u);
    ClusterBuildPages.Store(page + 116u, materialIndex & 255u);
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
    if (materialIndex <= 255u) ClusterBuildPages.Store4(page + 128u, batches);
    else
    {
        uint table = (indicesOffset + triangleCount * 3u + 3u) & ~3u;
        ClusterBuildPages.Store2(page + table, uint2(triangleCount << 8u, materialIndex));
        ClusterBuildPages.Store4(page + table + 8u, batches);
        ClusterBuildPages.Store(page + 120u, triangleCount | (triangleCount << 8u) | (1u << 16u));
        ClusterBuildPages.Store(page + 124u, table);
        ClusterBuildPages.Store4(page + 128u, uint4(table + 8u, 1u, 4u, 0u));
    }
    uint indexCount = triangleCount * 3u;
    for (uint first = 0u; first < indexCount; first += 4u)
    {
        uint packed = 0u;
        for (uint lane = 0u; lane < 4u && first + lane < indexCount; lane++)
            packed |= triangleIndices[first + lane] << (lane * 8u);
        ClusterBuildPages.Store(page + indicesOffset + first, packed);
    }
    uint leafStart = ClusterBuildParameters.BvhNodeCount - (ClusterBuildParameters.PageCount + 3u) / 4u;
    uint pageReference = ((ClusterBuildParameters.PageOffset + page) >> 9u) << 8u;
    StoreClusterBuildSlice(leafStart + pageIndex / 4u, pageIndex % 4u, lower, upper, pageReference, 0u, 1u);
}

void BuildClusterHierarchy(uint pageCount)
{
    uint childCapacity = (ClusterBuildParameters.PageCount + 3u) / 4u;
    uint childStart = ClusterBuildParameters.BvhNodeCount - childCapacity;
    uint childCount = (pageCount + 3u) / 4u;
    while (childCapacity > 1u)
    {
        uint parentCapacity = (childCapacity + 3u) / 4u;
        uint parentStart = childStart - parentCapacity;
        for (uint child = 0u; child < childCount; child++)
        {
            uint node = childStart + child;
            float3 lower = 1e30.xxx, upper = -1e30.xxx;
            for (uint slice = 0u; slice < 4u; slice++)
            {
                uint address = node * 240u;
                if (ClusterBuildBvh.Load(address + 196u + slice * 12u) == 0u) continue;
                float3 center = asfloat(ClusterBuildBvh.Load3(address + 64u + slice * 16u));
                float3 extent = asfloat(ClusterBuildBvh.Load3(address + 128u + slice * 16u));
                lower = min(lower, center - extent);
                upper = max(upper, center + extent);
            }
            StoreClusterBuildSlice(parentStart + child / 4u, child % 4u, lower, upper,
                ClusterBuildParameters.BvhRoot + node, 0xFFFFFFFFu, 511u);
        }
        childCapacity = parentCapacity;
        childStart = parentStart;
        childCount = (childCount + 3u) / 4u;
    }
}

void BuildCluster<T : IClusterVertexEncode>(T encoder)
{
    for (uint byte = 0u; byte < ClusterBuildParameters.PageBytes; byte += 4u)
        ClusterBuildPages.Store(byte, 0u);
    for (uint byte = 0u; byte < ClusterBuildParameters.BvhNodeCount * 240u; byte += 4u)
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
    uint previousMaterial = 0u;
    for (uint group = 0u; group < materialCount; group++)
    {
        uint material = ClusterBuildParameters.MaterialIndex;
        if (ClusterBuildParameters.HasTriangleMaterials != 0u)
        {
            bool found = false;
            for (uint sourceTriangle = 0u; sourceTriangle < triangleCount; sourceTriangle++)
            {
                uint candidate = ClusterBuildMaterials.Load(sourceTriangle * 4u);
                if ((group == 0u || candidate > previousMaterial) && (!found || candidate < material))
                {
                    material = candidate;
                    found = true;
                }
            }
            if (!found) break;
            previousMaterial = material;
        }
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
