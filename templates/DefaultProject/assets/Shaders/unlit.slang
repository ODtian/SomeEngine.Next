#include "generated/render_instance_layout.slang"
#include "cluster_shade_pipeline.slang"
#include "unlit_material.slang"

typedef StaticVertexEval VertexEvaluate;
typedef LinearSurface DomainEvaluate;
typedef OpaquePixelEvaluate PixelEvaluate;
typedef UnlitMaterial SurfaceEvaluate;

#include "cluster_material_entries.slang"
