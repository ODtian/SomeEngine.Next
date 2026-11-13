import material_interfaces;
import triangle_surface;
import standard_material;

[VertexEncoding("someengine.cluster.positions.uint16.v1+someengine.cluster.attributes.standard.v1")]
[ConservativeBounds("finite")]
struct DefaultStandardMaterial : IMaterial
{
    typedef StaticVertexEval Vertex;
    typedef LinearSurface Domain;
    typedef OpaqueVisibilityEvaluate<Vertex.Vertex> Visibility;

    StandardMaterial Surface;

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
