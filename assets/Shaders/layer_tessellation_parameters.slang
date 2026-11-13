#ifndef LAYER_TESSELLATION_PARAMETERS_SLANG
#define LAYER_TESSELLATION_PARAMETERS_SLANG

struct LayerTessellationParameters
{
    uint MaxFactor;
    float TargetEdgePixels;
    uint2 Padding;
};

ConstantBuffer<LayerTessellationParameters> LayerTessellation;

#endif
