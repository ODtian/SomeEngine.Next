#ifndef COMMON_SLANG
#define COMMON_SLANG

[__AttributeUsage(_AttributeTargets.Function)]
struct VertexEncodingAttribute { string compatibility; };
[__AttributeUsage(_AttributeTargets.Function)]
struct ConservativeBoundsAttribute { string support; };

#endif
