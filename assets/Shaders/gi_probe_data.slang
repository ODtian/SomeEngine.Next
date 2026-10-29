#ifndef GI_PROBE_DATA_SLANG
#define GI_PROBE_DATA_SLANG

struct GiScreenSettings
{
    uint Width;
    uint Height;
    uint ProbeWidth;
    uint ProbeHeight;
    uint Frame;
    uint HistoryLayer;
    uint UseHistory;
    uint ViewCount;
};

struct GiProbeSurface
{
    float4 Position;
    float4 Normal;
};

static const uint GI_PROBE_SPACING = 8u;

float GiSh(uint coefficient, float3 direction)
{
    switch (coefficient)
    {
        case 0u: return 0.2820947918;
        case 1u: return 0.4886025119 * direction.y;
        case 2u: return 0.4886025119 * direction.z;
        case 3u: return 0.4886025119 * direction.x;
        case 4u: return 1.0925484306 * direction.x * direction.y;
        case 5u: return 1.0925484306 * direction.y * direction.z;
        case 6u: return 0.3153915653 * (3.0 * direction.z * direction.z - 1.0);
        case 7u: return 1.0925484306 * direction.x * direction.z;
        default: return 0.5462742153 * (direction.x * direction.x - direction.y * direction.y);
    }
}

#endif
