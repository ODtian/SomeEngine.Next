import material_interfaces;
import triangle_surface;
import phong_tessellation;
import unlit_material;

// Wave moves each vertex by at most .3 world units. Phong's edge control point
// moves at most half the undeformed world edge beyond its source triangle.
// Callers supply that sum as MeshInstance.BoundsExpansion (.66 for gallery parts).
[VertexEncoding("someengine.cluster.positions.uint16.v1+someengine.cluster.attributes.standard.v1")]
[ConservativeBounds("finite")]
struct DeformedPhongMaterial : IMaterial
{
    typedef WaveVertexEval Vertex;
    typedef PhongTessellation Domain;
    typedef OpaqueVisibilityEvaluate<Vertex.Vertex> Visibility;

    UnlitMaterial Surface;

    void evaluateSurface<TOutput : ISurfaceOutput>(SurfacePixelInput input,
        Domain.Surface surface, float3 bary, Domain.TriangleData triangle, TOutput output)
    {
        Surface.evaluateSurface<TOutput>(input, surface, bary, triangle, output);
    }

    void evaluateDebug<TOutput : ISurfaceOutput>(uint debugMode, SurfacePixelInput input,
        Domain.Surface surface, float3 bary, Domain.TriangleData triangle, TOutput output)
    {
        Surface.evaluateDebug<TOutput>(debugMode, input, surface, bary, triangle, output);
    }
};
