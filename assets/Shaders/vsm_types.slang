__exported import vsm_page_entry;

struct VirtualShadowMapParameters
{
    float4x4 LightViewProjection;
    uint VirtualResolution;
    uint PageSize;
    uint AtlasSize;
    uint AtlasPagesPerRow;
    uint MaxPages;
    int VirtualPageOriginX;
    float DepthBias;
    uint PageTableOffset;
    uint CacheGeneration;
    int VirtualPageOriginY;
    int ClipmapLevel;
    uint LightIndex;
    float3 ClipmapWorldOrigin;
    float ResolutionLodBias;
    int FirstClipmapLevel;
    uint ClipmapLevelCount;
    uint MipLevelCount;
    uint MinMipLevel;
    uint Flags;
    uint LightKind;
    uint FaceIndex;
    float WorldUnitsPerCentimeter;
    float3 LightPosition;
    float LightRange;
    float GeometryLodScale;
    float MaxPixelsPerEdge;
    float MinPixelsPerEdgeHardware;
    float DicingRate;
    float DepthCenterZ;
    float DepthRadius;
    float2 DepthCullRange;
    float3 WorldOriginHigh;
    uint ReceiverMaskOriginX;
    float3 WorldOriginLow;
    uint ReceiverMaskOriginY;
    int ClipmapCornerRelativeOffsetX;
    int ClipmapCornerRelativeOffsetY;
    uint2 WorldPageOriginX;
    uint2 WorldPageOriginY;
    float PageDilationBorder;
    uint RangeId;
    float3 LightDirection;
    float SourceRadius;
    float SourceAngleRadians;
    float ProjectionDepthScale;
    float ProjectionDepthOffset;
    float TexelDitherScale;
    float3 WorldTranslation;
    uint RangeGeneration;
    float4x4 ShadowUvNormal;
    float4x4 ShadowUv;
};

struct VirtualShadowDepthSample
{
    // Depth and sample coordinates all belong to the actual atlas source view.
    bool Valid;
    float Depth;
    uint ViewIndex;
    uint MipLevel;
    float2 UV;
    float2 TexelCenter;
};

// Positions passed to this file are offsets from the owning Game world's origin.
// Users with another high/low reference can translate before projecting or sampling.
float3 TranslateVirtualShadowWorldPosition(float3 position, float3 originHigh,
    float3 originLow, VirtualShadowMapParameters view)
{
    return position + ((originHigh - view.WorldOriginHigh) + (originLow - view.WorldOriginLow)) - view.WorldTranslation;
}

float4 ProjectVirtualShadowClip(VirtualShadowMapParameters view, float3 worldPosition)
{
    return mul(float4(worldPosition + view.WorldTranslation, 1.0), view.LightViewProjection);
}

static const uint VSM_LIGHT_DIRECTIONAL = 0u;
static const uint VSM_LIGHT_POINT = 1u;
static const uint VSM_LIGHT_SPOT = 2u;
static const uint VSM_VIEW_COARSE_CLIP_LEVEL = 1u << 0u;
static const uint VSM_VIEW_FORCE_CACHE_DYNAMIC_COARSE = 1u << 1u;
static const uint VSM_VIEW_USE_RECEIVER_MASK = 1u << 2u;
static const uint VSM_VIEW_PREVIOUS_PROJECTION_VALID = 1u << 3u;
static const uint VSM_VIEW_ORTHOGRAPHIC_RECEIVER = 1u << 4u;
static const uint VSM_VIEW_EVEN_MIP_LEVELS = 1u << 5u;
static const uint VSM_VIEW_ODD_MIP_LEVELS = 1u << 6u;
static const uint VSM_VIEW_SINGLE_PAGE = 1u << 7u;
static const uint VSM_VIEW_DISTANT_LIGHT = 1u << 8u;
static const uint VSM_VIEW_CACHED_DISTANT_LIGHT = 1u << 9u;
static const uint VSM_VIEW_PREFILTERED_DISTANT = 1u << 10u;
static const uint VSM_VIEW_FIRST_PERSON_SHADOW = 1u << 11u;
static const uint VSM_VIEW_DIRECTIONAL_DYNAMIC_DEPTH_CACHE = 1u << 12u;
static const uint VSM_PAGE_REQUEST_DETAIL = 1u << 0u;
static const uint VSM_PAGE_REQUEST_COARSE = 1u << 1u;
static const uint VSM_PAGE_REQUEST_SECONDARY = 1u << 2u;
static const uint VSM_PAGE_REQUEST_PRIMARY = 1u << 3u;

static const uint VSM_PAGE_UNMAPPED = 0u;
// A completed masked page is usable only in its covered receiver cells this frame.
// It is not a whole-page cache entry or a source of prior-frame HiZ.
static const uint VSM_PAGE_MASKED = 1u;
static const uint VSM_PAGE_PRIOR_HIZ = 2u;
