#include "triangles.slang"

struct ResolveSettings
{
    uint Width;
    uint Height;
    uint NodeCount;
    float ColorError;
    uint BoundsOffset;
    uint ColorsOffset;
    uint CoverageOffset;
    uint CoverageCount;
};
ConstantBuffer<ResolveSettings> Settings;
ByteAddressBuffer Geometry;
Texture2DArray<float> OpaqueDepth;
RWTexture2DArray<float4> SceneColor;
#if TRANSPARENCY_INSPECTION
RWStructuredBuffer<uint4> Results;
#endif

ProjectedTriangle LoadTriangle(uint index) { return Geometry.Load<ProjectedTriangle>(index * 56); }
TriangleBounds LoadBounds(uint index) { return Geometry.Load<TriangleBounds>(Settings.BoundsOffset + index * 32); }
float4 LoadColor(uint index) { return Geometry.Load<float4>(Settings.ColorsOffset + index * 16); }
uint4 LoadCoverage(uint index) { return Geometry.Load4(Settings.CoverageOffset + index * 16); }

uint FindNext(float2 point, float previousDepth, uint previousTriangleId, float opaqueDepth,
    inout uint firstNode, uint completedCoverage, uint previousCoverage, out float depth, out uint visits, out uint coverageIndex)
{
    uint triangleIndex = 0xffffffff;
    uint closestTriangleId = 0xffffffff;
    depth = opaqueDepth;
    visits = 0;
    coverageIndex = 0xffffffff;
    uint nodeIndex = firstNode;
    while (nodeIndex < Settings.NodeCount)
    {
        TriangleBounds node = LoadBounds(nodeIndex);
        visits++;
        bool triangleNode = IsTriangle(node);
        uint subtreeEnd = GetSubtreeEnd(node, nodeIndex);
        bool completed = node.CoverageIndex != 0xffffffff &&
            (node.CoverageIndex == previousCoverage || (node.CoverageIndex < 32 && (completedCoverage & (1u << node.CoverageIndex)) != 0));
        if (completed)
        {
            uint4 coverage = LoadCoverage(node.CoverageIndex);
            if (nodeIndex == firstNode) firstNode = coverage.y;
            nodeIndex = coverage.y;
            if (triangleIndex != 0xffffffff && depth < asfloat(coverage.z)) return triangleIndex;
            continue;
        }
        if (!Contains(node, point))
        {
            if (nodeIndex == firstNode) firstNode = subtreeEnd;
            nodeIndex = subtreeEnd;
            continue;
        }
        if (node.Maximum.z < previousDepth || node.Minimum.z >= opaqueDepth)
        {
            if (nodeIndex == firstNode) firstNode = subtreeEnd;
            nodeIndex = subtreeEnd;
            continue;
        }
        if (node.Minimum.z > depth)
        {
            nodeIndex = subtreeEnd;
            continue;
        }
        if (!triangleNode)
        {
            if (nodeIndex == firstNode) firstNode = nodeIndex + 1;
        }
        else
        {
            uint coveredTriangle = GetTriangleIndex(node);
            ProjectedTriangle triangle = LoadTriangle(coveredTriangle);
            float triangleDepth;
            if (Covers(triangle, point, triangleDepth))
            {
                bool afterPrevious = triangleDepth > previousDepth ||
                    (triangleDepth == previousDepth && triangle.TriangleId > previousTriangleId);
                if (afterPrevious && triangleDepth < opaqueDepth &&
                    (triangleDepth < depth || (triangleDepth == depth && triangle.TriangleId < closestTriangleId)))
                {
                    triangleIndex = coveredTriangle;
                    closestTriangleId = triangle.TriangleId;
                    depth = triangleDepth;
                    coverageIndex = node.CoverageIndex;
                }
                uint4 coverage = LoadCoverage(node.CoverageIndex);
                if ((!afterPrevious || triangleDepth >= opaqueDepth) && nodeIndex == firstNode) firstNode = coverage.y;
                nodeIndex = coverage.y;
                if (triangleIndex != 0xffffffff && depth < asfloat(coverage.z)) return triangleIndex;
                continue;
            }
            if (nodeIndex == firstNode) firstNode = nodeIndex + 1;
        }
        nodeIndex++;
    }
    return triangleIndex;
}

[shader("compute")]
[numthreads(8, 8, 1)]
void ResolveTransparency(uint3 threadId : SV_DispatchThreadID)
{
    uint2 pixel = threadId.xy;
    if (any(pixel >= uint2(Settings.Width, Settings.Height))) return;
    float2 point = (float2(pixel) + .5) / float2(Settings.Width, Settings.Height) * float2(2, -2) + float2(-1, 1);
    TriangleBounds root = LoadBounds(0);
    if (!Contains(root, point))
    {
#if TRANSPARENCY_INSPECTION
        Results[pixel.y * Settings.Width + pixel.x] = 0;
#endif
        return;
    }
    float opaqueDepth = 1 - OpaqueDepth.Load(int4(pixel, 0, 0));
    if (root.Minimum.z >= opaqueDepth)
    {
#if TRANSPARENCY_INSPECTION
        Results[pixel.y * Settings.Width + pixel.x] = 0;
#endif
        return;
    }
    float4 background = SceneColor[uint3(pixel, 0)];
    float3 upperColor = max(float3(1), background.rgb);
    float errorScale = .5 * max(upperColor.x, max(upperColor.y, upperColor.z));
    float3 color = 0;
    float transmittance = 1, previousDepth = -1;
    uint previousTriangleId = 0, firstNode = 0, completedCoverage = 0, previousCoverage = 0xffffffff;
    uint surfaces = 0, nodeVisits = 0;
    bool exhausted = false;
#if TRANSPARENCY_SEPARATED_COVERAGE
    uint rangeIndex = 0;
    while (rangeIndex < Settings.CoverageCount && errorScale * transmittance > Settings.ColorError)
    {
        uint4 coverage = LoadCoverage(rangeIndex++);
        uint nodeIndex = coverage.x;
        while (nodeIndex < coverage.y)
        {
            TriangleBounds node = LoadBounds(nodeIndex);
            nodeVisits++;
            if (!Contains(node, point) || node.Minimum.z >= opaqueDepth)
            {
                nodeIndex = GetSubtreeEnd(node, nodeIndex);
                continue;
            }
            if (IsTriangle(node))
            {
                ProjectedTriangle triangle = LoadTriangle(GetTriangleIndex(node));
                float depth;
                if (Covers(triangle, point, depth) && depth < opaqueDepth)
                {
                    float4 surface = LoadColor(triangle.ColorIndex);
                    color += transmittance * surface.a * surface.rgb;
                    transmittance *= 1 - surface.a;
                    surfaces++;
                    break;
                }
            }
            nodeIndex++;
        }
    }
    exhausted = rangeIndex == Settings.CoverageCount;
#else
    while (errorScale * transmittance > Settings.ColorError)
    {
        float depth;
        uint visits, coverageIndex;
        uint triangleIndex = FindNext(point, previousDepth, previousTriangleId, opaqueDepth,
            firstNode, completedCoverage, previousCoverage, depth, visits, coverageIndex);
        nodeVisits += visits;
        if (triangleIndex == 0xffffffff) { exhausted = true; break; }
        ProjectedTriangle triangle = LoadTriangle(triangleIndex);
        float4 surface = LoadColor(triangle.ColorIndex);
        color += transmittance * surface.a * surface.rgb;
        transmittance *= 1 - surface.a;
        previousDepth = depth;
        previousTriangleId = triangle.TriangleId;
        previousCoverage = coverageIndex;
        if (coverageIndex < 32) completedCoverage |= 1u << coverageIndex;
        surfaces++;
    }
#endif
    color += transmittance * (exhausted ? background.rgb : .5 * upperColor);
    SceneColor[uint3(pixel, 0)] = float4(color, background.a);
#if TRANSPARENCY_INSPECTION
    Results[pixel.y * Settings.Width + pixel.x] = uint4(surfaces, nodeVisits,
        asuint(exhausted ? 0 : errorScale * transmittance), 0);
#endif
}
