#define CLUSTER_VISIBILITY_IDENTITY_TIES 1
#define CLUSTER_FORWARD_DEPTH_POSITION 1
#include "generated/render_instance_layout.slang"
#include "cluster_shade_pipeline.slang"
#include "triangle_surface.slang"

float3 MaterialCameraPosition(uint viewIndex)
{
    return Views[viewIndex].CameraPos;
}

#include "scene_material.slang"

typedef StaticVertexEval VertexEvaluate;
typedef LinearSurface DomainEvaluate;
struct SceneMaskVarying { float2 UV : TEXCOORD0; };
[VisibilityDepth("masked")]
struct SceneMask : IVisibilityEvaluate<StandardVertex>
{
    typealias Varying = SceneMaskVarying;
    SamplerState MaterialSampler;
    Varying makeVarying(StandardVertex vertex, VisibilityVertexInput input)
    { Varying result; result.UV = vertex.uv0; return result; }
    Varying readLane(Varying value, uint lane)
    { Varying result; result.UV = WaveReadLaneAt(value.UV, lane); return result; }
    Varying interpolateVertex(Varying a, Varying b, Varying c, float3 bary)
    { Varying result; result.UV = a.UV * bary.x + b.UV * bary.y + c.UV * bary.z; return result; }
    Varying interpolatePixel(Varying a, Varying b, Varying c, VisibilityInterpolationInput input)
    { return interpolateVertex(a, b, c, input.PerspectiveBarycentric); }
    VisibilityPixelResult evaluate(VisibilityPixelInput input, Varying varying)
    {
        VisibilityPixelResult result;
        result.Depth = input.Depth;
        if (input.TargetPolicy != 0u && input.EncodedTargetDepth >= input.EncodedDepth)
        {
            result.Visible = 0u;
            return result;
        }
        DrawRequest cluster = VisibleClusterRequest(VisibleClusters[input.VisibleClusterIndex]);
        uint clusterOffset = cluster.PageOffset + PageHeap.Load(cluster.PageOffset + 16u) +
            DrawRequestClusterID(cluster) * PACKED_CLUSTER_STRIDE_BYTES;
        uint materialIndex = MaterialSlotOffset(InstanceData, InstanceProperties, cluster.InstanceID) +
            LocalMaterial(PageHeap, clusterOffset, cluster.PageOffset, input.TriangleID);
        StandardScalars scalars = LoadMaterialScalars<StandardScalars>(materialIndex);
        result.Visible = StandardMaterialTextures[NonUniformResourceIndex(scalars.AlbedoMap)]
            .SampleLevel(MaterialSampler, varying.UV, 0.0).a >= 0.9 ? 1u : 0u;
        return result;
    }
};
typedef SceneMask VisibilityEvaluate;
typedef StandardMaterial SurfaceEvaluate;

#define CLUSTER_MESHLET_RASTER 1
#define CLUSTER_SURFACE_RECEIVERS 1
#define CLUSTER_SOFTWARE_MESHLET_RASTER 1
#define CLUSTER_SURFACE_VISIBILITY_DEPTH 1
#define CLUSTER_SURFACE_MODES 1
#define CLUSTER_FORWARD_SHADING 1
#include "cluster_material_entries.slang"
