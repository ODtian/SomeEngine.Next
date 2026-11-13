#ifndef LAYER_FRAGMENT_TYPES_SLANG
#define LAYER_FRAGMENT_TYPES_SLANG

struct LayerFragment
{
    float Depth;
    uint Identity;
    uint Packed;
};

struct LayerBlockHeader
{
    uint Tile;
    uint Next;
    uint Count;
};

// Six half components occupy three uint words on every shader target.
typedef uint3 LayerColor;

static const uint LAYER_BLOCK_EMPTY = 0xffffffffu;
static const uint LAYER_FRAGMENT_FRONT = 1u << 24u;
static const uint LAYER_FRAME_FRAGMENTS = 0u;
static const uint LAYER_FRAME_BLOCK_COUNTER = 1u;
static const uint LAYER_FRAME_OVERFLOW = 2u;
static const uint LAYER_FRAME_SLOW_SCRATCH = 3u;
static const uint LAYER_FRAME_MAX_PIXEL_FRAGMENTS = 4u;
static const uint LAYER_FRAME_SPLIT_TILES = 5u;
static const uint LAYER_FRAME_SLOW_PIXELS = 6u;
static const uint LAYER_FRAME_SOURCE_BLOCK_CAPACITY = 7u;
static const uint LAYER_FRAME_COUNTER_COUNT = 8u;

uint PackLayerFragment(uint pixel, uint passIndex, uint drawOrder, uint reserved = 0u)
{
    return (pixel & 1023u) | ((passIndex & 63u) << 10u) |
        ((drawOrder & 255u) << 16u) | ((reserved & 255u) << 24u);
}

uint LayerFragmentPixel(LayerFragment fragment) { return fragment.Packed & 1023u; }
uint LayerFragmentPass(LayerFragment fragment) { return (fragment.Packed >> 10u) & 63u; }
uint LayerFragmentDrawOrder(LayerFragment fragment) { return (fragment.Packed >> 16u) & 255u; }
uint LayerFragmentReserved(LayerFragment fragment) { return fragment.Packed >> 24u; }
bool LayerFragmentIsFront(LayerFragment fragment) { return (fragment.Packed & LAYER_FRAGMENT_FRONT) != 0u; }

LayerColor PackLayerColor(float3 premultipliedColor, float3 transmittance)
{
    uint3 color = f32tof16(premultipliedColor);
    uint3 transmit = f32tof16(transmittance);
    return uint3(color.x | (color.y << 16u), color.z | (transmit.x << 16u),
        transmit.y | (transmit.z << 16u));
}

float3 LayerColorPremultiplied(LayerColor color)
{
    return f16tof32(uint3(color.x & 65535u, color.x >> 16u, color.y & 65535u));
}

float3 LayerColorTransmittance(LayerColor color)
{
    return f16tof32(uint3(color.y >> 16u, color.z & 65535u, color.z >> 16u));
}

uint GetLayerBlockByteOffset(uint block, uint blockSize) { return block * (12u + blockSize * 12u); }
uint GetLayerFragmentByteOffset(uint block, uint record, uint blockSize)
{ return GetLayerBlockByteOffset(block, blockSize) + 12u + record * 12u; }
uint GetLayerFragmentIndex(uint block, uint record, uint blockSize) { return block * blockSize + record; }

#endif
