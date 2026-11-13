#ifndef CLUSTER_VISIBILITY_RASTER_OUTPUT_SLANG
#define CLUSTER_VISIBILITY_RASTER_OUTPUT_SLANG

__exported import resource_effects;

// A64 outputs publish depth and identity together. NonAtomic selects independent 32-bit stores.
[ResourceEffect(ResourceEffects.ReadWrite, ResourceQualifiers.Atomic)]
RWTexture2DArray<uint64_t> VisibilityWinnerImage;
[ResourceEffect(ResourceEffects.ReadWrite, ResourceQualifiers.Atomic)]
RWByteAddressBuffer VisibilityWinnerBuffer;
[ResourceEffect(ResourceEffects.ReadWrite, ResourceQualifiers.None)]
RWByteAddressBuffer VisibilityNonAtomicBuffer;

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

uint ReadVisibilityNonAtomicDepth(int2 pixel, uint viewIndex, uint width, uint height)
{
    uint offset = ((viewIndex * height + uint(pixel.y)) * width + uint(pixel.x)) * 8u;
    return asuint(1.0 - asfloat(VisibilityNonAtomicBuffer.Load(offset + 4u)));
}

void WriteVisibilityNonAtomic(int2 pixel, uint viewIndex, uint width, uint height,
    float depth, uint visibilityValue)
{
    uint offset = ((viewIndex * height + uint(pixel.y)) * width + uint(pixel.x)) * 8u;
    depth = saturate(depth);
    if (depth <= asfloat(VisibilityNonAtomicBuffer.Load(offset + 4u)))
    {
        VisibilityNonAtomicBuffer.Store(offset + 4u, asuint(depth));
        VisibilityNonAtomicBuffer.Store(offset, visibilityValue);
    }
}

#endif
