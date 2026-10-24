#ifndef MATERIAL_DECALS_SLANG
#define MATERIAL_DECALS_SLANG
#include "render_lighting.slang"

[__AttributeUsage(_AttributeTargets.Struct)]
struct MaterialDecalScalarsAttribute {};

struct MaterialDecalVolume
{
    float4x4 WorldToDecal;
    float4x4 DecalToWorld;
    uint Parameters;
    uint Padding0, Padding1, Padding2;
};

struct MaterialDecalParameters
{
    uint Count, CellCount, ReferenceCapacity, UseIndex;
};

struct MaterialDecalCellRange
{
    uint3 Minimum;
    uint Valid;
    uint3 Maximum;
    uint View;
};

ConstantBuffer<MaterialDecalParameters> MaterialDecalUniforms;
StructuredBuffer<MaterialDecalVolume> MaterialDecalVolumes;
ByteAddressBuffer MaterialDecalProperties;
StructuredBuffer<ClusteredLightGridParameters> MaterialDecalLightingViews;
StructuredBuffer<uint> MaterialDecalCounts;
StructuredBuffer<uint> MaterialDecalOffsets;
StructuredBuffer<uint> MaterialDecalReferences;

struct MaterialDecalInput
{
    MaterialDecalVolume Volume;
    float3 Position, PositionDx, PositionDy;
};

struct MaterialDecalIterator
{
    uint Next, Count, Offset;
    bool Indexed;
};

MaterialDecalIterator BeginMaterialDecals(uint3 pixel, float3 position)
{
    MaterialDecalIterator input = { 0u, MaterialDecalUniforms.Count, 0u, false };
    if (input.Count == 0u || MaterialDecalUniforms.UseIndex == 0u) return input;
    ClusteredLightGridParameters grid = MaterialDecalLightingViews[pixel.z];
    float depth = max(-mul(float4(position, 1.0), grid.View).z, grid.ZParams.x);
    uint cell = ClusteredLightCellIndex(grid, pixel.z, pixel.xy, depth);
    uint offset = MaterialDecalOffsets[cell];
    if (offset == 0xFFFFFFFFu) return input;
    input.Offset = offset; input.Count = MaterialDecalCounts[cell]; input.Indexed = true;
    return input;
}

bool NextMaterialDecal(inout MaterialDecalIterator input, float3 position, float3 positionDx,
    float3 positionDy, out MaterialDecalInput result)
{
    while (input.Next < input.Count)
    {
        uint index = input.Indexed ? MaterialDecalReferences[input.Offset + input.Next] : input.Next;
        input.Next++;
        MaterialDecalVolume volume = MaterialDecalVolumes[index];
        float3 local = mul(float4(position, 1.0), volume.WorldToDecal).xyz;
        if (any(abs(local) > 1.0)) continue;
        result.Volume = volume; result.Position = local;
        result.PositionDx = mul(float4(positionDx, 0.0), volume.WorldToDecal).xyz;
        result.PositionDy = mul(float4(positionDy, 0.0), volume.WorldToDecal).xyz;
        return true;
    }
    return false;
}
#endif
