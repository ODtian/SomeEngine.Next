#include "hiz_common.slang"
#include "resource_effects.slang"

[ResourceEffect(ResourceEffects.Read, ResourceQualifiers.None)]
RWTexture2DArray<float> Source;
[ResourceEffect(ResourceEffects.Write, ResourceQualifiers.None)]
RWTexture2DArray<float> Middle;
[ResourceEffect(ResourceEffects.Write, ResourceQualifiers.None)]
RWTexture2DArray<float> Destination;

[shader("compute")]
[numthreads(8, 8, 1)]
void ReduceHiZPair(uint3 threadId : SV_DispatchThreadID)
{
    uint2 coordinate = threadId.xy;
    uint viewIndex = threadId.z;
    uint destinationWidth;
    uint destinationHeight;
    uint destinationLayers;
    Destination.GetDimensions(destinationWidth, destinationHeight, destinationLayers);
    if (coordinate.x >= destinationWidth || coordinate.y >= destinationHeight)
        return;

    uint middleWidth;
    uint middleHeight;
    uint middleLayers;
    Middle.GetDimensions(middleWidth, middleHeight, middleLayers);
    uint sourceWidth;
    uint sourceHeight;
    uint sourceLayers;
    Source.GetDimensions(sourceWidth, sourceHeight, sourceLayers);

    uint2 middleBase = coordinate * 2u;
    uint middleEndX = CoveredEnd(coordinate.x, destinationWidth, middleWidth);
    uint middleEndY = CoveredEnd(coordinate.y, destinationHeight, middleHeight);
    float maximumDepth = 0.0;

    for (uint middleY = middleBase.y; middleY <= middleEndY; middleY++)
    {
        for (uint middleX = middleBase.x; middleX <= middleEndX; middleX++)
        {
            uint2 middleCoordinate = uint2(middleX, middleY);
            uint2 sourceBase = middleCoordinate * 2u;
            uint sourceEndX = CoveredEnd(middleCoordinate.x, middleWidth, sourceWidth);
            uint sourceEndY = CoveredEnd(middleCoordinate.y, middleHeight, sourceHeight);
            float middleDepth = 0.0;
            for (uint sourceY = sourceBase.y; sourceY <= sourceEndY; sourceY++)
                for (uint sourceX = sourceBase.x; sourceX <= sourceEndX; sourceX++)
                    middleDepth = max(
                        middleDepth,
                        Source[uint3(sourceX, sourceY, viewIndex)]);
            Middle[uint3(middleCoordinate, viewIndex)] = middleDepth;
            maximumDepth = max(maximumDepth, middleDepth);
        }
    }
    Destination[uint3(coordinate, viewIndex)] = maximumDepth;
}
