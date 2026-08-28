#ifndef VISBUFFER_ENCODING_SLANG
#define VISBUFFER_ENCODING_SLANG

static const uint VISBUFFER_EMPTY = 0u;
static const uint VISBUFFER_TRIANGLE_BITS = 7u;
static const uint VISBUFFER_TRIANGLE_MASK = (1u << VISBUFFER_TRIANGLE_BITS) - 1u;

uint EncodeVisBuffer(uint visibleClusterIndex, uint triangleID)
{
    return ((visibleClusterIndex + 1u) << VISBUFFER_TRIANGLE_BITS)
        | (triangleID & VISBUFFER_TRIANGLE_MASK);
}

uint DecodeVisBufferVisibleClusterIndex(uint visData)
{
    return (visData >> VISBUFFER_TRIANGLE_BITS) - 1u;
}

uint DecodeVisBufferTriangleID(uint visData)
{
    return visData & VISBUFFER_TRIANGLE_MASK;
}

bool IsVisBufferEmpty(uint visData)
{
    return visData == VISBUFFER_EMPTY;
}

#endif
