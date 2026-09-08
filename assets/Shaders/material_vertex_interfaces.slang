#ifndef MATERIAL_VERTEX_INTERFACES_SLANG
#define MATERIAL_VERTEX_INTERFACES_SLANG

// User-facing access to an opaque evaluated vertex. Pipelines operate on Vertex only
// through this interface; the concrete vertex layout remains owned by the material shader.
interface IVertexAccess
{
    associatedtype Vertex;

    float3 getPosition(Vertex value);
    void setPosition(inout Vertex value, float3 position);

    float3 getNormal(Vertex value);
    void setNormal(inout Vertex value, float3 normal);

    float4 getTangent(Vertex value);
    void setTangent(inout Vertex value, float4 tangent);

    float2 getTexCoord0(Vertex value);
    void setTexCoord0(inout Vertex value, float2 texCoord);
};

#endif
