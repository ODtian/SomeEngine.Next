#ifndef COMMON_SLANG
#define COMMON_SLANG

[__AttributeUsage(_AttributeTargets.Function)]
struct VertexEncodingAttribute { string compatibility; };
[__AttributeUsage(_AttributeTargets.Function)]
struct ConservativeBoundsAttribute { string support; };

#ifndef CLUSTER_RASTER_TWO_SIDED
#define CLUSTER_RASTER_TWO_SIDED 0
#endif
#ifndef CLUSTER_TARGET_RASTER_TWO_SIDED
#define CLUSTER_TARGET_RASTER_TWO_SIDED CLUSTER_RASTER_TWO_SIDED
#endif
#if CLUSTER_RASTER_TWO_SIDED
#define CLUSTER_RASTER_FACES "both"
#else
#define CLUSTER_RASTER_FACES "front"
#endif
#if CLUSTER_TARGET_RASTER_TWO_SIDED
#define CLUSTER_TARGET_RASTER_FACES "both"
#else
#define CLUSTER_TARGET_RASTER_FACES "front"
#endif
[__AttributeUsage(_AttributeTargets.Function)]
struct RasterFacesAttribute { string primary; string target; };

#endif
