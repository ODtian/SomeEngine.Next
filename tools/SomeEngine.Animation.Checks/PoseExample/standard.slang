#define CLUSTER_VERTEX_ENCODING "someengine.cluster.positions.uint16.v1+someengine.tests.skin.float.v1"
#define CLUSTER_FORWARD_DEPTH_POSITION 1
#include "generated/render_instance_layout.slang"
#include "cluster_shade_pipeline.slang"
#include "triangle_surface.slang"

float3 MaterialCameraPosition(uint viewIndex)
{
    return Views[viewIndex].CameraPos;
}

#include "standard_material.slang"

#include "skin_pose.slang"
struct PanelSkin : IVertexEvaluate
{
    typedef StandardVertex Vertex;
    Vertex evaluate(VertexEvalContext ctx)
    {
        uint2 footer = ctx.pageHeap.Load2(ctx.attributeBlobAddress + ctx.attributeBlobSize - 8u);
        uint address = ctx.attributeBlobAddress + footer.x;
        uint2 joints = ctx.pageHeap.Load2(address + ctx.localVertexIndex * 16u); float2 weights = asfloat(ctx.pageHeap.Load2(address + ctx.localVertexIndex * 16u + 8u));
        uint2 binding = LoadSkinBinding(ctx.instanceID);
        bool previous = ctx.IsPreviousFrame != 0u || footer.y != 0u;
        float3 position = EvalObjectPosition(ctx);
        float3 local = mul(float4(position, 1), LoadSkinTransform(binding, joints.x, previous)).xyz * weights.x
            + mul(float4(position, 1), LoadSkinTransform(binding, joints.y, previous)).xyz * weights.y;
        StaticVertexEval source;
        Vertex result = source.evaluate(ctx);
        result.position = TransformInstanceLocalPosition(local, ctx.transform);
        result.basePosition = result.position;
        return result;
    }
    float3 getPosition(Vertex value) { return value.position; }
    Vertex readLane(Vertex value, uint lane) { StaticVertexEval source; return source.readLane(value, lane); }
};
typedef PanelSkin VertexEvaluate;
typedef LinearSurface DomainEvaluate;
typedef OpaqueVisibilityEvaluate<VertexEvaluate.Vertex> VisibilityEvaluate;
typedef StandardMaterial SurfaceEvaluate;

#define CLUSTER_MESHLET_RASTER 1
#define CLUSTER_SURFACE_RECEIVERS 1
#define CLUSTER_SOFTWARE_MESHLET_RASTER 1
#define CLUSTER_SURFACE_VISIBILITY_DEPTH 1
#define CLUSTER_SURFACE_MODES 1
#define CLUSTER_FORWARD_SHADING 1
#include "cluster_material_entries.slang"

#define TRANSPARENCY_MATERIAL_TYPE StandardTransparencyMaterial
#include "cluster_transparency_material.slang"
