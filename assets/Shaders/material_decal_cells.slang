#include "material_decals.slang"
#include "resource_effects.slang"

[ResourceEffect(ResourceEffects.ReadWrite, ResourceQualifiers.Atomic)]
RWStructuredBuffer<uint> MaterialDecalCountOutput;
RWStructuredBuffer<uint> MaterialDecalOffsetOutput;
[ResourceEffect(ResourceEffects.ReadWrite, ResourceQualifiers.Atomic)]
RWStructuredBuffer<uint> MaterialDecalWrites;
RWStructuredBuffer<uint> MaterialDecalReferenceOutput;
RWStructuredBuffer<MaterialDecalCellRange> MaterialDecalRanges;
[ResourceEffect(ResourceEffects.ReadWrite, ResourceQualifiers.Atomic)]
RWByteAddressBuffer MaterialDecalWork;

uint DecalCell(MaterialDecalCellRange range, uint3 coordinate)
{
    ClusteredLightGridParameters grid = MaterialDecalLightingViews[range.View];
    return range.View * grid.CellCountPerView + (coordinate.z * grid.TileCountY + coordinate.y) * grid.TileCountX + coordinate.x;
}

bool DecalCellBounds(uint index, uint view, out MaterialDecalCellRange range)
{
    range = (MaterialDecalCellRange)0;
    range.View = view;
    MaterialDecalVolume volume = MaterialDecalVolumes[index];
    ClusteredLightGridParameters grid = MaterialDecalLightingViews[view];
    float2 low = float2(grid.ScreenWidth, grid.ScreenHeight), high = 0.0.xx;
    float nearDepth = 1e30, farDepth = -1e30;
    bool crossesNear = false;
    for (uint corner = 0u; corner < 8u; corner++)
    {
        float3 local = float3((corner & 1u) != 0u ? 1.0 : -1.0,
            (corner & 2u) != 0u ? 1.0 : -1.0, (corner & 4u) != 0u ? 1.0 : -1.0);
        float3 world = mul(float4(local, 1.0), volume.DecalToWorld).xyz;
        float4 viewPosition = mul(float4(world, 1.0), grid.View);
        float depth = -viewPosition.z;
        nearDepth = min(nearDepth, depth); farDepth = max(farDepth, depth);
        crossesNear = crossesNear || depth < grid.ZParams.x;
        float4 clip = mul(viewPosition, grid.Projection);
        float2 pixel = (clip.xy / clip.w * float2(.5, -.5) + .5) * float2(grid.ScreenWidth, grid.ScreenHeight);
        low = min(low, pixel); high = max(high, pixel);
    }
    if (farDepth < grid.ZParams.x || nearDepth > grid.ZParams.y) return false;
    if (crossesNear) { low = 0.0.xx; high = float2(grid.ScreenWidth, grid.ScreenHeight) - 1; }
    if (any(high < 0.0) || any(low >= float2(grid.ScreenWidth, grid.ScreenHeight))) return false;
    low = max(0.0.xx, floor(low)); high = min(float2(grid.ScreenWidth, grid.ScreenHeight) - 1, ceil(high));
    range.Minimum = uint3(uint2(low) / uint2(grid.TileSizeX, grid.TileSizeY), ClusteredLightDepthSlice(grid, nearDepth));
    range.Maximum = uint3(uint2(high) / uint2(grid.TileSizeX, grid.TileSizeY), ClusteredLightDepthSlice(grid, farDepth));
    range.Valid = 1u;
    return true;
}

[shader("compute")] [numthreads(64,1,1)]
void ClearMaterialDecalCells(uint3 thread : SV_DispatchThreadID)
{
    if (thread.x < MaterialDecalUniforms.CellCount)
    {
        MaterialDecalCountOutput[thread.x] = 0u;
        MaterialDecalWrites[thread.x] = 0u;
    }
    if (thread.x == 0u) MaterialDecalWork.Store4(0u, uint4(0u, 0u, MaterialDecalUniforms.Count, 0u));
}

[shader("compute")] [numthreads(64,1,1)]
void CountMaterialDecalCells(uint3 thread : SV_DispatchThreadID)
{
    uint count = MaterialDecalUniforms.Count;
    if (count == 0u) return;
    uint viewCount = MaterialDecalUniforms.CellCount / MaterialDecalLightingViews[0].CellCountPerView;
    if (thread.x >= count * viewCount) return;
    MaterialDecalCellRange range;
    bool visible = DecalCellBounds(thread.x % count, thread.x / count, range);
    MaterialDecalRanges[thread.x] = range;
    if (!visible) return;
    for (uint z = range.Minimum.z; z <= range.Maximum.z; z++)
    for (uint y = range.Minimum.y; y <= range.Maximum.y; y++)
    for (uint x = range.Minimum.x; x <= range.Maximum.x; x++)
        InterlockedAdd(MaterialDecalCountOutput[DecalCell(range, uint3(x,y,z))], 1u);
}

[shader("compute")] [numthreads(64,1,1)]
void ReserveMaterialDecalCells(uint3 thread : SV_DispatchThreadID)
{
    if (thread.x >= MaterialDecalUniforms.CellCount) return;
    uint count = MaterialDecalCountOutput[thread.x], offset;
    MaterialDecalWork.InterlockedAdd(0u, count, offset);
    bool retained = offset + count <= MaterialDecalUniforms.ReferenceCapacity;
    MaterialDecalOffsetOutput[thread.x] = retained ? offset : 0xFFFFFFFFu;
    if (retained) MaterialDecalWork.InterlockedAdd(4u, count);
}

[shader("compute")] [numthreads(64,1,1)]
void ScatterMaterialDecalCells(uint3 thread : SV_DispatchThreadID)
{
    uint count = MaterialDecalUniforms.Count;
    if (count == 0u) return;
    uint viewCount = MaterialDecalUniforms.CellCount / MaterialDecalLightingViews[0].CellCountPerView;
    if (thread.x >= count * viewCount) return;
    MaterialDecalCellRange range = MaterialDecalRanges[thread.x];
    if (range.Valid == 0u) return;
    for (uint z = range.Minimum.z; z <= range.Maximum.z; z++)
    for (uint y = range.Minimum.y; y <= range.Maximum.y; y++)
    for (uint x = range.Minimum.x; x <= range.Maximum.x; x++)
    {
        uint cell = DecalCell(range, uint3(x,y,z)), offset = MaterialDecalOffsetOutput[cell];
        if (offset == 0xFFFFFFFFu) continue;
        uint item;
        InterlockedAdd(MaterialDecalWrites[cell], 1u, item);
        MaterialDecalReferenceOutput[offset + item] = thread.x % count;
    }
}

[shader("compute")] [numthreads(64,1,1)]
void SortMaterialDecalCells(uint3 thread : SV_DispatchThreadID)
{
    if (thread.x >= MaterialDecalUniforms.CellCount) return;
    uint offset = MaterialDecalOffsetOutput[thread.x], count = MaterialDecalCountOutput[thread.x];
    if (offset == 0xFFFFFFFFu) return;
    // Source indices follow the caller's order; each cell restores that order after atomic scatter.
    for (uint item = 1u; item < count; item++)
    {
        uint value = MaterialDecalReferenceOutput[offset + item], index = item;
        while (index > 0u && MaterialDecalReferenceOutput[offset + index - 1u] > value)
        {
            MaterialDecalReferenceOutput[offset + index] = MaterialDecalReferenceOutput[offset + index - 1u];
            index--;
        }
        MaterialDecalReferenceOutput[offset + index] = value;
    }
}
