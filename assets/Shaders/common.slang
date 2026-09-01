#ifndef COMMON_SLANG
#define COMMON_SLANG

[__AttributeUsage(_AttributeTargets.Function)]
struct MaterialTargetAttribute { string target; };
struct VertexEncodingAttribute { string compatibility; };
struct ConservativeBoundsAttribute { string support; };

#endif
