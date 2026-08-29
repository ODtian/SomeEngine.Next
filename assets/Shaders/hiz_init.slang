#include "hiz_common.slang"
#include "resource_effects.slang"

Texture2D<float> DepthTexture;
[ResourceEffect(ResourceEffects.Write, ResourceQualifiers.None)]
RWTexture2D<float> Mip0;
[ResourceEffect(ResourceEffects.Write, ResourceQualifiers.None)]
RWTexture2D<float> Mip1;

[shader("compute")]
[numthreads(8, 8, 1)]
void InitHiZ(uint3 threadId : SV_DispatchThreadID)
{
    uint2 coordinate = threadId.xy;
    uint destinationWidth;
    uint destinationHeight;
    Mip1.GetDimensions(destinationWidth, destinationHeight);
    if (coordinate.x >= destinationWidth || coordinate.y >= destinationHeight)
        return;

    uint sourceWidth;
    uint sourceHeight;
    Mip0.GetDimensions(sourceWidth, sourceHeight);
    uint2 sourceBase = coordinate * 2u;
    uint sourceEndX = CoveredEnd(coordinate.x, destinationWidth, sourceWidth);
    uint sourceEndY = CoveredEnd(coordinate.y, destinationHeight, sourceHeight);

    float maximumDepth = 0.0;
    for (uint y = sourceBase.y; y <= sourceEndY; y++)
    {
        for (uint x = sourceBase.x; x <= sourceEndX; x++)
        {
            float depth = DepthTexture.Load(int3(x, y, 0));
            Mip0[uint2(x, y)] = depth;
            maximumDepth = max(maximumDepth, depth);
        }
    }
    Mip1[coordinate] = maximumDepth;
}
