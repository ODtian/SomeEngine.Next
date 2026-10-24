#include "hiz_common.slang"
#include "resource_effects.slang"

Texture2DArray<float> DepthTexture;
[ResourceEffect(ResourceEffects.Write, ResourceQualifiers.None)]
RWTexture2DArray<float> Mip0;
[ResourceEffect(ResourceEffects.Write, ResourceQualifiers.None)]
RWTexture2DArray<float> Mip1;
[ResourceEffect(ResourceEffects.Write, ResourceQualifiers.None)]
RWTexture2DArray<float> Mip2;
[ResourceEffect(ResourceEffects.Write, ResourceQualifiers.None)]
RWTexture2DArray<float> Mip3;
[ResourceEffect(ResourceEffects.Write, ResourceQualifiers.None)]
RWTexture2DArray<float> Mip4;

groupshared float FirstDepth[15 * 15];
groupshared float SecondDepth[7 * 7];
groupshared float ThirdDepth[3 * 3];

uint2 TileEnd(uint2 group, uint2 groups, uint2 size, uint span)
{
    return uint2(group.x + 1u == groups.x ? size.x : (group.x + 1u) * span,
        group.y + 1u == groups.y ? size.y : (group.y + 1u) * span);
}

void InitFullTile(uint3 group, uint lane)
{
    uint2 local = uint2(lane & 7u, lane >> 3u);
    uint2 pixel = group.xy * 16u + local * 2u;
    float a = DepthTexture.Load(int4(pixel, group.z, 0));
    float b = DepthTexture.Load(int4(pixel + uint2(1, 0), group.z, 0));
    float c = DepthTexture.Load(int4(pixel + uint2(0, 1), group.z, 0));
    float d = DepthTexture.Load(int4(pixel + uint2(1, 1), group.z, 0));
    Mip0[uint3(pixel, group.z)] = a;
    Mip0[uint3(pixel + uint2(1, 0), group.z)] = b;
    Mip0[uint3(pixel + uint2(0, 1), group.z)] = c;
    Mip0[uint3(pixel + uint2(1, 1), group.z)] = d;
    float value = max(0.0, max(max(a, b), max(c, d)));
    Mip1[uint3(group.xy * 8u + local, group.z)] = value;
    FirstDepth[local.y * 15u + local.x] = value;
    GroupMemoryBarrierWithGroupSync();

    if (lane < 16u)
    {
        local = uint2(lane & 3u, lane >> 2u);
        uint source = local.y * 30u + local.x * 2u;
        value = max(max(FirstDepth[source], FirstDepth[source + 1u]),
            max(FirstDepth[source + 15u], FirstDepth[source + 16u]));
        Mip2[uint3(group.xy * 4u + local, group.z)] = value;
        SecondDepth[local.y * 7u + local.x] = value;
    }
    GroupMemoryBarrierWithGroupSync();

    if (lane < 4u)
    {
        local = uint2(lane & 1u, lane >> 1u);
        uint source = local.y * 14u + local.x * 2u;
        value = max(max(SecondDepth[source], SecondDepth[source + 1u]),
            max(SecondDepth[source + 7u], SecondDepth[source + 8u]));
        Mip3[uint3(group.xy * 2u + local, group.z)] = value;
        ThirdDepth[local.y * 3u + local.x] = value;
    }
    GroupMemoryBarrierWithGroupSync();

    if (lane == 0u)
        Mip4[group] = max(max(ThirdDepth[0], ThirdDepth[1]), max(ThirdDepth[3], ThirdDepth[4]));
}

// Each group owns one mip-four pixel and all of its descendants. The last
// group takes the odd remainder at every level, preserving edge coverage.
[shader("compute")]
[numthreads(64, 1, 1)]
void InitHiZ(uint3 group : SV_GroupID, uint lane : SV_GroupIndex)
{
    uint width, height, layers;
    Mip0.GetDimensions(width, height, layers);
    uint2 size0 = uint2(width, height);
    uint2 size1 = max(size0 >> 1u, 1u);
    uint2 size2 = max(size0 >> 2u, 1u);
    uint2 size3 = max(size0 >> 3u, 1u);
    uint2 size4 = max(size0 >> 4u, 1u);
    // A complete 16x16 subtree has fixed 2x2 reductions at every level.
    // The predicate is uniform within the group. Test the complete extent,
    // including the final odd remainder, before using fixed addressing.
    if (all(TileEnd(group.xy, size4, size0, 16u) - group.xy * 16u == 16u))
    {
        InitFullTile(group, lane);
        return;
    }
    uint2 start1 = group.xy * 8u;
    uint2 start2 = group.xy * 4u;
    uint2 start3 = group.xy * 2u;
    uint2 count1 = TileEnd(group.xy, size4, size1, 8u) - start1;
    uint2 count2 = TileEnd(group.xy, size4, size2, 4u) - start2;
    uint2 count3 = TileEnd(group.xy, size4, size3, 2u) - start3;

    for (uint index = lane; index < count1.x * count1.y; index += 64u)
    {
        uint2 local = uint2(index % count1.x, index / count1.x);
        uint2 position = start1 + local;
        uint2 begin = position * 2u;
        uint2 end = uint2(CoveredEnd(position.x, size1.x, size0.x),
            CoveredEnd(position.y, size1.y, size0.y));
        float value = 0.0;
        for (uint y = begin.y; y <= end.y; y++)
        for (uint x = begin.x; x <= end.x; x++)
        {
            float depth = DepthTexture.Load(int4(x, y, group.z, 0));
            Mip0[uint3(x, y, group.z)] = depth;
            value = max(value, depth);
        }
        Mip1[uint3(position, group.z)] = value;
        FirstDepth[local.y * 15u + local.x] = value;
    }
    GroupMemoryBarrierWithGroupSync();

    if (lane < count2.x * count2.y)
    {
        uint2 local = uint2(lane % count2.x, lane / count2.x);
        uint2 position = start2 + local;
        uint2 begin = position * 2u - start1;
        uint2 end = uint2(CoveredEnd(position.x, size2.x, size1.x),
            CoveredEnd(position.y, size2.y, size1.y)) - start1;
        float value = 0.0;
        for (uint y = begin.y; y <= end.y; y++)
        for (uint x = begin.x; x <= end.x; x++) value = max(value, FirstDepth[y * 15u + x]);
        Mip2[uint3(position, group.z)] = value;
        SecondDepth[local.y * 7u + local.x] = value;
    }
    GroupMemoryBarrierWithGroupSync();

    if (lane < count3.x * count3.y)
    {
        uint2 local = uint2(lane % count3.x, lane / count3.x);
        uint2 position = start3 + local;
        uint2 begin = position * 2u - start2;
        uint2 end = uint2(CoveredEnd(position.x, size3.x, size2.x),
            CoveredEnd(position.y, size3.y, size2.y)) - start2;
        float value = 0.0;
        for (uint y = begin.y; y <= end.y; y++)
        for (uint x = begin.x; x <= end.x; x++) value = max(value, SecondDepth[y * 7u + x]);
        Mip3[uint3(position, group.z)] = value;
        ThirdDepth[local.y * 3u + local.x] = value;
    }
    GroupMemoryBarrierWithGroupSync();

    if (lane == 0u)
    {
        float value = 0.0;
        for (uint y = 0u; y < count3.y; y++)
        for (uint x = 0u; x < count3.x; x++) value = max(value, ThirdDepth[y * 3u + x]);
        Mip4[group] = value;
    }
}
