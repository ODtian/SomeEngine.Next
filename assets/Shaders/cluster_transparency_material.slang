#ifndef CLUSTER_TRANSPARENCY_MATERIAL_SLANG
#define CLUSTER_TRANSPARENCY_MATERIAL_SLANG

#include "cluster_transparency_source.slang"
#include "cluster_tessellation_geometry.slang"

[__AttributeUsage(_AttributeTargets.Struct)]
struct TransparencyMaterialAttribute {};

// User vertex, surface and traversal data stay in their source unit. Composition
// names each concrete Projection and exchanges clip-space triangles.
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

    struct Projection
    {
        DomainEvaluate.TriangleData Triangle;
        TessellationTraversal Traversal;
        TessellationPatch Patch;
        float4 ScreenBounds;
        uint Instance, Primitive, PatchPrimitive, PatchCount, Factor, Enabled;
        uint3 EdgeFactors;
    };

    bool overlapsScreenBounds(float3 minimum, float3 maximum, ClusterViewUniforms view, float4 screenBounds)
    {
        float2 low = 1e20.xx, high = -1e20.xx;
        [unroll]
        for (uint corner = 0u; corner < 8u; corner++)
        {
            float3 position = float3((corner & 1u) != 0u ? maximum.x : minimum.x,
                (corner & 2u) != 0u ? maximum.y : minimum.y, (corner & 4u) != 0u ? maximum.z : minimum.z);
            float4 clip = projectPosition(position, view);
            if (clip.w <= 0.0) return true;
            float2 pixel = TessellationScreenPosition(clip, view);
            low = min(low, pixel); high = max(high, pixel);
        }
        float margin = .5 / CLUSTER_RASTER_SUBPIXEL_SCALE_F;
        return all(high + margin >= screenBounds.xy) && all(low - margin <= screenBounds.zw);
    }

    Projection beginProjection(uint source, uint viewIndex, bool tessellated, float4 screenBounds)
    {
        Projection value = {};
        TriangleInfo info = triangleInfo(source, viewIndex);
        value.Triangle = evaluateTriangle<VertexEvaluate, DomainEvaluate>(Vertex, Domain, info);
        ClusterViewUniforms view = ApplyFirstPersonViewTransform(InstanceData, InstanceProperties, info.instanceID, Views[viewIndex]);
        float3 minimum, maximum;
        Domain.bounds(value.Triangle, minimum, maximum);
        value.Enabled = overlapsScreenBounds(minimum, maximum, view, screenBounds) ? 1u : 0u;
        value.Instance = info.instanceID;
        value.ScreenBounds = screenBounds;
        if (tessellated) value.Traversal = BeginTessellationTraversal();
        return value;
    }

    float4 projectPosition(float3 position, ClusterViewUniforms view)
    {
        return mul(float4(ClusterPositionForView(position, view), 1.0), view.ViewProj);
    }

    bool nextProjection(uint source, uint viewIndex, bool tessellated, inout Projection value,
        out uint primitive, out float3x2 domain, out float4 first, out float4 second, out float4 third)
    {
        primitive = 0u;
        domain = {};
        first = second = third = 0.0.xxxx;
        if (value.Enabled == 0u) return false;
        ClusterViewUniforms view = ApplyFirstPersonViewTransform(InstanceData, InstanceProperties, value.Instance, Views[viewIndex]);
        if (!tessellated)
        {
            value.Enabled = 0u;
            domain[1] = float2(1, 0);
            domain[2] = float2(0, 1);
            first = projectPosition(Domain.cornerPosition(value.Triangle, 0u), view);
            second = projectPosition(Domain.cornerPosition(value.Triangle, 1u), view);
            third = projectPosition(Domain.cornerPosition(value.Triangle, 2u), view);
            return true;
        }
        while (value.PatchPrimitive >= value.PatchCount)
        {
            if (!NextTessellationPatch<VertexEvaluate.Vertex, DomainEvaluate>(Domain, value.Triangle, view,
                TransparencyGeometryUniforms.MaxFactor, TransparencyGeometryUniforms.TargetEdgePixels, value.Traversal, value.Patch))
                return false;
            value.EdgeFactors = TessellationPatchEdgeFactors(value.Patch);
            value.Factor = max(value.EdgeFactors.x, max(value.EdgeFactors.y, value.EdgeFactors.z));
            value.PatchCount = TessellationGridTriangleCount(value.Factor);
            value.PatchPrimitive = 0u;
            float3 minimum, maximum;
            Domain.patchBounds(value.Triangle, DecodeTessellationPatchBarycentric(value.Patch, 0u),
                DecodeTessellationPatchBarycentric(value.Patch, 1u), DecodeTessellationPatchBarycentric(value.Patch, 2u), minimum, maximum);
            if (!overlapsScreenBounds(minimum, maximum, view, value.ScreenBounds))
            {
                value.Primitive += value.PatchCount;
                value.PatchPrimitive = value.PatchCount;
            }
        }
        uint3 vertices = TessellationGridTriangleVertexIndices(value.Factor, value.PatchPrimitive++);
        float3 bary0 = DecodeTessellationPatchBarycentric(value.Patch, 0u);
        float3 bary1 = DecodeTessellationPatchBarycentric(value.Patch, 1u);
        float3 bary2 = DecodeTessellationPatchBarycentric(value.Patch, 2u);
        primitive = value.Primitive++;
        float3 firstDomain = TransformPatchBarycentric(bary0, bary1, bary2,
            TessellationGridBarycentric(value.Factor, value.EdgeFactors, vertices.x));
        float3 secondDomain = TransformPatchBarycentric(bary0, bary1, bary2,
            TessellationGridBarycentric(value.Factor, value.EdgeFactors, vertices.y));
        float3 thirdDomain = TransformPatchBarycentric(bary0, bary1, bary2,
            TessellationGridBarycentric(value.Factor, value.EdgeFactors, vertices.z));
        domain[0] = firstDomain.yz;
        domain[1] = secondDomain.yz;
        domain[2] = thirdDomain.yz;
        first = projectPosition(Domain.position(value.Triangle, firstDomain), view);
        second = projectPosition(Domain.position(value.Triangle, secondDomain), view);
        third = projectPosition(Domain.position(value.Triangle, thirdDomain), view);
        return true;
    }

    bool cover(uint source, float3x2 domain, bool tessellated, uint3 coord, float depth, out float resultDepth)
    {
        TriangleInfo info = triangleInfo(source, coord.z);
        RenderInstanceTransform transform = GetCurrentInstanceTransform(info.instanceID);
        VertexEvaluate.Vertex first = Vertex.evaluate(makeVertexEvalContext(info, transform, 0u, info.vi0));
        VertexEvaluate.Vertex second = Vertex.evaluate(makeVertexEvalContext(info, transform, 0u, info.vi1));
        VertexEvaluate.Vertex third = Vertex.evaluate(makeVertexEvalContext(info, transform, 0u, info.vi2));
        DomainEvaluate.TriangleData triangle = Domain.makeTriangle(first, second, third);
        ClusterViewUniforms view = ApplyFirstPersonViewTransform(InstanceData, InstanceProperties, info.instanceID, Views[coord.z]);
        float3 bary0 = Barycentric(domain[0]), bary1 = Barycentric(domain[1]), bary2 = Barycentric(domain[2]);
        resultDepth = depth;
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
        VisibilityVertexInput vertexInput = { TransparencySourceRequest(source), info.vi0 };
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
        pixel.Depth = dot(interpolation.LinearBarycentric, float3(clip0.z / clip0.w, clip1.z / clip1.w, clip2.z / clip2.w));
        pixel.EncodedDepth = asuint(1.0 - pixel.Depth);
        pixel.Source = TransparencySourceRequest(source);
        pixel.TriangleID = info.triangleID;
        pixel.ViewIndex = coord.z;
        VisibilityPixelResult result = Visibility.evaluate(pixel, Visibility.interpolatePixel(v0, v1, v2, interpolation));
        resultDepth = result.Depth;
        if (result.Visible == 0u) return false;
        float3 position;
        SurfaceBarycentrics coordinates = ResolveBarycentricsAtDepth<VertexEvaluate.Vertex, DomainEvaluate>(
            Domain, coord, view, triangle, info, result.Depth, position);
        return coordinates.Valid != 0u;
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
    typealias Projection = TRANSPARENCY_EVALUATOR(TRANSPARENCY_MATERIAL_TYPE).Projection;
    Projection beginProjection(uint source, uint view, bool tessellated, float4 screenBounds)
    { return Material.beginProjection(source, view, tessellated, screenBounds); }
    bool nextProjection(uint source, uint view, bool tessellated, inout Projection value,
        out uint primitive, out float3x2 domain, out float4 first, out float4 second, out float4 third)
    { return Material.nextProjection(source, view, tessellated, value, primitive, domain, first, second, third); }
    bool cover(uint source, float3x2 domain, bool tessellated, uint3 pixel, float depth, out float resultDepth)
    { return Material.cover(source, domain, tessellated, pixel, depth, resultDepth); }
    float4 shade<let visibilityDepth : uint>(uint source, uint primitive, uint3 pixel, float depth)
    { return Material.shade<visibilityDepth>(source, primitive, pixel, depth); }
    void receiver<let visibilityDepth : uint>(uint source, uint primitive, uint3 pixel, float depth,
        out float3 position, out float3 normal, out uint flags)
    { Material.receiver<visibilityDepth>(source, primitive, pixel, depth, position, normal, flags); }
};
#undef TRANSPARENCY_MATERIAL_TYPE
#endif
