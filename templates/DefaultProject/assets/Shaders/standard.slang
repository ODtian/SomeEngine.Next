#include "generated/render_instance_layout.slang"
#include "cluster_shade_pipeline.slang"

float3 MaterialCameraPosition(uint viewIndex)
{
    return Views[viewIndex].CameraPos;
}

#include "standard_material.slang"

typedef StaticVertexEval VertexEvaluate;
typedef LinearSurface DomainEvaluate;
typedef OpaquePixelEvaluate PixelEvaluate;
typedef StandardMaterial SurfaceEvaluate;

#include "cluster_material_entries.slang"
