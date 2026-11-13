__exported import triangle_surface;
__exported import unlit_material;

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

[VertexEncoding("example.terrain.position_normal_uv.v1")]
[ConservativeBounds("finite")]
struct GpuTerrainMaterial : IMaterial
{
    typedef GroundVertexEvaluate Vertex;
    typedef LinearSurface Domain;
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
