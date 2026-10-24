#ifndef CLUSTER_TESSELLATION_TYPES_SLANG
#define CLUSTER_TESSELLATION_TYPES_SLANG

static const uint TESS_PATTERN_MAX_FACTOR = 8u;

struct TessellationUniforms
{
    uint ScreenWidth;
    uint ScreenHeight;
    uint CurrentBin;
    uint MaxPatchItems;
    uint MaxFactor;
    float TargetEdgePixels;
    uint UsePreviousHiZ;
    uint SplitLevel;
};

// Keep the denominator divisible by 2^5, the maximum recursive edge-split depth
// allowed by MaxFactor<=256. Every shared-edge dyadic coordinate is therefore exact
// in either triangle orientation, avoiding one-LSB cracks from an odd denominator.
static const uint TESS_BARYCENTRIC_MAX = 65504u;

// Persistent runtime patch. Six 16-bit normalized barycentric components give
// substantially more precision than the removed 1/256 encoding while keeping the
// queue payload compact. With the supported refinement cap (<=5 recursive levels
// at MaxFactor=256), centroid movement remains orders of magnitude above one LSB.
struct TessellationPatch
{
    uint VisibleClusterIndex;
    uint TriangleAndRasterBin;
    uint Barycentric0;
    uint Barycentric1;
    uint Barycentric2;
    uint PackedState;
};

// Final records retain all six 16-bit barycentric coordinates. Source indices
// need 22 bits at the supported candidate capacity; triangles use eight bits,
// bins 24 bits, and the three final factors use three bits each (minus one).
struct TessellationFinalPatch
{
    uint SourceAndBin;
    uint BinAndFactors;
    uint Barycentric0;
    uint Barycentric1;
    uint Barycentric2;
};

uint4 EncodeTessellationWorkPatch(TessellationPatch patch)
{
    return uint4(
        patch.VisibleClusterIndex | ((patch.TriangleAndRasterBin & 0xFFu) << 22u),
        patch.Barycentric0, patch.Barycentric1, patch.Barycentric2);
}

TessellationPatch DecodeTessellationWorkPatch(uint4 data, uint bin, uint depth)
{
    TessellationPatch patch;
    patch.VisibleClusterIndex = data.x & 0x3FFFFFu;
    patch.TriangleAndRasterBin = ((data.x >> 22u) & 0xFFu) | (bin << 8u);
    patch.Barycentric0 = data.y;
    patch.Barycentric1 = data.z;
    patch.Barycentric2 = data.w;
    patch.PackedState = 0x111u | (depth << 12u);
    return patch;
}

TessellationFinalPatch EncodeTessellationFinalPatch(TessellationPatch patch)
{
    uint bin = patch.TriangleAndRasterBin >> 8u;
    TessellationFinalPatch data;
    data.SourceAndBin = patch.VisibleClusterIndex |
        ((patch.TriangleAndRasterBin & 0xFFu) << 22u) | ((bin & 3u) << 30u);
    data.BinAndFactors = (bin >> 2u) |
        (((patch.PackedState & 0xFu) - 1u) << 22u) |
        ((((patch.PackedState >> 4u) & 0xFu) - 1u) << 25u) |
        ((((patch.PackedState >> 8u) & 0xFu) - 1u) << 28u);
    data.Barycentric0 = patch.Barycentric0;
    data.Barycentric1 = patch.Barycentric1;
    data.Barycentric2 = patch.Barycentric2;
    return data;
}

TessellationPatch DecodeTessellationFinalPatch(TessellationFinalPatch data)
{
    uint bin = (data.SourceAndBin >> 30u) | ((data.BinAndFactors & 0x3FFFFFu) << 2u);
    TessellationPatch patch;
    patch.VisibleClusterIndex = data.SourceAndBin & 0x3FFFFFu;
    patch.TriangleAndRasterBin = ((data.SourceAndBin >> 22u) & 0xFFu) | (bin << 8u);
    patch.Barycentric0 = data.Barycentric0;
    patch.Barycentric1 = data.Barycentric1;
    patch.Barycentric2 = data.Barycentric2;
    patch.PackedState = (((data.BinAndFactors >> 22u) & 7u) + 1u) |
        ((((data.BinAndFactors >> 25u) & 7u) + 1u) << 4u) |
        ((((data.BinAndFactors >> 28u) & 7u) + 1u) << 8u);
    return patch;
}

struct TessellationBinUniforms
{
    uint BinCount;
    uint MaxPatchItems;
    uint SourceCounterOffset;
    uint Pad0;
};

uint TessellationDicingLanes(uint factor)
{
    return factor <= 1u ? 1u : factor <= 2u ? 4u : factor <= 4u ? 16u : 32u;
}

struct TessellationBinEntry
{
    uint Count;
    uint Offset;
};

static const uint TESS_COUNTER_ROOT_COUNT = 0u;
static const uint TESS_COUNTER_WORK_NODE_COUNT = 4u;
static const uint TESS_COUNTER_FINAL_COUNT = 8u;
static const uint TESS_COUNTER_OVERFLOW = 12u;
static const uint TESS_COUNTER_CULLED = 16u;
static const uint TESS_COUNTER_QUALITY_LIMIT = 20u;
static const uint TESS_COUNTER_PROCESSED = 24u;

static const uint TESS_SPLIT_LEVEL_COUNT = 6u;
static const uint TESS_SPLIT_THREADS = 32u;

uint TessellationPatchTriangle(TessellationPatch patch)
{
    return patch.TriangleAndRasterBin & 0xFFu;
}

uint TessellationPatchRasterBin(TessellationPatch patch)
{
    return patch.TriangleAndRasterBin >> 8u;
}

uint EncodeTessellationBarycentric(float3 barycentric)
{
    barycentric = max(barycentric, 0.0.xxx);
    float sum = barycentric.x + barycentric.y + barycentric.z;
    barycentric = sum > 1e-12
        ? barycentric / sum
        : float3(1.0, 0.0, 0.0);
    uint y = min(uint(round(barycentric.y * TESS_BARYCENTRIC_MAX)), TESS_BARYCENTRIC_MAX);
    uint z = min(uint(round(barycentric.z * TESS_BARYCENTRIC_MAX)), TESS_BARYCENTRIC_MAX);
    if (y + z > TESS_BARYCENTRIC_MAX)
    {
        uint excess = y + z - TESS_BARYCENTRIC_MAX;
        if (y >= z)
            y -= min(y, excess);
        else
            z -= min(z, excess);
    }
    return y | (z << 16u);
}

void SetTessellationPatchBarycentrics(
    inout TessellationPatch patch,
    float3 barycentric0,
    float3 barycentric1,
    float3 barycentric2)
{
    patch.Barycentric0 = EncodeTessellationBarycentric(barycentric0);
    patch.Barycentric1 = EncodeTessellationBarycentric(barycentric1);
    patch.Barycentric2 = EncodeTessellationBarycentric(barycentric2);
}

float3 DecodeTessellationPatchBarycentric(
    TessellationPatch patch,
    uint vertex)
{
    uint packed = vertex == 0u
        ? patch.Barycentric0
        : vertex == 1u
            ? patch.Barycentric1
            : patch.Barycentric2;
    uint y = packed & 0xFFFFu;
    uint z = packed >> 16u;
    float inverseMaximum = rcp(float(TESS_BARYCENTRIC_MAX));
    return float3(TESS_BARYCENTRIC_MAX - y - z, y, z) * inverseMaximum;
}

uint TessellationPatchDepth(TessellationPatch patch)
{
    return (patch.PackedState >> 12u) & 0xFFu;
}

uint3 TessellationPatchEdgeFactors(TessellationPatch patch)
{
    return max(
        uint3(
            patch.PackedState & 0xFu,
            (patch.PackedState >> 4u) & 0xFu,
            (patch.PackedState >> 8u) & 0xFu),
        1u.xxx);
}

void SetTessellationPatchState(
    inout TessellationPatch patch,
    uint depth,
    uint3 edgeFactors)
{
    edgeFactors = clamp(edgeFactors, 1u.xxx, 15u.xxx);
    patch.PackedState =
        edgeFactors.x |
        (edgeFactors.y << 4u) |
        (edgeFactors.z << 8u) |
        ((depth & 0xFFu) << 12u);
}

uint TessellationNextPowerOfTwo(uint value)
{
    value = max(value, 1u);
    value--;
    value |= value >> 1u;
    value |= value >> 2u;
    value |= value >> 4u;
    value |= value >> 8u;
    value |= value >> 16u;
    return value + 1u;
}

uint TessellationGridVertexCount(uint factor)
{
    return ((factor + 1u) * (factor + 2u)) / 2u;
}

uint TessellationGridTriangleCount(uint factor)
{
    return factor * factor;
}

// Precomputed regular-triangle micro-patterns for factors 1..8. Coordinates use
// four bits per component; triangle vertices use six bits per index.
static const uint TESS_PATTERN_VERTEX_OFFSETS[9] =
{
    0u, 0u, 3u, 9u, 19u, 34u, 55u, 83u, 119u
};
static const uint TESS_PATTERN_VERTEX_COORDINATES[164] =
{
    0u, 1u, 16u, 0u, 1u, 2u, 16u, 17u, 32u, 0u, 1u, 2u,
    3u, 16u, 17u, 18u, 32u, 33u, 48u, 0u, 1u, 2u, 3u, 4u,
    16u, 17u, 18u, 19u, 32u, 33u, 34u, 48u, 49u, 64u, 0u, 1u,
    2u, 3u, 4u, 5u, 16u, 17u, 18u, 19u, 20u, 32u, 33u, 34u,
    35u, 48u, 49u, 50u, 64u, 65u, 80u, 0u, 1u, 2u, 3u, 4u,
    5u, 6u, 16u, 17u, 18u, 19u, 20u, 21u, 32u, 33u, 34u, 35u,
    36u, 48u, 49u, 50u, 51u, 64u, 65u, 66u, 80u, 81u, 96u, 0u,
    1u, 2u, 3u, 4u, 5u, 6u, 7u, 16u, 17u, 18u, 19u, 20u,
    21u, 22u, 32u, 33u, 34u, 35u, 36u, 37u, 48u, 49u, 50u, 51u,
    52u, 64u, 65u, 66u, 67u, 80u, 81u, 82u, 96u, 97u, 112u, 0u,
    1u, 2u, 3u, 4u, 5u, 6u, 7u, 8u, 16u, 17u, 18u, 19u,
    20u, 21u, 22u, 23u, 32u, 33u, 34u, 35u, 36u, 37u, 38u, 48u,
    49u, 50u, 51u, 52u, 53u, 64u, 65u, 66u, 67u, 68u, 80u, 81u,
    82u, 83u, 96u, 97u, 98u, 112u, 113u, 128u
};
static const uint TESS_PATTERN_TRIANGLE_OFFSETS[9] =
{
    0u, 0u, 1u, 5u, 14u, 30u, 55u, 91u, 140u
};
static const uint TESS_PATTERN_TRIANGLES[204] =
{
    8256u, 12352u, 12545u, 16513u, 20739u, 16448u, 16705u, 20609u, 20866u, 24770u, 28996u, 29189u,
    33157u, 37383u, 20544u, 20865u, 24705u, 25026u, 28866u, 29187u, 33027u, 37253u, 37510u, 41414u,
    41671u, 45575u, 49801u, 49994u, 53962u, 58188u, 24640u, 25025u, 28801u, 29186u, 32962u, 33347u,
    37123u, 37508u, 41284u, 45510u, 45831u, 49671u, 49992u, 53832u, 54153u, 57993u, 62219u, 62476u,
    66380u, 66637u, 70541u, 74767u, 74960u, 78928u, 83154u, 28736u, 29185u, 32897u, 33346u, 37058u,
    37507u, 41219u, 41668u, 45380u, 45829u, 49541u, 53767u, 54152u, 57928u, 58313u, 62089u, 62474u,
    66250u, 66635u, 70411u, 74637u, 74958u, 78798u, 79119u, 82959u, 83280u, 87120u, 91346u, 91603u,
    95507u, 95764u, 99668u, 103894u, 104087u, 108055u, 112281u, 32832u, 33345u, 36993u, 37506u, 41154u,
    41667u, 45315u, 45828u, 49476u, 49989u, 53637u, 54150u, 57798u, 62024u, 62473u, 66185u, 66634u,
    70346u, 70795u, 74507u, 74956u, 78668u, 79117u, 82829u, 87055u, 87440u, 91216u, 91601u, 95377u,
    95762u, 99538u, 99923u, 103699u, 107925u, 108246u, 112086u, 112407u, 116247u, 116568u, 120408u, 124634u,
    124891u, 128795u, 129052u, 132956u, 137182u, 137375u, 141343u, 145569u, 36928u, 37505u, 41089u, 41666u,
    45250u, 45827u, 49411u, 49988u, 53572u, 54149u, 57733u, 58310u, 61894u, 62471u, 66055u, 70281u,
    70794u, 74442u, 74955u, 78603u, 79116u, 82764u, 83277u, 86925u, 87438u, 91086u, 91599u, 95247u,
    99473u, 99922u, 103634u, 104083u, 107795u, 108244u, 111956u, 112405u, 116117u, 116566u, 120278u, 124504u,
    124889u, 128665u, 129050u, 132826u, 133211u, 136987u, 137372u, 141148u, 145374u, 145695u, 149535u, 149856u,
    153696u, 154017u, 157857u, 162083u, 162340u, 166244u, 166501u, 170405u, 174631u, 174824u, 178792u, 183018u
};

uint2 TessellationGridVertexCoordinate(uint factor, uint vertexIndex)
{
    uint packed = TESS_PATTERN_VERTEX_COORDINATES[
        TESS_PATTERN_VERTEX_OFFSETS[factor] + vertexIndex];
    return uint2(packed & 0xFu, packed >> 4u);
}

uint TessellationSnapBoundaryCoordinate(uint value, uint factor, uint edgeFactor)
{
    uint divisor = max(edgeFactor, 1u);
    uint step = max(factor / divisor, 1u);
    uint snapped = ((value + step / 2u) / step) * step;
    return min(snapped, factor);
}

float3 TessellationGridBarycentric(
    uint factor,
    uint3 edgeFactors,
    uint vertexIndex)
{
    uint2 coordinate = TessellationGridVertexCoordinate(factor, vertexIndex);
    uint y = coordinate.x;
    uint z = coordinate.y;
    uint x = factor - y - z;

    if (z == 0u)
    {
        y = TessellationSnapBoundaryCoordinate(y, factor, edgeFactors.x);
        x = factor - y;
    }
    if (x == 0u)
    {
        z = TessellationSnapBoundaryCoordinate(z, factor, edgeFactors.y);
        y = factor - z;
    }
    if (y == 0u)
    {
        z = TessellationSnapBoundaryCoordinate(z, factor, edgeFactors.z);
        x = factor - z;
    }

    return float3(x, y, z) * rcp(float(factor));
}

float3 TessellationGridBarycentricCoordinate(
    uint factor,
    uint3 edgeFactors,
    uint y,
    uint z)
{
    uint x = factor - y - z;

    if (z == 0u)
    {
        y = TessellationSnapBoundaryCoordinate(y, factor, edgeFactors.x);
        x = factor - y;
    }
    if (x == 0u)
    {
        z = TessellationSnapBoundaryCoordinate(z, factor, edgeFactors.y);
        y = factor - z;
    }
    if (y == 0u)
    {
        z = TessellationSnapBoundaryCoordinate(z, factor, edgeFactors.z);
        x = factor - z;
    }

    return float3(x, y, z) * rcp(float(factor));
}

uint3 TessellationGridTriangleVertexIndices(uint factor, uint triangleIndex)
{
    uint packed = TESS_PATTERN_TRIANGLES[
        TESS_PATTERN_TRIANGLE_OFFSETS[factor] + triangleIndex];
    return uint3(packed & 0x3Fu, (packed >> 6u) & 0x3Fu, packed >> 12u);
}

// A lane holds at most three vertices (factor one); all other patterns need
// at most two. Shuffle each live register bank before selecting the requested
// one, so a divergent vertex index never reads an inactive source lane.
float4 ReadTessellationDiceVertex(
    uint index, uint lanesPerPatch, uint firstLane, uint registersPerLane,
    float4 vertex0, float4 vertex1, float4 vertex2)
{
    uint lane = firstLane + index % lanesPerPatch;
    float4 result0 = WaveReadLaneAt(vertex0, lane);
    float4 result1 = 0.0.xxxx;
    float4 result2 = 0.0.xxxx;
    if (registersPerLane > 1u)
        result1 = WaveReadLaneAt(vertex1, lane);
    if (registersPerLane > 2u)
        result2 = WaveReadLaneAt(vertex2, lane);
    uint bank = index / lanesPerPatch;
    return bank == 0u ? result0 : bank == 1u ? result1 : result2;
}

#endif
