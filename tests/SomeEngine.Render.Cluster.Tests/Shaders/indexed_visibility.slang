#include "generated/render_instance_layout.slang"
#include "cluster_shade_pipeline.slang"
#include "triangle_surface.slang"
#include "unlit_material.slang"

struct AlphaVarying { float2 UV : TEXCOORD0; };
[VisibilityDepth("masked")]
struct IndexedAlpha : IVisibilityEvaluate<StandardVertex>
{
    typealias Varying = AlphaVarying;
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
        DrawRequest cluster = VisibleClusterRequest(VisibleClusters[input.VisibleClusterIndex]);
        uint clusterOffset = cluster.PageOffset + PageHeap.Load(cluster.PageOffset + 16u) +
            DrawRequestClusterID(cluster) * PACKED_CLUSTER_STRIDE_BYTES;
        uint materialIndex = MaterialSlotOffset(InstanceData, InstanceProperties, cluster.InstanceID) +
            LocalMaterial(PageHeap, clusterOffset, cluster.PageOffset, input.TriangleID);
        UnlitScalars scalars = LoadMaterialScalars<UnlitScalars>(materialIndex);
        VisibilityPixelResult result;
        result.Depth = input.Depth;
        result.Visible = UnlitMaterialTextures[NonUniformResourceIndex(scalars.AlbedoMap)]
            .SampleLevel(MaterialSampler, varying.UV, 0).a >= .5 ? 1u : 0u;
        return result;
    }
};

typedef StaticVertexEval VertexEvaluate;
typedef LinearSurface DomainEvaluate;
typedef IndexedAlpha VisibilityEvaluate;
typedef UnlitMaterial SurfaceEvaluate;
#define CLUSTER_MESHLET_RASTER 1
#define CLUSTER_SOFTWARE_MESHLET_RASTER 1
#include "cluster_material_entries.slang"
