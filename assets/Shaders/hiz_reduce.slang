#include "hiz_common.slang"
#include "resource_effects.slang"

[ResourceEffect(ResourceEffects.Read, ResourceQualifiers.None)]
RWTexture2DArray<float> Source;
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
[ResourceEffect(ResourceEffects.Write, ResourceQualifiers.None)]
RWTexture2DArray<float> Mip5;
[ResourceEffect(ResourceEffects.Write, ResourceQualifiers.None)]
RWTexture2DArray<float> Mip6;

groupshared float EvenDepth[64 * 64];
groupshared float OddDepth[32 * 32];

void WriteDepthMip(uint mip, uint3 position, float value)
{
    switch (mip)
    {
        case 0u: Mip0[position] = value; break;
        case 1u: Mip1[position] = value; break;
        case 2u: Mip2[position] = value; break;
        case 3u: Mip3[position] = value; break;
        case 4u: Mip4[position] = value; break;
        case 5u: Mip5[position] = value; break;
        default: Mip6[position] = value; break;
    }
}

// Once the first output fits 64 by 64, one group per view can finish every
// remaining mip without another dispatch or inter-group synchronization.
[shader("compute")]
[numthreads(256, 1, 1)]
void ReduceHiZ(uint3 group : SV_GroupID, uint lane : SV_GroupIndex)
{
    uint width, height, layers;
    Source.GetDimensions(width, height, layers);
    uint2 sourceSize = uint2(width, height);
    uint2 size = max(sourceSize >> 1u, 1u);
    for (uint index = lane; index < size.x * size.y; index += 256u)
    {
        uint2 position = uint2(index % size.x, index / size.x);
        uint2 begin = position * 2u;
        uint2 end = uint2(CoveredEnd(position.x, size.x, sourceSize.x),
            CoveredEnd(position.y, size.y, sourceSize.y));
        float value = 0.0;
        for (uint y = begin.y; y <= end.y; y++)
        for (uint x = begin.x; x <= end.x; x++) value = max(value, Source[uint3(x, y, group.z)]);
        EvenDepth[index] = value;
        Mip0[uint3(position, group.z)] = value;
    }
    GroupMemoryBarrierWithGroupSync();

    for (uint mip = 1u; mip < 7u; mip++)
    {
        if (all(size == 1u)) break;
        sourceSize = size;
        size = max(sourceSize >> 1u, 1u);
        for (uint index = lane; index < size.x * size.y; index += 256u)
        {
            uint2 position = uint2(index % size.x, index / size.x);
            uint2 begin = position * 2u;
            uint2 end = uint2(CoveredEnd(position.x, size.x, sourceSize.x),
                CoveredEnd(position.y, size.y, sourceSize.y));
            float value = 0.0;
            for (uint y = begin.y; y <= end.y; y++)
            for (uint x = begin.x; x <= end.x; x++)
            {
                uint address = y * sourceSize.x + x;
                value = max(value, (mip & 1u) != 0u ? EvenDepth[address] : OddDepth[address]);
            }
            WriteDepthMip(mip, uint3(position, group.z), value);
            if ((mip & 1u) != 0u) OddDepth[index] = value;
            else EvenDepth[index] = value;
        }
        GroupMemoryBarrierWithGroupSync();
    }
}
