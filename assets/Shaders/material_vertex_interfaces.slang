#ifndef MATERIAL_VERTEX_INTERFACES_SLANG
#define MATERIAL_VERTEX_INTERFACES_SLANG

// Pipeline-neutral vertex capability required by surface materials. A render
// pipeline may provide a richer evaluator while preserving this material ABI.
interface IMaterialVertex
{
    associatedtype DeformedVertex;
};

#endif
