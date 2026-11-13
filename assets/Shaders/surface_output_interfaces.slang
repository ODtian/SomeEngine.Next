#ifndef SURFACE_OUTPUT_INTERFACES_SLANG
#define SURFACE_OUTPUT_INTERFACES_SLANG

interface ISurfaceOutput
{
    [mutating] void SetIndex(uint index);
    void Write(uint3 pixel, float4 color);
    void Write(uint3 pixel, float3 premultipliedColor, float3 transmittance);
};

#endif
