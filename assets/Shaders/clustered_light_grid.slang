#include "common.slang"
#include "render_lighting.slang"
#include "resource_effects.slang"
#include "cluster_overflow.slang"

ConstantBuffer<ClusteredLightGridParameters> LightGridUniforms;
StructuredBuffer<GPULight> LightBuffer;
[ResourceEffect(ResourceEffects.ReadWrite, ResourceQualifiers.Atomic)]
RWStructuredBuffer<uint2> ClusteredLightGrid;
[ResourceEffect(ResourceEffects.Write, ResourceQualifiers.None)]
RWStructuredBuffer<uint> LightIndexList;
[ResourceEffect(ResourceEffects.ReadWrite, ResourceQualifiers.Atomic)]
RWStructuredBuffer<uint> ClusterOverflowCounters;

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
    float3 viewPosition,
    float radius,
    out uint2 minimumTile,
    out uint2 maximumTile)
{
    float viewDepth = -viewPosition.z;
    float nearDepth = LightGridUniforms.ZParams.x;
    if (viewDepth + radius <= nearDepth)
        return false;

    if (viewDepth <= radius + nearDepth)
    {
        minimumTile = uint2(0u, 0u);
        maximumTile = uint2(
            LightGridUniforms.TileCountX - 1u,
            LightGridUniforms.TileCountY - 1u);
        return true;
    }

    float inverseTangentDistance = rsqrt(max(
        viewDepth * viewDepth - radius * radius,
        1e-8));
    float2 ndcCenter = float2(
        viewPosition.x * LightGridUniforms.P00 / viewDepth,
        viewPosition.y * LightGridUniforms.P11 / viewDepth);
    float2 ndcRadius = abs(float2(
        LightGridUniforms.P00,
        LightGridUniforms.P11)) * radius * inverseTangentDistance;
    float2 pixelCenter = float2(
        (ndcCenter.x * 0.5 + 0.5) * float(LightGridUniforms.ScreenWidth),
        (1.0 - (ndcCenter.y * 0.5 + 0.5)) * float(LightGridUniforms.ScreenHeight));
    float2 pixelRadius = ndcRadius * 0.5 * float2(
        LightGridUniforms.ScreenWidth,
        LightGridUniforms.ScreenHeight);
    float2 pixelMinimum = pixelCenter - pixelRadius;
    float2 pixelMaximum = pixelCenter + pixelRadius;
    if (pixelMaximum.x < 0.0 || pixelMaximum.y < 0.0 ||
        pixelMinimum.x >= float(LightGridUniforms.ScreenWidth) ||
        pixelMinimum.y >= float(LightGridUniforms.ScreenHeight))
    {
        return false;
    }

    int2 tileMinimum = int2(floor(pixelMinimum / float2(
        LightGridUniforms.TileSizeX,
        LightGridUniforms.TileSizeY)));
    int2 tileMaximum = int2(floor(pixelMaximum / float2(
        LightGridUniforms.TileSizeX,
        LightGridUniforms.TileSizeY)));
    minimumTile = uint2(clamp(
        tileMinimum,
        int2(0, 0),
        int2(
            int(LightGridUniforms.TileCountX) - 1,
            int(LightGridUniforms.TileCountY) - 1)));
    maximumTile = uint2(clamp(
        tileMaximum,
        int2(0, 0),
        int2(
            int(LightGridUniforms.TileCountX) - 1,
            int(LightGridUniforms.TileCountY) - 1)));
    return true;
}

[shader("compute")]
[numthreads(64, 1, 1)]
void BuildClusteredLightGrid(uint3 dispatchThreadId : SV_DispatchThreadID)
{
    uint localLightIndex = dispatchThreadId.x;
    if (localLightIndex >= LightGridUniforms.NonDirectionalCount)
        return;

    uint lightIndex = LightGridUniforms.DirectionalCount + localLightIndex;
    GPULight light = LightBuffer[lightIndex];
    float3 worldCenter;
    float radius;
    GetClusteredLightBoundingSphere(light, worldCenter, radius);
    if (radius <= 0.0)
        return;

    float3 viewPosition = mul(
        float4(worldCenter, 1.0),
        LightGridUniforms.View).xyz;
    float viewDepth = -viewPosition.z;
    float nearDepth = LightGridUniforms.ZParams.x;
    float farDepth = LightGridUniforms.ZParams.y;
    float minimumDepth = max(nearDepth, viewDepth - radius);
    float maximumDepth = min(farDepth, viewDepth + radius);
    if (maximumDepth < nearDepth || minimumDepth > farDepth)
        return;

    uint2 minimumTile;
    uint2 maximumTile;
    if (!GetClusteredLightTileBounds(
            viewPosition,
            radius,
            minimumTile,
            maximumTile))
    {
        return;
    }

    uint minimumSlice = ClusteredLightDepthSlice(
        LightGridUniforms,
        minimumDepth);
    uint maximumSlice = ClusteredLightDepthSlice(
        LightGridUniforms,
        maximumDepth);
    uint cellsPerSlice =
        LightGridUniforms.TileCountX * LightGridUniforms.TileCountY;
    for (uint slice = minimumSlice; slice <= maximumSlice; slice++)
    {
        uint sliceOffset = slice * cellsPerSlice;
        for (uint tileY = minimumTile.y; tileY <= maximumTile.y; tileY++)
        {
            uint rowOffset = sliceOffset + tileY * LightGridUniforms.TileCountX;
            for (uint tileX = minimumTile.x; tileX <= maximumTile.x; tileX++)
            {
                uint cell = rowOffset + tileX;
                uint slot;
                InterlockedAdd(ClusteredLightGrid[cell].y, 1u, slot);
                if (slot < LightGridUniforms.MaxLightsPerCluster)
                {
                    uint outputOffset =
                        cell * LightGridUniforms.MaxLightsPerCluster;
                    LightIndexList[outputOffset + slot] = lightIndex;
                }
                else
                {
                    uint ignored;
                    InterlockedAdd(
                        ClusterOverflowCounters[CLUSTER_OVERFLOW_LIGHT_GRID],
                        1u,
                        ignored);
                }
            }
        }
    }
}
