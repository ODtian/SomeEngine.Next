#include "triangles.slang"
#define TILE_PIXEL_SIZE 16
#define TILE_PIXEL_COUNT 256
#define TILE_MASK_WORDS 8

struct TileSettings
{
    uint Width;
    uint Height;
    float UnusedBias;
    float UnusedOverestimation;
    uint TileColumns;
    uint TileRows;
    uint AssociationCount;
    float ColorError;
};
ConstantBuffer<TileSettings> Settings;

struct TileTriangleData
{
    float3 DepthPlane;
    uint TriangleId;
    uint ColorIndex;
    uint FirstAssociation;
    uint MinimumTile;
    uint TileWidth;
};

StructuredBuffer<TileTriangleData> TileTriangles;
StructuredBuffer<uint> TileAssociations;
StructuredBuffer<uint2> TileRanges;
StructuredBuffer<float4> Colors;
#ifdef TILE_SOFTWARE_RASTER
StructuredBuffer<ProjectedTriangle> Coverage;
Texture2DArray<float> OpaqueDepth;
#endif
#ifndef TILE_SOFTWARE_RASTER
RWStructuredBuffer<uint> CoverageMasks;
#endif

float2 PixelPoint(uint2 pixel)
{
    return float2((pixel.x + .5) * (2.0 / Settings.Width) - 1,
        1 - (pixel.y + .5) * (2.0 / Settings.Height));
}

uint CoverageAssociation(TileTriangleData triangle, uint2 tile)
{
    uint2 minimum = uint2(triangle.MinimumTile & 65535, triangle.MinimumTile >> 16);
    return triangle.FirstAssociation + (tile.y - minimum.y) * triangle.TileWidth + tile.x - minimum.x;
}
