#ifndef SURFACE_OUTPUTS_SLANG
#define SURFACE_OUTPUTS_SLANG

__exported import surface_output_interfaces;
__exported import layer_fragment_store;

struct LayerPayloadOutput : ISurfaceOutput
{
    uint FragmentIndex;

    [mutating] void SetIndex(uint index) { FragmentIndex = index; }

    void Write(uint3 pixel, float4 color)
    {
        StoreLayerColor(FragmentIndex, color.rgb * color.a, (1.0 - color.a).xxx);
    }

    void Write(uint3 pixel, float3 premultipliedColor, float3 transmittance)
    {
        StoreLayerColor(FragmentIndex, premultipliedColor, transmittance);
    }
};

#endif
