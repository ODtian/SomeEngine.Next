#ifndef CLUSTER_TRANSPARENCY_MATERIAL_SLANG
#define CLUSTER_TRANSPARENCY_MATERIAL_SLANG

#include "cluster_transparency_source.slang"
#include "cluster_tessellation_geometry.slang"

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

    TriangleInfo triangleInfo(uint source, uint viewIndex)
    {
        TriangleInfo info;
        LoadTriangleInfo(TransparencySourceRequest(source), TransparencySourceTriangle(source), info);
        info.visibleClusterIndex = source >> 7u;
        info.visibility = source;
        info.sourceLane = WaveGetLaneIndex();
        return info;
    }

    uint primitiveCount(uint source, uint viewIndex, bool tessellated)
    {
        if (!tessellated) return 1u;
        TriangleInfo info = triangleInfo(source, viewIndex);
        DomainEvaluate.TriangleData triangle = evaluateTriangle<VertexEvaluate, DomainEvaluate>(Vertex, Domain, info);
        ClusterViewUniforms view = ApplyFirstPersonViewTransform(InstanceData, InstanceProperties, info.instanceID, Views[viewIndex]);
        TessellationTraversal traversal = BeginTessellationTraversal();
        TessellationPatch patch;
        uint count = 0u;
        while (NextTessellationPatch<VertexEvaluate.Vertex, DomainEvaluate>(Domain, triangle, view,
            TransparencyGeometryUniforms.MaxFactor, TransparencyGeometryUniforms.TargetEdgePixels, traversal, patch))
        {
            uint3 factors = TessellationPatchEdgeFactors(patch);
            count += TessellationGridTriangleCount(max(factors.x, max(factors.y, factors.z)));
        }
        return count;
    }

    bool primitiveBarycentrics(DomainEvaluate.TriangleData triangle, ClusterViewUniforms view,
        uint primitive, bool tessellated, out float3 first, out float3 second, out float3 third)
    {
        first = float3(1, 0, 0);
        second = float3(0, 1, 0);
        third = float3(0, 0, 1);
        if (!tessellated) return primitive == 0u;
        TessellationTraversal traversal = BeginTessellationTraversal();
        TessellationPatch patch;
        while (NextTessellationPatch<VertexEvaluate.Vertex, DomainEvaluate>(Domain, triangle, view,
            TransparencyGeometryUniforms.MaxFactor, TransparencyGeometryUniforms.TargetEdgePixels, traversal, patch))
        {
            uint3 factors = TessellationPatchEdgeFactors(patch);
            uint factor = max(factors.x, max(factors.y, factors.z));
            uint count = TessellationGridTriangleCount(factor);
            if (primitive >= count) { primitive -= count; continue; }
            uint3 vertices = TessellationGridTriangleVertexIndices(factor, primitive);
            float3 bary0 = DecodeTessellationPatchBarycentric(patch, 0u);
            float3 bary1 = DecodeTessellationPatchBarycentric(patch, 1u);
            float3 bary2 = DecodeTessellationPatchBarycentric(patch, 2u);
            first = TransformPatchBarycentric(bary0, bary1, bary2, TessellationGridBarycentric(factor, factors, vertices.x));
            second = TransformPatchBarycentric(bary0, bary1, bary2, TessellationGridBarycentric(factor, factors, vertices.y));
            third = TransformPatchBarycentric(bary0, bary1, bary2, TessellationGridBarycentric(factor, factors, vertices.z));
            return true;
        }
        return false;
    }

    float4 projectPosition(float3 position, ClusterViewUniforms view)
    {
        return mul(float4(ClusterPositionForView(position, view), 1.0), view.ViewProj);
    }

    bool project(uint source, uint viewIndex, uint primitive, bool tessellated,
        out float4 first, out float4 second, out float4 third)
    {
        TriangleInfo info = triangleInfo(source, viewIndex);
        DomainEvaluate.TriangleData triangle = evaluateTriangle<VertexEvaluate, DomainEvaluate>(Vertex, Domain, info);
        ClusterViewUniforms view = ApplyFirstPersonViewTransform(InstanceData, InstanceProperties, info.instanceID, Views[viewIndex]);
        float3 bary0, bary1, bary2;
        bool valid = primitiveBarycentrics(triangle, view, primitive, tessellated, bary0, bary1, bary2);
        first = projectPosition(tessellated ? Domain.position(triangle, bary0) : Domain.cornerPosition(triangle, 0u), view);
        second = projectPosition(tessellated ? Domain.position(triangle, bary1) : Domain.cornerPosition(triangle, 1u), view);
        third = projectPosition(tessellated ? Domain.position(triangle, bary2) : Domain.cornerPosition(triangle, 2u), view);
        return valid;
    }

    bool cover(uint source, uint primitive, bool tessellated, uint3 coord, float depth, out float resultDepth)
    {
        TriangleInfo info = triangleInfo(source, coord.z);
        RenderInstanceTransform transform = GetCurrentInstanceTransform(info.instanceID);
        VertexEvaluate.Vertex first = Vertex.evaluate(makeVertexEvalContext(info, transform, 0u, info.vi0));
        VertexEvaluate.Vertex second = Vertex.evaluate(makeVertexEvalContext(info, transform, 0u, info.vi1));
        VertexEvaluate.Vertex third = Vertex.evaluate(makeVertexEvalContext(info, transform, 0u, info.vi2));
        DomainEvaluate.TriangleData triangle = Domain.makeTriangle(first, second, third);
        ClusterViewUniforms view = ApplyFirstPersonViewTransform(InstanceData, InstanceProperties, info.instanceID, Views[coord.z]);
        float3 bary0, bary1, bary2;
        resultDepth = depth;
        if (!primitiveBarycentrics(triangle, view, primitive, tessellated, bary0, bary1, bary2)) return false;
        float4 clip0 = projectPosition(tessellated ? Domain.position(triangle, bary0) : Domain.cornerPosition(triangle, 0u), view);
        float4 clip1 = projectPosition(tessellated ? Domain.position(triangle, bary1) : Domain.cornerPosition(triangle, 1u), view);
        float4 clip2 = projectPosition(tessellated ? Domain.position(triangle, bary2) : Domain.cornerPosition(triangle, 2u), view);
        float2 screen = float2(Uniforms.ScreenWidth, Uniforms.ScreenHeight);
        float2 point0 = (clip0.xy / clip0.w * float2(.5, -.5) + .5) * screen;
        float2 point1 = (clip1.xy / clip1.w * float2(.5, -.5) + .5) * screen;
        float2 point2 = (clip2.xy / clip2.w * float2(.5, -.5) + .5) * screen;
        PipelineTriangleProjection projection;
        projection.Origin = point0;
        projection.FirstEdge = point1 - point0;
        projection.SecondEdge = point2 - point0;
        projection.InverseArea = 1.0 / (projection.FirstEdge.x * projection.SecondEdge.y - projection.FirstEdge.y * projection.SecondEdge.x);
        projection.InverseW = 1.0 / float3(clip0.w, clip1.w, clip2.w);
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
        VisibilityEvaluate.Varying source0 = Visibility.makeVarying(first, vertexInput);
        vertexInput.LocalVertexIndex = info.vi1;
        VisibilityEvaluate.Varying source1 = Visibility.makeVarying(second, vertexInput);
        vertexInput.LocalVertexIndex = info.vi2;
        VisibilityEvaluate.Varying source2 = Visibility.makeVarying(third, vertexInput);
        VisibilityEvaluate.Varying v0 = Visibility.interpolateVertex(source0, source1, source2, bary0);
        VisibilityEvaluate.Varying v1 = Visibility.interpolateVertex(source0, source1, source2, bary1);
        VisibilityEvaluate.Varying v2 = Visibility.interpolateVertex(source0, source1, source2, bary2);
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

    void reconstruct(uint source, uint3 coord, float depth, uint visibilityDepth, out TriangleInfo info,
        out DomainEvaluate.TriangleData triangle, out float3 bary, out DomainEvaluate.Surface evaluated, out SurfacePixel pixel)
    {
        info = triangleInfo(source, coord.z);
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

    float4 shade<let visibilityDepth : uint>(uint source, uint primitive, uint3 coord, float depth)
    {
        TriangleInfo info;
        DomainEvaluate.TriangleData triangle;
        float3 bary;
        DomainEvaluate.Surface evaluated;
        SurfacePixel pixel;
        reconstruct(source, coord, depth, visibilityDepth, info, triangle, bary, evaluated, pixel);
        SurfaceReceiver receiver;
        SurfaceEvaluate.Data data = Surface.evaluateSurface(pixel, evaluated, bary, triangle, receiver);
        receiver = TransformMaterialReceiver(receiver, info, true);
        return Surface.shadeForwardSurface(coord, Uniforms.DebugMode, data, receiver);
    }

    void receiver<let visibilityDepth : uint>(uint source, uint primitive, uint3 coord, float depth,
        out float3 position, out float3 normal, out uint flags)
    {
        TriangleInfo info;
        DomainEvaluate.TriangleData triangle;
        float3 bary;
        DomainEvaluate.Surface evaluated;
        SurfacePixel pixel;
        reconstruct(source, coord, depth, visibilityDepth, info, triangle, bary, evaluated, pixel);
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
    uint primitiveCount(uint source, uint view, bool tessellated)
    { return Material.primitiveCount(source, view, tessellated); }
    bool project(uint source, uint view, uint primitive, bool tessellated, out float4 first, out float4 second, out float4 third)
    { return Material.project(source, view, primitive, tessellated, first, second, third); }
    bool cover(uint source, uint primitive, bool tessellated, uint3 pixel, float depth, out float resultDepth)
    { return Material.cover(source, primitive, tessellated, pixel, depth, resultDepth); }
    float4 shade<let visibilityDepth : uint>(uint source, uint primitive, uint3 pixel, float depth)
    { return Material.shade<visibilityDepth>(source, primitive, pixel, depth); }
    void receiver<let visibilityDepth : uint>(uint source, uint primitive, uint3 pixel, float depth,
        out float3 position, out float3 normal, out uint flags)
    { Material.receiver<visibilityDepth>(source, primitive, pixel, depth, position, normal, flags); }
};
#undef TRANSPARENCY_MATERIAL_TYPE
#endif
