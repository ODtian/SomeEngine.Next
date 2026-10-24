#define TILE_HARDWARE_RASTER 1
#include "tile_visibility_common.slang"

struct ForwardTriangle
{
    float4 A;
    float4 B;
    float4 C;
    uint TriangleIndex;
    uint ColorIndex;
};
StructuredBuffer<ForwardTriangle> Vertices;

struct VertexOutput
{
    float4 Position : SV_Position;
    nointerpolation uint TriangleIndex : TEXCOORD0;
};

[shader("vertex")]
VertexOutput DrawTransparency(uint vertexId : SV_VertexID)
{
    ForwardTriangle triangle = Vertices[vertexId / 3];
    VertexOutput result;
    result.Position = vertexId % 3 == 0 ? triangle.A : vertexId % 3 == 1 ? triangle.B : triangle.C;
    result.Position.z = result.Position.w - result.Position.z;
    result.TriangleIndex = triangle.TriangleIndex;
    return result;
}

[shader("fragment")]
[earlydepthstencil]
void StoreTileCoverage(VertexOutput input)
{
    uint2 pixel = uint2(input.Position.xy);
    TileTriangleData triangle = TileTriangles[input.TriangleIndex];
    uint association = CoverageAssociation(triangle, pixel / TILE_PIXEL_SIZE);
    uint bit = (pixel.y & 15) * 16 + (pixel.x & 15);
    uint word = association * TILE_MASK_WORDS + bit / 32;
    uint contribution = 1u << (bit & 31);
    uint4 peers = WaveMatch(word);
    uint combined = WaveMultiPrefixBitOr(contribution, peers) | contribution;
    uint last = peers.w != 0 ? 96 + firstbithigh(peers.w) : peers.z != 0 ? 64 + firstbithigh(peers.z) :
        peers.y != 0 ? 32 + firstbithigh(peers.y) : firstbithigh(peers.x);
    if (WaveGetLaneIndex() == last)
    {
        uint ignored;
        InterlockedOr(CoverageMasks[word], combined, ignored);
    }
}
