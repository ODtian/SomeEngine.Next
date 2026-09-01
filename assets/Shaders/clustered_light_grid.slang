#include "common.slang"
#include "render_lighting.slang"
#include "resource_effects.slang"

StructuredBuffer<ClusteredLightGridParameters> LightingViews;
StructuredBuffer<GPULight> LightBuffer;
[ResourceEffect(ResourceEffects.ReadWrite, ResourceQualifiers.Atomic)]
RWStructuredBuffer<uint2> ClusteredLightGrid;
[ResourceEffect(ResourceEffects.Write, ResourceQualifiers.None)]
RWStructuredBuffer<uint> LightIndexList;

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

    float inverseTangentDistance = rsqrt(max(
        viewDepth * viewDepth - radius * radius,
        1e-8));
    float2 ndcCenter = float2(
        viewPosition.x * parameters.P00 / viewDepth,
        viewPosition.y * parameters.P11 / viewDepth);
    float2 ndcRadius = abs(float2(
        parameters.P00,
        parameters.P11)) * radius * inverseTangentDistance;
    float2 pixelCenter = float2(
        (ndcCenter.x * 0.5 + 0.5) * float(parameters.ScreenWidth),
        (1.0 - (ndcCenter.y * 0.5 + 0.5)) * float(parameters.ScreenHeight));
    float2 pixelRadius = ndcRadius * 0.5 * float2(
        parameters.ScreenWidth,
        parameters.ScreenHeight);
    float2 pixelMinimum = pixelCenter - pixelRadius;
    float2 pixelMaximum = pixelCenter + pixelRadius;
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

[shader("compute")]
[numthreads(64, 1, 1)]
void BuildClusteredLightGrid(uint3 dispatchThreadId : SV_DispatchThreadID)
{
    uint viewIndex = dispatchThreadId.y;
    ClusteredLightGridParameters parameters = LightingViews[viewIndex];
    uint localLightIndex = dispatchThreadId.x;
    if (localLightIndex >= parameters.NonDirectionalCount)
        return;

    uint lightIndex = parameters.DirectionalCount + localLightIndex;
    GPULight light = LightBuffer[lightIndex];
    float3 worldCenter;
    float radius;
    GetClusteredLightBoundingSphere(light, worldCenter, radius);
    if (radius <= 0.0)
        return;

    float3 viewPosition = mul(
        float4(worldCenter, 1.0),
        parameters.View).xyz;
    float viewDepth = -viewPosition.z;
    float nearDepth = parameters.ZParams.x;
    float farDepth = parameters.ZParams.y;
    float minimumDepth = max(nearDepth, viewDepth - radius);
    float maximumDepth = min(farDepth, viewDepth + radius);
    if (maximumDepth < nearDepth || minimumDepth > farDepth)
        return;

    uint2 minimumTile;
    uint2 maximumTile;
    if (!GetClusteredLightTileBounds(
            parameters,
            viewPosition,
            radius,
            minimumTile,
            maximumTile))
    {
        return;
    }

    uint minimumSlice = ClusteredLightDepthSlice(
        parameters,
        minimumDepth);
    uint maximumSlice = ClusteredLightDepthSlice(
        parameters,
        maximumDepth);
    uint cellsPerSlice =
        parameters.TileCountX * parameters.TileCountY;
    uint viewOffset = viewIndex * parameters.CellCountPerView;
    for (uint slice = minimumSlice; slice <= maximumSlice; slice++)
    {
        uint sliceOffset = slice * cellsPerSlice;
        for (uint tileY = minimumTile.y; tileY <= maximumTile.y; tileY++)
        {
            uint rowOffset = viewOffset + sliceOffset + tileY * parameters.TileCountX;
            for (uint tileX = minimumTile.x; tileX <= maximumTile.x; tileX++)
            {
                uint cell = rowOffset + tileX;
                uint slot;
                InterlockedAdd(ClusteredLightGrid[cell].y, 1u, slot);
                if (slot < parameters.MaxLightsPerCluster)
                {
                    uint outputOffset =
                        cell * parameters.MaxLightsPerCluster;
                    LightIndexList[outputOffset + slot] = lightIndex;
                }
            }
        }
    }
}
