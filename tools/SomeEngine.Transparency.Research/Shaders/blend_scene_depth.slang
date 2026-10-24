#define BLEND_BACKGROUND 1
#include "blend_effect_common.slang"

[shader("vertex")]
float4 BlendSceneVertex(uint vertex : SV_VertexID) : SV_Position
{
    return float4(vertex == 2 ? 3 : -1, vertex == 1 ? 3 : -1, 0, 1);
}

[shader("fragment")]
float BlendSceneDepth(float4 position : SV_Position) : SV_Depth
{
    float2 point = BlendPixelPoint(uint2(position.xy));
    float depth = point.x < 0 ? 12 : 36;
    if (Settings.Scene == 1)
    {
        depth = 12 + floor((point.x + 1) * 2) * 6;
        if (abs(point.x) < .08 || abs(point.y) > .8) depth = 64;
    }
    return DeviceDepth(depth);
}
