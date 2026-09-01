#include "hiz_common.slang"
#include "resource_effects.slang"

[ResourceEffect(ResourceEffects.Read, ResourceQualifiers.None)]
RWTexture2DArray<float> Source;
[ResourceEffect(ResourceEffects.Write, ResourceQualifiers.None)]
RWTexture2DArray<float> Destination;

[shader("compute")]
[numthreads(8, 8, 1)]
void ReduceHiZ(uint3 threadId : SV_DispatchThreadID)
{
    uint2 coordinate = threadId.xy;
    uint viewIndex = threadId.z;
    uint destinationWidth;
    uint destinationHeight;
    uint destinationLayers;
    Destination.GetDimensions(destinationWidth, destinationHeight, destinationLayers);
    if (coordinate.x >= destinationWidth || coordinate.y >= destinationHeight)
        return;

    uint sourceWidth;
    uint sourceHeight;
    uint sourceLayers;
    Source.GetDimensions(sourceWidth, sourceHeight, sourceLayers);
    uint2 sourceBase = coordinate * 2u;
    uint sourceEndX = CoveredEnd(coordinate.x, destinationWidth, sourceWidth);
    uint sourceEndY = CoveredEnd(coordinate.y, destinationHeight, sourceHeight);

    float maximumDepth = 0.0;
    for (uint y = sourceBase.y; y <= sourceEndY; y++)
        for (uint x = sourceBase.x; x <= sourceEndX; x++)
            maximumDepth = max(maximumDepth, Source[uint3(x, y, viewIndex)]);
    Destination[uint3(coordinate, viewIndex)] = maximumDepth;
}
