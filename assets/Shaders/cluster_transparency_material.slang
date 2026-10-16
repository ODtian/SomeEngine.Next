#ifndef CLUSTER_TRANSPARENCY_MATERIAL_SLANG
#define CLUSTER_TRANSPARENCY_MATERIAL_SLANG

[__AttributeUsage(_AttributeTargets.Struct)]
struct TransparencyMaterialAttribute {};

// User vertex and surface types stay in their source unit. The composition
// interface uses builtin values so independently cooked material units can link.
#define TRANSPARENCY_EVALUATOR_NAME(name) name##Evaluator
#define TRANSPARENCY_EVALUATOR(name) TRANSPARENCY_EVALUATOR_NAME(name)

struct TRANSPARENCY_EVALUATOR(TRANSPARENCY_MATERIAL_TYPE)
{
    VertexEvaluate Vertex;
    DomainEvaluate Domain;
    VisibilityEvaluate Visibility;
    SurfaceEvaluate Surface;

    TriangleInfo triangleInfo(uint visibility, uint viewIndex)
    {
        TriangleInfo info;
        LoadTriangleInfo(visibility, viewIndex, info);
        info.visibility = visibility;
        info.sourceLane = WaveGetLaneIndex();
        return info;
    }

    bool project(uint visibility, uint viewIndex, out float4 first, out float4 second, out float4 third)
    {
        TriangleInfo info = triangleInfo(visibility, viewIndex);
        DomainEvaluate.TriangleData triangle = evaluateTriangle<VertexEvaluate, DomainEvaluate>(Vertex, Domain, info);
        ClusterViewUniforms view = ApplyFirstPersonViewTransform(InstanceData, InstanceProperties, info.instanceID, Views[viewIndex]);
        first = mul(float4(ClusterPositionForView(Domain.cornerPosition(triangle, 0u), view), 1.0), view.ViewProj);
        second = mul(float4(ClusterPositionForView(Domain.cornerPosition(triangle, 1u), view), 1.0), view.ViewProj);
        third = mul(float4(ClusterPositionForView(Domain.cornerPosition(triangle, 2u), view), 1.0), view.ViewProj);
        return true;
    }

    bool cover(uint visibility, uint3 coord, float depth, out float resultDepth)
    {
        TriangleInfo info = triangleInfo(visibility, coord.z);
        RenderInstanceTransform transform = GetCurrentInstanceTransform(info.instanceID);
        VertexEvaluate.Vertex first = Vertex.evaluate(makeVertexEvalContext(info, transform, 0u, info.vi0));
        VertexEvaluate.Vertex second = Vertex.evaluate(makeVertexEvalContext(info, transform, 0u, info.vi1));
        VertexEvaluate.Vertex third = Vertex.evaluate(makeVertexEvalContext(info, transform, 0u, info.vi2));
        DomainEvaluate.TriangleData triangle = Domain.makeTriangle(first, second, third);
        ClusterViewUniforms view = ApplyFirstPersonViewTransform(InstanceData, InstanceProperties, info.instanceID, Views[coord.z]);
        PipelineTriangleProjection projection = ProjectTriangleWave<VertexEvaluate.Vertex, DomainEvaluate>(Domain, view, triangle, info);
        float2 center = float2(coord.xy) + .5;
        float3 bary = TriangleBarycentricAt(projection, center);
        VisibilityInterpolationInput interpolation;
        interpolation.PerspectiveBarycentric = bary;
        interpolation.PerspectiveBarycentricDx = TriangleBarycentricAt(projection, center + float2(1, 0)) - bary;
        interpolation.PerspectiveBarycentricDy = TriangleBarycentricAt(projection, center + float2(0, 1)) - bary;
        float3 linear = bary / projection.InverseW;
        interpolation.LinearBarycentric = linear / (linear.x + linear.y + linear.z);
        interpolation.LinearBarycentricDx = (TriangleBarycentricAt(projection, center + float2(1, 0)) / projection.InverseW);
        interpolation.LinearBarycentricDx /= dot(interpolation.LinearBarycentricDx, 1.0.xxx);
        interpolation.LinearBarycentricDx -= interpolation.LinearBarycentric;
        interpolation.LinearBarycentricDy = (TriangleBarycentricAt(projection, center + float2(0, 1)) / projection.InverseW);
        interpolation.LinearBarycentricDy /= dot(interpolation.LinearBarycentricDy, 1.0.xxx);
        interpolation.LinearBarycentricDy -= interpolation.LinearBarycentric;
        VisibilityVertexInput vertexInput = { info.visibleClusterIndex, info.instanceID, coord.z, info.vi0 };
        VisibilityEvaluate.Varying v0 = Visibility.makeVarying(first, vertexInput);
        vertexInput.LocalVertexIndex = info.vi1;
        VisibilityEvaluate.Varying v1 = Visibility.makeVarying(second, vertexInput);
        vertexInput.LocalVertexIndex = info.vi2;
        VisibilityEvaluate.Varying v2 = Visibility.makeVarying(third, vertexInput);
        VisibilityPixelInput pixel = (VisibilityPixelInput)0;
        pixel.PixelCoord = coord;
        pixel.Depth = depth;
        pixel.EncodedDepth = asuint(1.0 - depth);
        pixel.VisibleClusterIndex = info.visibleClusterIndex;
        pixel.TriangleID = info.triangleID;
        pixel.ViewIndex = coord.z;
        VisibilityPixelResult result = Visibility.evaluate(pixel, Visibility.interpolatePixel(v0, v1, v2, interpolation));
        resultDepth = result.Depth;
        return result.Visible != 0u;
    }

    void reconstruct(uint visibility, uint3 coord, float depth, uint visibilityDepth, out TriangleInfo info,
        out DomainEvaluate.TriangleData triangle, out float3 bary, out DomainEvaluate.Surface evaluated, out SurfacePixel pixel)
    {
        info = triangleInfo(visibility, coord.z);
        ClusterViewUniforms view = ApplyFirstPersonViewTransform(InstanceData, InstanceProperties, info.instanceID, Views[coord.z]);
        triangle = evaluateTriangle<VertexEvaluate, DomainEvaluate>(Vertex, Domain, info);
        float3 position;
        SurfaceBarycentrics coordinates = ResolveBarycentricsAtDepth<VertexEvaluate.Vertex, DomainEvaluate>(Domain, coord, view, triangle, info, depth, position);
        bary = coordinates.Value;
        evaluated = Domain.eval(triangle, bary);
        pixel.PixelCoord = coord;
        pixel.InstanceID = info.instanceID;
        pixel.MaterialIndex = info.materialSlot;
        pixel.DebugMode = Uniforms.DebugMode;
        pixel.Depth = depth;
        pixel.VisibilityDepth = visibilityDepth;
        pixel.PositionFromDepth = position;
        pixel.BarycentricDx = coordinates.Dx;
        pixel.BarycentricDy = coordinates.Dy;
    }

    float4 shade<let visibilityDepth : uint>(uint visibility, uint3 coord, float depth)
    {
        TriangleInfo info;
        DomainEvaluate.TriangleData triangle;
        float3 bary;
        DomainEvaluate.Surface evaluated;
        SurfacePixel pixel;
        reconstruct(visibility, coord, depth, visibilityDepth, info, triangle, bary, evaluated, pixel);
        SurfaceReceiver receiver;
        SurfaceEvaluate.Data data = Surface.evaluateSurface(pixel, evaluated, bary, triangle, receiver);
        receiver = TransformMaterialReceiver(receiver, info, true);
        return Surface.shadeForwardSurface(coord, Uniforms.DebugMode, data, receiver);
    }

    void receiver<let visibilityDepth : uint>(uint visibility, uint3 coord, float depth,
        out float3 position, out float3 normal, out uint flags)
    {
        TriangleInfo info;
        DomainEvaluate.TriangleData triangle;
        float3 bary;
        DomainEvaluate.Surface evaluated;
        SurfacePixel pixel;
        reconstruct(visibility, coord, depth, visibilityDepth, info, triangle, bary, evaluated, pixel);
        SurfaceReceiver receiver = Surface.evaluateShadowReceiver(pixel, evaluated, bary, triangle);
        receiver = TransformMaterialReceiver(receiver, info, false);
        position = receiver.Position;
        normal = receiver.GeometricNormal;
        flags = receiver.Flags;
    }
};
#endif

#ifdef TRANSPARENCY_MATERIAL_TYPE
[TransparencyMaterial]
struct TRANSPARENCY_MATERIAL_TYPE
{
    TRANSPARENCY_EVALUATOR(TRANSPARENCY_MATERIAL_TYPE) Material;
    bool project(uint visibility, uint view, out float4 first, out float4 second, out float4 third)
    { return Material.project(visibility, view, first, second, third); }
    bool cover(uint visibility, uint3 pixel, float depth, out float resultDepth)
    { return Material.cover(visibility, pixel, depth, resultDepth); }
    float4 shade<let visibilityDepth : uint>(uint visibility, uint3 pixel, float depth)
    { return Material.shade<visibilityDepth>(visibility, pixel, depth); }
    void receiver<let visibilityDepth : uint>(uint visibility, uint3 pixel, float depth,
        out float3 position, out float3 normal, out uint flags)
    { Material.receiver<visibilityDepth>(visibility, pixel, depth, position, normal, flags); }
};
#undef TRANSPARENCY_MATERIAL_TYPE
#endif
