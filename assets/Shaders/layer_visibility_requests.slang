#ifndef LAYER_VISIBILITY_REQUESTS_SLANG
#define LAYER_VISIBILITY_REQUESTS_SLANG

__exported import layer_visibility_output;
__exported import vsm_page_requests;

struct LayerShadowPageRequests<let threadCount : uint> : ILayerPageRequests
{
    static void Begin(uint tile, uint lane)
    {
        BeginVirtualShadowTileRequests(lane, threadCount);
    }

    static void Request(uint tile, uint3 pixel, float depth, uint identity, float3 worldPosition, bool firstPerson)
    {
        RequestVirtualShadowLayerReceiver<VirtualShadowTilePageRequests>(worldPosition, pixel.z, firstPerson);
    }

    static void End(uint tile, uint lane) { }
};

struct LayerHardwareShadowPageRequests : ILayerPageRequests
{
    static void Begin(uint tile, uint lane) { }
    static void Request(uint tile, uint3 pixel, float depth, uint identity, float3 worldPosition, bool firstPerson)
    {
        RequestVirtualShadowLayerReceiver<VirtualShadowGlobalPageRequests>(worldPosition, pixel.z, firstPerson);
    }
    static void End(uint tile, uint lane) { }
};

#endif
