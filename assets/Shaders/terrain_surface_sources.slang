#ifndef TERRAIN_SURFACE_SOURCES
#define TERRAIN_SURFACE_SOURCES

#include "terrain_surface_input.slang"

struct TerrainScalarSource
{
    int Kind;
    int PageSize;
    int Width;
    int Depth;
    int SourcePageX;
    int SourcePageY;
    int SourcePageZ;
    int SourcePageWidth;
    int SourcePageHeight;
    float Spacing;
    float InitialValue;
    int SamplesStart;
    int PageOffsetsStart;
    int3 Padding;
};

StructuredBuffer<TerrainScalarSource> TerrainSources;
StructuredBuffer<float> TerrainSourceSamples;
StructuredBuffer<int> TerrainSourcePageOffsets;

#endif
