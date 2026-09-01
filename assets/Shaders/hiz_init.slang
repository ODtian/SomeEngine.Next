#include "hiz_common.slang"
#include "resource_effects.slang"

Texture2DArray<float> DepthTexture;
[ResourceEffect(ResourceEffects.Write, ResourceQualifiers.None)]
RWTexture2DArray<float> Mip0;
[ResourceEffect(ResourceEffects.Write, ResourceQualifiers.None)]
RWTexture2DArray<float> Mip1;

[shader("compute")]
[numthreads(8, 8, 1)]
void InitHiZ(uint3 threadId : SV_DispatchThreadID)
{
    uint2 coordinate = threadId.xy;
    uint viewIndex = threadId.z;
    uint destinationWidth;
    uint destinationHeight;
    uint destinationLayers;
    Mip1.GetDimensions(destinationWidth, destinationHeight, destinationLayers);
    if (coordinate.x >= destinationWidth || coordinate.y >= destinationHeight)
        return;

    uint sourceWidth;
    uint sourceHeight;
    uint sourceLayers;
    Mip0.GetDimensions(sourceWidth, sourceHeight, sourceLayers);
    uint2 sourceBase = coordinate * 2u;
    uint sourceEndX = CoveredEnd(coordinate.x, destinationWidth, sourceWidth);
    uint sourceEndY = CoveredEnd(coordinate.y, destinationHeight, sourceHeight);

    float maximumDepth = 0.0;
    for (uint y = sourceBase.y; y <= sourceEndY; y++)
    {
        for (uint x = sourceBase.x; x <= sourceEndX; x++)
        {
            float depth = DepthTexture.Load(int4(x, y, viewIndex, 0));
            Mip0[uint3(x, y, viewIndex)] = depth;
            maximumDepth = max(maximumDepth, depth);
        }
    }
    Mip1[uint3(coordinate, viewIndex)] = maximumDepth;
}
