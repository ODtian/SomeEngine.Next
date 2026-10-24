#ifndef COMMON_SLANG
#define COMMON_SLANG

[__AttributeUsage(_AttributeTargets.Function)]
struct VertexEncodingAttribute { string compatibility; };
[__AttributeUsage(_AttributeTargets.Function)]
struct ConservativeBoundsAttribute { string support; };
[__AttributeUsage(_AttributeTargets.Function)]
struct ShaderSpecializationAttribute { string arguments; };

// Complete material-property dependencies of a vertex, domain, or visibility evaluator.
// An empty list declares no material inputs. Missing/unknown declarations remain conservative.
[__AttributeUsage(_AttributeTargets.Struct)]
struct MaterialDepthInputsAttribute { string properties; };
// Selected raster entries with this declaration read material data only through the
// VertexEvaluate, DomainEvaluate and VisibilityEvaluate implementations.
[__AttributeUsage(_AttributeTargets.Function)]
struct RasterMaterialInputsAttribute { string source; };

[__AttributeUsage(_AttributeTargets.Function)]
struct RasterFacesAttribute { string primary; string target; };

#endif

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
