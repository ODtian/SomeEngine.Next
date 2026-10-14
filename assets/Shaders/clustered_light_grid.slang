#include "common.slang"
#include "render_lighting.slang"
#include "resource_effects.slang"

StructuredBuffer<ClusteredLightGridParameters> LightingViews;
StructuredBuffer<GPULight> LightBuffer;
[ResourceEffect(ResourceEffects.ReadWrite, ResourceQualifiers.Atomic)]
RWStructuredBuffer<uint2> ClusteredLightGrid;
[ResourceEffect(ResourceEffects.Write, ResourceQualifiers.None)]
RWStructuredBuffer<uint> LightIndexList;
[ResourceEffect(ResourceEffects.ReadWrite, ResourceQualifiers.None)]
RWStructuredBuffer<uint2> LightLinkList;
[ResourceEffect(ResourceEffects.ReadWrite, ResourceQualifiers.Atomic)]
RWStructuredBuffer<uint> LightAllocationCounters;
[ResourceEffect(ResourceEffects.ReadWrite, ResourceQualifiers.Atomic)]
RWStructuredBuffer<uint> LightAllocationStatus;


[__AttributeUsage(_AttributeTargets.Function)]
struct ClusteredSinglePointDirectAttribute
{
    string Role;
};

[ResourceEffect(ResourceEffects.ReadWrite, ResourceQualifiers.None)]
RWStructuredBuffer<uint4> SinglePointBounds;

[shader("compute")]
[numthreads(64, 1, 1)]
void ClearClusteredLightGrid(uint3 dispatchThreadId : SV_DispatchThreadID)
{
    ClusteredLightGridParameters parameters = LightingViews[dispatchThreadId.y];
    uint cell = dispatchThreadId.x;
    if (cell < parameters.CellCountPerView)
        ClusteredLightGrid[dispatchThreadId.y * parameters.CellCountPerView + cell] = uint2(0xffffffffu, 0u);
    if (cell == 0u && dispatchThreadId.y == 0u)
    {
        LightAllocationCounters[0] = LightAllocationCounters[1] = 0u;
        LightAllocationStatus[0] = 0u;
    }
}

void GetClusteredLightBoundingSphere(
    GPULight light,
    out float3 center,
    out float radius)
{
    center = light.Position;
    radius = max(light.Range, 0.0);

    float directionLengthSquared = dot(light.Direction, light.Direction);
    if (directionLengthSquared <= 1e-8 || light.OuterConeCos <= 1e-4)
        return;

    float3 direction = light.Direction * rsqrt(directionLengthSquared);
    float height = radius;
    float coneCos = saturate(light.OuterConeCos);
    float coneSin = sqrt(saturate(1.0 - coneCos * coneCos));
    float baseRadius = height * coneSin / max(coneCos, 1e-4);
    if (baseRadius <= height)
    {
        radius = (height * height + baseRadius * baseRadius) /
            max(2.0 * height, 1e-4);
        center += direction * radius;
    }
    else
    {
        center += direction * height;
        radius = baseRadius;
    }
}

bool GetClusteredLightTileBounds(
    ClusteredLightGridParameters parameters,
    float3 viewPosition,
    float radius,
    out uint2 minimumTile,
    out uint2 maximumTile)
{
    float viewDepth = -viewPosition.z;
    float nearDepth = parameters.ZParams.x;
    if (viewDepth + radius <= nearDepth)
        return false;

    if (viewDepth <= radius + nearDepth)
    {
        minimumTile = uint2(0u, 0u);
        maximumTile = uint2(
            parameters.TileCountX - 1u,
            parameters.TileCountY - 1u);
        return true;
    }

    // The projected box encloses the sphere for perspective, off-center and
    // orthographic matrices. A box crossing the eye plane covers the screen.
    float2 ndcMinimum = float2(1e30);
    float2 ndcMaximum = float2(-1e30);
    for (uint corner = 0u; corner < 8u; corner++)
    {
        float3 offset = float3(
            (corner & 1u) != 0u ? radius : -radius,
            (corner & 2u) != 0u ? radius : -radius,
            (corner & 4u) != 0u ? radius : -radius);
        float4 clip = mul(float4(viewPosition + offset, 1.0), parameters.Projection);
        if (clip.w <= 0.0)
        {
            minimumTile = uint2(0u);
            maximumTile = uint2(parameters.TileCountX - 1u, parameters.TileCountY - 1u);
            return true;
        }
        float2 ndc = clip.xy / clip.w;
        ndcMinimum = min(ndcMinimum, ndc);
        ndcMaximum = max(ndcMaximum, ndc);
    }
    float2 screenSize = float2(parameters.ScreenWidth, parameters.ScreenHeight);
    float2 pixelMinimum = float2(ndcMinimum.x + 1.0, 1.0 - ndcMaximum.y) * 0.5 * screenSize;
    float2 pixelMaximum = float2(ndcMaximum.x + 1.0, 1.0 - ndcMinimum.y) * 0.5 * screenSize;
    if (pixelMaximum.x < 0.0 || pixelMaximum.y < 0.0 ||
        pixelMinimum.x >= float(parameters.ScreenWidth) ||
        pixelMinimum.y >= float(parameters.ScreenHeight))
    {
        return false;
    }

    int2 tileMinimum = int2(floor(pixelMinimum / float2(
        parameters.TileSizeX,
        parameters.TileSizeY)));
    int2 tileMaximum = int2(floor(pixelMaximum / float2(
        parameters.TileSizeX,
        parameters.TileSizeY)));
    minimumTile = uint2(clamp(
        tileMinimum,
        int2(0, 0),
        int2(
            int(parameters.TileCountX) - 1,
            int(parameters.TileCountY) - 1)));
    maximumTile = uint2(clamp(
        tileMaximum,
        int2(0, 0),
        int2(
            int(parameters.TileCountX) - 1,
            int(parameters.TileCountY) - 1)));
    return true;
}

bool GetClusteredLightCellBounds(
    ClusteredLightGridParameters parameters,
    GPULight light,
    out uint3 minimum,
    out uint3 extent)
{
    minimum = uint3(0u, 0u, 0u);
    extent = uint3(0u, 0u, 0u);
    float3 worldCenter;
    float radius;
    GetClusteredLightBoundingSphere(light, worldCenter, radius);
    if (radius <= 0.0)
        return false;

    float3 viewPosition = mul(
        float4(worldCenter, 1.0),
        parameters.View).xyz;
    float viewDepth = -viewPosition.z;
    float nearDepth = parameters.ZParams.x;
    float farDepth = parameters.ZParams.y;
    float minimumDepth = max(nearDepth, viewDepth - radius);
    float maximumDepth = min(farDepth, viewDepth + radius);
    if (maximumDepth < nearDepth || minimumDepth > farDepth)
        return false;

    uint2 minimumTile;
    uint2 maximumTile;
    if (!GetClusteredLightTileBounds(
            parameters,
            viewPosition,
            radius,
            minimumTile,
            maximumTile))
    {
        return false;
    }

    uint minimumSlice = ClusteredLightDepthSlice(
        parameters,
        minimumDepth);
    uint maximumSlice = ClusteredLightDepthSlice(
        parameters,
        maximumDepth);
    minimum = uint3(minimumTile, minimumSlice);
    extent = uint3(maximumTile - minimumTile + 1u, maximumSlice - minimumSlice + 1u);
    return true;
}

groupshared uint3 LightCellMinimum;
groupshared uint3 LightCellExtent;

// A wave reserves from the shared budget, rather than truncating each cell.
// Saturation keeps both list addresses and the allocator safe even after overflow.
bool AllocateClusteredLightLink(out uint link)
{
    uint capacity, stride;
    LightLinkList.GetDimensions(capacity, stride);
    uint requested = WaveActiveCountBits(true);
    uint first = 0u, retained = 0u;
    if (WaveIsFirstLane())
    {
        uint expected = LightAllocationCounters[0];
        for (;;)
        {
            retained = min(requested, capacity - expected);
            first = expected;
            if (retained == 0u) break;
            uint observed;
            InterlockedCompareExchange(LightAllocationCounters[0], expected, expected + retained, observed);
            if (observed == expected) break;
            expected = observed;
        }
        if (retained != requested) InterlockedOr(LightAllocationStatus[0], 1u);
    }
    uint lane = WavePrefixCountBits(true);
    link = WaveReadLaneFirst(first) + lane;
    return lane < WaveReadLaneFirst(retained);
}

void AssignClusteredLightCell(ClusteredLightGridParameters parameters, uint viewOffset, uint lightIndex, uint index)
{
    uint x = index % LightCellExtent.x;
    uint yz = index / LightCellExtent.x;
    uint y = yz % LightCellExtent.y;
    uint z = yz / LightCellExtent.y;
    uint3 coordinate = LightCellMinimum + uint3(x, y, z);
    uint cell = viewOffset + (coordinate.z * parameters.TileCountY + coordinate.y) * parameters.TileCountX + coordinate.x;
    uint link;
    if (!AllocateClusteredLightLink(link)) return;
    uint previous;
    InterlockedExchange(ClusteredLightGrid[cell].x, link, previous);
    LightLinkList[link] = uint2(lightIndex, previous);
    InterlockedAdd(ClusteredLightGrid[cell].y, 1u);
}

[ClusteredSinglePointDirect("build-v1")]
[shader("compute")]
[numthreads(64, 1, 1)]
void BuildClusteredLightGrid(uint3 groupId : SV_GroupID, uint threadIndex : SV_GroupIndex)
{
    uint viewIndex = groupId.y;
    ClusteredLightGridParameters parameters = LightingViews[viewIndex];
    uint localLightIndex = groupId.x;
    if (localLightIndex >= parameters.NonDirectionalCount)
        return;
    uint lightIndex = parameters.DirectionalCount + localLightIndex;
    if (threadIndex == 0u)
    {
        uint3 minimum;
        uint3 extent;
        GetClusteredLightCellBounds(parameters, LightBuffer[lightIndex], minimum, extent);
        LightCellMinimum = minimum;
        LightCellExtent = extent;
    }
    GroupMemoryBarrierWithGroupSync();
    // Share cell work between groups when there are few lights. With many
    // lights, keep the parallelism across lights instead of multiplying empty work.
    uint count = LightCellExtent.x * LightCellExtent.y * LightCellExtent.z;
    uint viewOffset = viewIndex * parameters.CellCountPerView;
    if (parameters.GroupsPerLight == 1u)
    {
        for (uint index = threadIndex; index < count; index += 64u)
            AssignClusteredLightCell(parameters, viewOffset, lightIndex, index);
    }
    else
        for (uint index = groupId.z * 64u + threadIndex; index < count; index += 64u * parameters.GroupsPerLight)
            AssignClusteredLightCell(parameters, viewOffset, lightIndex, index);
}

[shader("compute")]
[numthreads(64, 1, 1)]
void CompactClusteredLightGrid(uint3 dispatchThreadId : SV_DispatchThreadID)
{
    ClusteredLightGridParameters parameters = LightingViews[dispatchThreadId.y];
    if (dispatchThreadId.x >= parameters.CellCountPerView) return;
    uint cell = dispatchThreadId.y * parameters.CellCountPerView + dispatchThreadId.x;
    uint2 head = ClusteredLightGrid[cell];
    uint first = 0u;
    if (head.y != 0u)
    {
        // Every stored link contributes once, so the compact list fits the same budget.
        InterlockedAdd(LightAllocationCounters[1], head.y, first);
        uint link = head.x;
        for (uint count = head.y; count != 0u; count--)
        {
            uint2 entry = LightLinkList[link];
            LightIndexList[first + count - 1u] = entry.x;
            link = entry.y;
        }
    }
    ClusteredLightGrid[cell] = uint2(first, head.y);
}

[ClusteredSinglePointDirect("prepare-v1")]
[shader("compute")]
[numthreads(1, 1, 1)]
void PrepareSinglePointLightGrid(uint3 tid : SV_DispatchThreadID)
{
    ClusteredLightGridParameters parameters = LightingViews[0];
    uint3 minimum, extent;
    GetClusteredLightCellBounds(parameters, LightBuffer[parameters.DirectionalCount], minimum, extent);
    uint count = extent.x * extent.y * extent.z;
    SinglePointBounds[0] = uint4(minimum, count);
    SinglePointBounds[1] = uint4(extent, parameters.DirectionalCount);
    LightAllocationCounters[0] = count;
    LightAllocationCounters[1] = count;
    LightAllocationStatus[0] = 0u;
}

[ClusteredSinglePointDirect("fill-v1")]
[shader("compute")]
[numthreads(64, 1, 1)]
void FillSinglePointLightGrid(uint3 groupId : SV_GroupID, uint threadIndex : SV_GroupIndex)
{
    ClusteredLightGridParameters parameters = LightingViews[0];
    uint group = groupId.x + groupId.y * 65535u;
    uint groups = parameters.CellCountPerView / 64u + (parameters.CellCountPerView % 64u != 0u ? 1u : 0u);
    if (group >= groups) return;
    uint cell = group * 64u + threadIndex;
    if (cell >= parameters.CellCountPerView) return;
    uint4 bounds = SinglePointBounds[0];
    uint4 shape = SinglePointBounds[1];
    uint x = cell % parameters.TileCountX;
    uint yz = cell / parameters.TileCountX;
    uint y = yz % parameters.TileCountY;
    uint z = yz / parameters.TileCountY;
    uint3 coordinate = uint3(x, y, z);
    uint3 relative = coordinate - bounds.xyz;
    if (all(coordinate >= bounds.xyz) && all(relative < shape.xyz))
    {
        uint ordinal = (relative.z * shape.y + relative.y) * shape.x + relative.x;
        ClusteredLightGrid[cell] = uint2(ordinal, 1u);
        LightIndexList[ordinal] = shape.w;
        // Retain the existing private link-prefix/counter protocol for the strict gate.
        LightLinkList[ordinal] = uint2(shape.w, 0xffffffffu);
    }
    else ClusteredLightGrid[cell] = uint2(0u, 0u);
}
