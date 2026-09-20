#include "common.slang"
#include "render_lighting.slang"
#include "resource_effects.slang"

StructuredBuffer<ClusteredLightGridParameters> LightingViews;
StructuredBuffer<GPULight> LightBuffer;
[ResourceEffect(ResourceEffects.ReadWrite, ResourceQualifiers.Atomic)]
RWStructuredBuffer<uint2> ClusteredLightGrid;
[ResourceEffect(ResourceEffects.Write, ResourceQualifiers.None)]
RWStructuredBuffer<uint> LightIndexList;

[shader("compute")]
[numthreads(64, 1, 1)]
void ClearClusteredLightGrid(uint3 dispatchThreadId : SV_DispatchThreadID)
{
    ClusteredLightGridParameters parameters = LightingViews[dispatchThreadId.y];
    uint cell = dispatchThreadId.x;
    if (cell < parameters.CellCountPerView)
        ClusteredLightGrid[dispatchThreadId.y * parameters.CellCountPerView + cell] = uint2(0u, 0u);
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

void AssignClusteredLightCell(ClusteredLightGridParameters parameters, uint viewOffset, uint lightIndex, uint index)
{
    uint x = index % LightCellExtent.x;
    uint yz = index / LightCellExtent.x;
    uint y = yz % LightCellExtent.y;
    uint z = yz / LightCellExtent.y;
    uint3 coordinate = LightCellMinimum + uint3(x, y, z);
    uint cell = viewOffset + (coordinate.z * parameters.TileCountY + coordinate.y) * parameters.TileCountX + coordinate.x;
    uint destination;
    InterlockedAdd(ClusteredLightGrid[cell].y, 1u, destination);
    if (destination < parameters.MaxLightsPerCluster)
        LightIndexList[cell * parameters.MaxLightsPerCluster + destination] = lightIndex;
}

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
