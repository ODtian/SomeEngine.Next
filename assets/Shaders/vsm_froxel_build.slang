#include "vsm_froxel_parameters.slang"
#include "surface_receiver.slang"

ConstantBuffer<VirtualShadowFroxelParameters> FroxelParameters;
Texture2DArray<float> FroxelHiZ;
StructuredBuffer<SurfaceReceiver> FroxelReceivers;
StructuredBuffer<SurfaceReceiver> FroxelAdditionalReceivers;
RWStructuredBuffer<uint2> OutFroxels;
RWStructuredBuffer<uint> OutFroxelArguments;
groupshared int FroxelSlices[64];

void EmitVirtualShadowFroxel(uint2 tile, int slice, uint countIndex, uint first)
{
    uint index;
    InterlockedAdd(OutFroxelArguments[countIndex], 1u, index);
    OutFroxels[first + index] = uint2(tile.x | (tile.y << 16u), asuint(slice));
}

bool FirstVirtualShadowFroxelSlice(uint lane, int slice)
{
    if (slice == VSM_FROXEL_INVALID_SLICE) return false;
    for (uint previous = 0u; previous < lane; previous++)
        if (FroxelSlices[previous] == slice) return false;
    return true;
}

[shader("compute")]
[numthreads(8,8,1)]
void BuildVirtualShadowFroxels(uint3 tile : SV_GroupID, uint3 local : SV_GroupThreadID, uint lane : SV_GroupIndex)
{
    uint2 pixel = tile.xy * 8u + local.xy;
    bool inView = all(pixel < uint2(FroxelParameters.Width, FroxelParameters.Height));
    uint pixelIndex = pixel.y * FroxelParameters.Width + pixel.x;
    int slice = VSM_FROXEL_INVALID_SLICE;
    if (inView)
    {
        SurfaceReceiver receiver = FroxelReceivers[pixelIndex];
        bool eligible = (receiver.Flags & SURFACE_RECEIVER_PRESENT) == 0u || SurfaceReceiverHasLighting(receiver);
        if (eligible && (receiver.Flags & (SURFACE_RECEIVER_HAIR | SURFACE_RECEIVER_FIRST_PERSON)) == 0u)
            slice = VirtualShadowFroxelSlice(FroxelParameters, FroxelHiZ.Load(int4(pixel, 0, 0)));
    }
    FroxelSlices[lane] = slice;
    GroupMemoryBarrierWithGroupSync();
    if (FirstVirtualShadowFroxelSlice(lane, slice)) EmitVirtualShadowFroxel(tile.xy, slice, 3u, 0u);
    GroupMemoryBarrierWithGroupSync();
    for (uint layer = 0u; layer < FroxelParameters.AdditionalReceiverLayerCount; layer++)
    {
        slice = VSM_FROXEL_INVALID_SLICE;
        if (inView)
        {
            SurfaceReceiver extra = FroxelAdditionalReceivers[layer * FroxelParameters.Width * FroxelParameters.Height + pixelIndex];
            if ((extra.Flags & SURFACE_RECEIVER_TRANSLUCENCY) != 0u) InterlockedOr(OutFroxelArguments[8], 1u);
            if (SurfaceReceiverHasLighting(extra) && (extra.Flags & SURFACE_RECEIVER_WATER) != 0u && extra.Depth >= 0.0)
                slice = VirtualShadowFroxelSlice(FroxelParameters, extra.Depth);
        }
        FroxelSlices[lane] = slice;
        GroupMemoryBarrierWithGroupSync();
        if (FirstVirtualShadowFroxelSlice(lane, slice))
            EmitVirtualShadowFroxel(tile.xy, slice, 7u, FroxelParameters.Width * FroxelParameters.Height);
        GroupMemoryBarrierWithGroupSync();
    }
}

[shader("compute")]
[numthreads(1,1,1)]
void FinishVirtualShadowFroxelArguments()
{
    uint pixelMode = max(OutFroxelArguments[8], FroxelParameters.HighQualityFrontLayerTranslucency);
    OutFroxelArguments[8] = pixelMode;
    OutFroxelArguments[0] = pixelMode != 0u ? 0u : (OutFroxelArguments[3] + 63u) / 64u;
    OutFroxelArguments[1] = OutFroxelArguments[2] = 1u;
    OutFroxelArguments[4] = pixelMode != 0u ? 0u : (OutFroxelArguments[7] + 63u) / 64u;
    OutFroxelArguments[5] = OutFroxelArguments[6] = 1u;
    OutFroxelArguments[12] = (FroxelParameters.Width * FroxelParameters.Height + 63u) / 64u;
    OutFroxelArguments[13] = OutFroxelArguments[14] = 1u;
}
