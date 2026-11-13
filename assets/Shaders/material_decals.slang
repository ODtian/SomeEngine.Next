#ifndef MATERIAL_DECALS_SLANG
#define MATERIAL_DECALS_SLANG

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

struct MaterialDecalBinningView
{
    float4x4 View;
    float4x4 Projection;
    float4x4 InverseProjection;
    uint2 ScreenSize;
    uint2 TileCount;
    uint TileSize;
    uint DepthSliceCount;
    uint CellCount;
    uint DecalCount;
    float4 Depth;
};

ConstantBuffer<MaterialDecalParameters> MaterialDecalUniforms;
StructuredBuffer<MaterialDecalVolume> MaterialDecalVolumes;
ByteAddressBuffer MaterialDecalProperties;
StructuredBuffer<MaterialDecalBinningView> MaterialDecalViews;
StructuredBuffer<uint2> MaterialDecalCells;
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
    MaterialDecalBinningView grid = MaterialDecalViews[pixel.z];
    float depth = max(-mul(float4(position, 1.0), grid.View).z, grid.Depth.x);
    uint slice = uint(clamp(floor(log2(depth) * grid.Depth.z + grid.Depth.w),
        0.0, float(grid.DepthSliceCount - 1u)));
    uint2 tile = pixel.xy / grid.TileSize;
    uint cell = pixel.z * grid.CellCount + (slice * grid.TileCount.y + tile.y) * grid.TileCount.x + tile.x;
    uint2 range = MaterialDecalCells[cell];
    input.Offset = range.x; input.Count = range.y; input.Indexed = true;
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
