#ifndef CLUSTER_VISIBILITY_RASTER_OUTPUT_SLANG
#define CLUSTER_VISIBILITY_RASTER_OUTPUT_SLANG

#include "resource_effects.slang"

// Depth and visibility are one indivisible winner. Splitting these fields into two 32-bit
// stores permits a losing fragment to publish its id after another fragment won the depth race.
[ResourceEffect(ResourceEffects.ReadWrite, ResourceQualifiers.Atomic)]
RWTexture2DArray<uint64_t> VisibilityWinnerImage;
[ResourceEffect(ResourceEffects.ReadWrite, ResourceQualifiers.Atomic)]
RWByteAddressBuffer VisibilityWinnerBuffer;

uint64_t PackVisibilityWinner(float depth, uint visibilityValue)
{
    uint reversedDepth = asuint(1.0 - saturate(depth));
    return (uint64_t(reversedDepth) << 32u) | uint64_t(visibilityValue);
}

void WriteVisibilityWinnerImage(
    int2 pixel,
    uint viewIndex,
    float depth,
    uint visibilityValue)
{
    uint64_t ignored;
    InterlockedMax(
        VisibilityWinnerImage[uint3(pixel, viewIndex)],
        PackVisibilityWinner(depth, visibilityValue),
        ignored);
}

void WriteVisibilityWinnerBuffer(
    int2 pixel,
    uint viewIndex,
    uint width,
    uint height,
    float depth,
    uint visibilityValue)
{
    uint64_t linear =
        (uint64_t(viewIndex) * uint64_t(height) + uint64_t(pixel.y)) * uint64_t(width) +
        uint64_t(pixel.x);
    uint64_t ignored = VisibilityWinnerBuffer.InterlockedMaxU64(
        uint(linear * 8u),
        PackVisibilityWinner(depth, visibilityValue));
}

#endif
