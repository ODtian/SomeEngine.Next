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

#if defined(CLUSTER_VISIBILITY_IDENTITY_TIES) && CLUSTER_VISIBILITY_IDENTITY_TIES
// Opt-in, append-order-independent ties for one immutable visible/residency epoch.
// PageOffset is a resident address, not a persistent asset identifier across relocation.
// Keep the existing packed depth encoding and visible-index payload format unchanged.
uint64_t VisibilityIdentityMaximum(uint64_t first, uint64_t second)
{
    uint firstPayload = uint(first);
    uint secondPayload = uint(second);
    if (firstPayload == 0u) return second;
    if (secondPayload == 0u) return first;
    if (firstPayload == secondPayload) return first;
    DrawRequest a = VisibleClusterRequest(RasterOutputVisibleClusters[
        DecodeVisBufferVisibleClusterIndex(firstPayload)]);
    DrawRequest b = VisibleClusterRequest(RasterOutputVisibleClusters[
        DecodeVisBufferVisibleClusterIndex(secondPayload)]);
    if (a.InstanceID != b.InstanceID) return a.InstanceID > b.InstanceID ? first : second;
    if (a.PageOffset != b.PageOffset) return a.PageOffset > b.PageOffset ? first : second;
    if (a.ClusterAndView != b.ClusterAndView) return a.ClusterAndView > b.ClusterAndView ? first : second;
    uint firstTriangle = DecodeVisBufferTriangleID(firstPayload);
    uint secondTriangle = DecodeVisBufferTriangleID(secondPayload);
    if (firstTriangle != secondTriangle) return firstTriangle > secondTriangle ? first : second;
    // Duplicate work for the same identity may retain either lookup index.
    return max(first, second);
}

// Every primary writer must use this protocol. A plain numeric max writer could
// overwrite a repaired semantic winner without retaining its repair obligation.
// Invariant: depth never decreases, and the greatest submitted semantic identity at
// the current greatest depth is either stored or retained by an unfinished repair.
// Atomic max returns any displaced winner; failed CAS merges the actual winner;
// successful CAS transfers the obligation back to memory. A greater depth ends it.
// After the last initial max, every successful repair strictly improves semantic
// order, so finite competing writers terminate under normal atomic forward progress.
// Do not cap retries or mix legacy max-only writers into this opt-in epoch. Clear
// must complete first; visible indices and resident pages stay valid through shading.
void WriteVisibilityWinnerImage(int2 pixel, uint viewIndex, float depth, uint visibilityValue)
{
    uint64_t incoming = PackVisibilityWinner(depth, visibilityValue);
    uint64_t previous;
    InterlockedMax(VisibilityWinnerImage[uint3(pixel, viewIndex)], incoming, previous);
    if (uint(previous >> 32u) != uint(incoming >> 32u)) return;
    uint64_t expected = max(previous, incoming);
    uint64_t best = VisibilityIdentityMaximum(previous, incoming);
    // The old atomic-return value, not a pre-read, carries a displaced best winner.
    [loop]
    while (best != expected)
    {
        uint64_t observed;
        InterlockedCompareExchange(VisibilityWinnerImage[uint3(pixel, viewIndex)],
            expected, best, observed);
        if (observed == expected) return;
        // Depth is monotonic; a differing depth can only supersede this obligation.
        if (uint(observed >> 32u) != uint(incoming >> 32u)) return;
        best = VisibilityIdentityMaximum(best, observed);
        expected = observed;
    }
}

void WriteVisibilityWinnerBuffer(int2 pixel, uint viewIndex, uint width, uint height,
    float depth, uint visibilityValue)
{
    uint64_t linear = (uint64_t(viewIndex) * uint64_t(height) + uint64_t(pixel.y)) *
        uint64_t(width) + uint64_t(pixel.x);
    uint address = uint(linear * 8u);
    uint64_t incoming = PackVisibilityWinner(depth, visibilityValue);
    uint64_t previous = VisibilityWinnerBuffer.InterlockedMaxU64(address, incoming);
    if (uint(previous >> 32u) != uint(incoming >> 32u)) return;
    uint64_t expected = max(previous, incoming);
    uint64_t best = VisibilityIdentityMaximum(previous, incoming);
    [loop]
    while (best != expected)
    {
        uint64_t observed;
        VisibilityWinnerBuffer.InterlockedCompareExchange64(address, expected, best, observed);
        if (observed == expected) return;
        if (uint(observed >> 32u) != uint(incoming >> 32u)) return;
        best = VisibilityIdentityMaximum(best, observed);
        expected = observed;
    }
}
#else
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

#endif
