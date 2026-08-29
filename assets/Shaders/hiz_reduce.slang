#include "hiz_common.slang"
#include "resource_effects.slang"

[ResourceEffect(ResourceEffects.Read, ResourceQualifiers.None)]
RWTexture2D<float> Source;
[ResourceEffect(ResourceEffects.Write, ResourceQualifiers.None)]
RWTexture2D<float> Destination;

[shader("compute")]
[numthreads(8, 8, 1)]
void ReduceHiZ(uint3 threadId : SV_DispatchThreadID)
{
    uint2 coordinate = threadId.xy;
    uint destinationWidth;
    uint destinationHeight;
    Destination.GetDimensions(destinationWidth, destinationHeight);
    if (coordinate.x >= destinationWidth || coordinate.y >= destinationHeight)
        return;

    uint sourceWidth;
    uint sourceHeight;
    Source.GetDimensions(sourceWidth, sourceHeight);
    uint2 sourceBase = coordinate * 2u;
    uint sourceEndX = CoveredEnd(coordinate.x, destinationWidth, sourceWidth);
    uint sourceEndY = CoveredEnd(coordinate.y, destinationHeight, sourceHeight);

    float maximumDepth = 0.0;
    for (uint y = sourceBase.y; y <= sourceEndY; y++)
        for (uint x = sourceBase.x; x <= sourceEndX; x++)
            maximumDepth = max(maximumDepth, Source[uint2(x, y)]);
    Destination[coordinate] = maximumDepth;
}
