#ifndef DEPTH_PROJECTION_SLANG
#define DEPTH_PROJECTION_SLANG

// Adapt the engine's forward-depth matrix before projection, rather than
// subtracting an already rounded normalized depth from one. VSM raster and
// receiver comparisons then use the native reverse-Z representation.
float ProjectReverseDepth(float4 position, float4x4 projection)
{
    float4 depthColumn = float4(
        projection[0][3] - projection[0][2],
        projection[1][3] - projection[1][2],
        projection[2][3] - projection[2][2],
        projection[3][3] - projection[3][2]);
    return dot(position, depthColumn);
}

#endif
