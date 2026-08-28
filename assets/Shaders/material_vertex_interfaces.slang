#ifndef MATERIAL_VERTEX_INTERFACES_SLANG
#define MATERIAL_VERTEX_INTERFACES_SLANG

// Pipeline-neutral vertex capability required by surface materials. A render
// pipeline may provide a richer evaluator while preserving this material ABI.
interface IMaterialVertex
{
    associatedtype DeformedVertex;
};

// Pipeline-neutral surface access. Materials depend on these operations rather than a concrete
// deformed-vertex layout; imported glTF/PBR data is only one possible user implementation.
interface ISurfaceMaterialVertex : IMaterialVertex
{
    float3 getPosition(DeformedVertex value);
    float3 getNormal(DeformedVertex value);
    float2 getTexCoord0(DeformedVertex value);
};

interface ITangentSurfaceMaterialVertex : ISurfaceMaterialVertex
{
    float4 getTangent(DeformedVertex value);
};

#endif
