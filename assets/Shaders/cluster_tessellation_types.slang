#ifndef CLUSTER_TESSELLATION_TYPES_SLANG
#define CLUSTER_TESSELLATION_TYPES_SLANG

struct TessellationUniforms
{
    uint ScreenWidth;
    uint ScreenHeight;
    uint CurrentBin;
    uint DispatchGroupWidth;
    uint MaxSplitWorkItems;
    uint MaxDicedTriangles;
    uint MaxFactor;
    float TargetEdgePixels;
};

struct TessellationWorkItem
{
    uint VisibleClusterIndex;
    uint OriginalTriangle;
    uint ViewIndex;
    uint Factor;
};

// The original visibility identity is carried beside the diced positions. Deferred shade uses
// depth plus this identity to run the surface's inverse; no diced-triangle identity leaks into the
// visibility ABI.
struct DicedTriangle
{
    float3 Position0;
    uint VisibleClusterIndex;
    float3 Position1;
    uint OriginalTriangle;
    float3 Position2;
    uint ViewIndex;
};

static const uint TESS_COUNTER_SPLIT_COUNT = 0u;
static const uint TESS_COUNTER_DICED_COUNT = 4u;
static const uint TESS_COUNTER_SPLIT_OVERFLOW = 8u;
static const uint TESS_COUNTER_DICED_OVERFLOW = 12u;

#endif
