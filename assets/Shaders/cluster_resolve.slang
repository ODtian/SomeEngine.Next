#include "visbuffer_encoding.slang"

struct ResolveUniforms
{
    uint ScreenWidth;
    uint ScreenHeight;
    uint Pad0;
    uint Pad1;
};

ConstantBuffer<ResolveUniforms> Uniforms;

Texture2DArray<uint> VisBuffer;

RWTexture2DArray<float4> OutputColor;

[shader("compute")]
[numthreads(8, 8, 1)]
void CSResolve(uint3 dispatchThreadID : SV_DispatchThreadID)
{
    uint3 pixelCoord = dispatchThreadID;

    if (pixelCoord.x >= Uniforms.ScreenWidth || pixelCoord.y >= Uniforms.ScreenHeight)
        return;

    uint visData = VisBuffer[pixelCoord];

    // 0 means no geometry (clear value)
    if (IsVisBufferEmpty(visData))
        OutputColor[pixelCoord] = float4(0, 0, 0, 1);
}
