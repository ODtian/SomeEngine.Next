#include "triangle_surface.slang"
#include "unlit_material.slang"

struct GroundVertexEvaluate : IVertexEvaluate
{
    typedef StandardVertex Vertex;
    Vertex evaluate(VertexEvalContext ctx)
    {
        uint address = ctx.vertexBlobAddress + ctx.localVertexIndex * 32u;
        Vertex value;
        value.position = TransformInstanceLocalPosition(asfloat(ctx.pageHeap.Load3(address)), ctx.transform);
        value.basePosition = value.position;
        value.worldNormal = RotateByTransform(asfloat(ctx.pageHeap.Load3(address + 12u)), ctx.transform);
        value.worldTangent = float4(RotateByTransform(float3(1, 0, 0), ctx.transform), 1);
        value.uv0 = asfloat(ctx.pageHeap.Load2(address + 24u));
        return value;
    }
    float3 getPosition(Vertex value) { return value.position; }
    Vertex readLane(Vertex value, uint lane)
    {
        StaticVertexEval helper;
        return helper.readLane(value, lane);
    }
};

typedef GroundVertexEvaluate VertexEvaluate;
typedef LinearSurface DomainEvaluate;
typedef OpaqueVisibilityEvaluate<VertexEvaluate.Vertex> VisibilityEvaluate;
typedef UnlitMaterial SurfaceEvaluate;
#define CLUSTER_VERTEX_ENCODING "example.terrain.position_normal_uv.v1"
#include "Pipeline/cluster_material.slang"
