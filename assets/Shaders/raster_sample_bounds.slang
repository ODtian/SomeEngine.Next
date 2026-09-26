#ifndef RASTER_SAMPLE_BOUNDS_SLANG
#define RASTER_SAMPLE_BOUNDS_SLANG

// Conservative coverage for a single sample at each pixel center. The margin
// includes four-bit raster snapping and floating-point projection roundoff.
bool RasterBoundsCoverPixelCenters(float2 minUv, float2 maxUv, uint2 screenSize)
{
    float2 screen = float2(screenSize);
    float2 tolerance = 1.0 / 16.0 + screen * 1e-6;
    float2 firstSample = ceil(minUv * screen - 0.5 - tolerance);
    float2 lastSample = floor(maxUv * screen - 0.5 + tolerance);
    return !any(firstSample > lastSample);
}

#endif
