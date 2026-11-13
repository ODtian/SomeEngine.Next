#ifndef LAYER_COMPOSITION_PARAMETERS_SLANG
#define LAYER_COMPOSITION_PARAMETERS_SLANG

struct LayerCompositionParameters
{
    uint Width, Height, ViewCount, TileSize;
    uint TileCountX, TileCountY, TileCount, BlockSize;
    uint BlockCapacity, FragmentCapacity, PassCount, PassIndex;
    uint SortMode, ClipByOpaqueDepth, Accumulator, HardwareWindowTiles;
};

ConstantBuffer<LayerCompositionParameters> LayerCompositionUniforms;

#endif
