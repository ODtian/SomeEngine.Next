#ifndef LAYER_VISIBILITY_OUTPUT_SLANG
#define LAYER_VISIBILITY_OUTPUT_SLANG

__exported import layer_fragment_store;

interface ILayerPageRequests
{
    static void Begin(uint tile, uint lane);
    static void Request(uint tile, uint3 pixel, float depth, uint identity, float3 worldPosition, bool firstPerson);
    static void End(uint tile, uint lane);
};

struct LayerNoPageRequests : ILayerPageRequests
{
    static void Begin(uint tile, uint lane) { }
    static void Request(uint tile, uint3 pixel, float depth, uint identity, float3 worldPosition, bool firstPerson) { }
    static void End(uint tile, uint lane) { }
};

struct LayerVisibilityOutput<TStore : ILayerFragmentStore, TRequests : ILayerPageRequests>
{
    uint Tile;
    uint WaveIndex;
    uint PassIndex;
    uint DrawOrder;
    bool ClipByOpaqueDepth;

    [mutating] void Begin(uint lane)
    {
        WaveIndex = WaveReadLaneFirst(lane);
        TStore.Begin(lane);
        TRequests.Begin(Tile, lane);
    }

    uint Write(bool visible, uint3 pixel, uint localPixel, float depth, uint identity,
        float opaqueDepth, float3 worldPosition, bool firstPerson, bool frontFace)
    {
        if (!visible || (ClipByOpaqueDepth && depth > opaqueDepth)) return LAYER_BLOCK_EMPTY;
        LayerFragment fragment;
        fragment.Depth = depth;
        fragment.Identity = identity;
        fragment.Packed = PackLayerFragment(localPixel, PassIndex, DrawOrder, frontFace ? 1u : 0u);
        TRequests.Request(Tile, pixel, depth, identity, worldPosition, firstPerson);
        return TStore.Append(Tile, fragment, WaveIndex);
    }

    void End(uint lane)
    {
        TRequests.End(Tile, lane);
        TStore.End(lane);
    }
};

#endif
