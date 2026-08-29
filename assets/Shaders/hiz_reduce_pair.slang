#include "hiz_common.slang"
#include "resource_effects.slang"

[ResourceEffect(ResourceEffects.Read, ResourceQualifiers.None)]
RWTexture2D<float> Source;
[ResourceEffect(ResourceEffects.Write, ResourceQualifiers.None)]
RWTexture2D<float> Middle;
[ResourceEffect(ResourceEffects.Write, ResourceQualifiers.None)]
RWTexture2D<float> Destination;

[shader("compute")]
[numthreads(8, 8, 1)]
void ReduceHiZPair(uint3 threadId : SV_DispatchThreadID)
{
    uint2 coordinate = threadId.xy;
    uint destinationWidth;
    uint destinationHeight;
    Destination.GetDimensions(destinationWidth, destinationHeight);
    if (coordinate.x >= destinationWidth || coordinate.y >= destinationHeight)
        return;

    uint middleWidth;
    uint middleHeight;
    Middle.GetDimensions(middleWidth, middleHeight);
    uint sourceWidth;
    uint sourceHeight;
    Source.GetDimensions(sourceWidth, sourceHeight);

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
                    middleDepth = max(middleDepth, Source[uint2(sourceX, sourceY)]);
            Middle[middleCoordinate] = middleDepth;
            maximumDepth = max(maximumDepth, middleDepth);
        }
    }
    Destination[coordinate] = maximumDepth;
}
