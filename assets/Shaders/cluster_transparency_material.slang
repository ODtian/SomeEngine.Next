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

    TriangleInfo triangleInfo(uint source, uint viewIndex, bool shareSource = true)
    {
        TriangleInfo info = {};
        uint leader = shareSource ? WaveMaskFirstLane(WaveMatchingLanes(source) & WaveMatchingLanes(viewIndex)) : WaveGetLaneIndex();
        if (WaveGetLaneIndex() == leader)
            LoadTriangleInfo(TransparencySourceRequest(source), TransparencySourceTriangle(source), info);
        if (shareSource) info = ReadTriangleInfoLane(info, leader);
        info.viewIndex = viewIndex;
        info.visibleClusterIndex = source >> 7u;
        info.visibility = source;
        info.sourceLane = leader;
        return info;
    }

    struct SourceData
    {
        TriangleInfo Info;
        DomainEvaluate.TriangleData Triangle;
        VisibilityEvaluate.Varying First, Second, Third;
        PipelineTriangleProjection Projected;
    };

    SourceData readSourceData(SourceData value, uint lane)
    {
        value.Info.visibility = WaveReadLaneAt(value.Info.visibility, lane);
        value.Info.viewIndex = WaveReadLaneAt(value.Info.viewIndex, lane);
        value.Info = ReadTriangleInfoLane(value.Info, lane);
        value.Info.sourceLane = lane;
        value.Triangle = Domain.readLane(value.Triangle, lane);
        value.First = Visibility.readLane(value.First, lane);
        value.Second = Visibility.readLane(value.Second, lane);
        value.Third = Visibility.readLane(value.Third, lane);
        value.Projected.Origin = WaveReadLaneAt(value.Projected.Origin, lane);
        value.Projected.FirstEdge = WaveReadLaneAt(value.Projected.FirstEdge, lane);
        value.Projected.SecondEdge = WaveReadLaneAt(value.Projected.SecondEdge, lane);
        value.Projected.InverseArea = WaveReadLaneAt(value.Projected.InverseArea, lane);
        value.Projected.InverseW = WaveReadLaneAt(value.Projected.InverseW, lane);
        return value;
    }

    SourceData evaluateSource(TriangleInfo info, ClusterViewUniforms view,
        out float4 firstClip, out float4 secondClip, out float4 thirdClip, bool shareSource = true)
    {
        SourceData value = {};
        uint leader = info.sourceLane;
        if (WaveGetLaneIndex() == leader)
        {
            value.Info = info;
            RenderInstanceTransform transform = GetCurrentInstanceTransform(info.instanceID);
            TriangleVertices<VertexEvaluate.Vertex> vertices = EvaluateTriangleVertices(
                Vertex, makeVertexEvalContext(info, transform, 0u, info.vi0), uint3(info.vi0, info.vi1, info.vi2));
            VertexEvaluate.Vertex first = vertices.First;
            VertexEvaluate.Vertex second = vertices.Second;
            VertexEvaluate.Vertex third = vertices.Third;
            value.Triangle = Domain.makeTriangle(first, second, third, info.materialSlot);
            VisibilityVertexInput input = { TransparencySourceRequest(info.visibility), info.vi0 };
            value.First = Visibility.makeVarying(first, input);
            input.LocalVertexIndex = info.vi1; value.Second = Visibility.makeVarying(second, input);
            input.LocalVertexIndex = info.vi2; value.Third = Visibility.makeVarying(third, input);
        }
        if (shareSource) value = readSourceData(value, leader);
        firstClip = secondClip = thirdClip = 0.0.xxxx;
        if (WaveGetLaneIndex() == leader)
        {
            firstClip = projectPosition(Domain.cornerPosition(value.Triangle,0u),view);
            secondClip = projectPosition(Domain.cornerPosition(value.Triangle,1u),view);
            thirdClip = projectPosition(Domain.cornerPosition(value.Triangle,2u),view);
        }
        if (shareSource)
        {
            firstClip = WaveReadLaneAt(firstClip,leader);
            secondClip = WaveReadLaneAt(secondClip,leader);
            thirdClip = WaveReadLaneAt(thirdClip,leader);
        }
        float2 firstPixel = TessellationScreenPosition(firstClip,view);
        float2 secondPixel = TessellationScreenPosition(secondClip,view);
        float2 thirdPixel = TessellationScreenPosition(thirdClip,view);
        value.Projected.Origin = firstPixel;
        value.Projected.FirstEdge = secondPixel - firstPixel;
        value.Projected.SecondEdge = thirdPixel - firstPixel;
        value.Projected.InverseArea = 1.0 / (value.Projected.FirstEdge.x * value.Projected.SecondEdge.y -
            value.Projected.FirstEdge.y * value.Projected.SecondEdge.x);
        value.Projected.InverseW = 1.0 / float3(firstClip.w,secondClip.w,thirdClip.w);
        return value;
    }

    SourceData evaluateSource(TriangleInfo info, ClusterViewUniforms view)
    {
        float4 firstClip, secondClip, thirdClip;
        return evaluateSource(info,view,firstClip,secondClip,thirdClip);
    }

    struct Projection
    {
        SourceData Source;
        TessellationTraversal Traversal;
        TessellationPatch Patch;
        float4 ScreenBounds;
        float4 FirstClip, SecondClip, ThirdClip;
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
        bool shareSource = WaveActiveAllEqual(source) && WaveActiveAllEqual(viewIndex);
        TriangleInfo info = triangleInfo(source, viewIndex,shareSource);
        ClusterViewUniforms view = ApplyFirstPersonViewTransform(InstanceData, InstanceProperties, info.instanceID, Views[viewIndex]);
        value.Source = evaluateSource(info, view,value.FirstClip,value.SecondClip,value.ThirdClip,shareSource);
        value.Enabled = 1u;
        value.Instance = info.instanceID;
        value.ScreenBounds = screenBounds;
        if (tessellated)
        {
            float3 minimum, maximum;
            Domain.bounds(value.Source.Triangle,minimum,maximum);
            value.Enabled = overlapsScreenBounds(minimum,maximum,view,screenBounds) ? 1u : 0u;
            value.Traversal = BeginTessellationTraversal();
        }
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
        if (!tessellated)
        {
            value.Enabled = 0u;
            domain[1] = float2(1, 0);
            domain[2] = float2(0, 1);
            first = value.FirstClip; second = value.SecondClip; third = value.ThirdClip;
            return true;
        }
        ClusterViewUniforms view = ApplyFirstPersonViewTransform(InstanceData, InstanceProperties, value.Instance, Views[viewIndex]);
        while (value.PatchPrimitive >= value.PatchCount)
        {
            if (!NextTessellationPatch<VertexEvaluate.Vertex, DomainEvaluate>(Domain, value.Source.Triangle, view,
                TransparencyGeometryUniforms.MaxFactor, TransparencyGeometryUniforms.TargetEdgePixels, value.Traversal, value.Patch))
                return false;
            value.EdgeFactors = TessellationPatchEdgeFactors(value.Patch);
            value.Factor = max(value.EdgeFactors.x, max(value.EdgeFactors.y, value.EdgeFactors.z));
            value.PatchCount = TessellationGridTriangleCount(value.Factor);
            value.PatchPrimitive = 0u;
            float3 minimum, maximum;
            Domain.patchBounds(value.Source.Triangle, DecodeTessellationPatchBarycentric(value.Patch, 0u),
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
        first = projectPosition(Domain.position(value.Source.Triangle, firstDomain), view);
        second = projectPosition(Domain.position(value.Source.Triangle, secondDomain), view);
        third = projectPosition(Domain.position(value.Source.Triangle, thirdDomain), view);
        return true;
    }

    bool cover(uint source, float3x2 domain, bool tessellated, uint3 coord, float depth, out float resultDepth)
    {
        TriangleInfo info = triangleInfo(source, coord.z);
        ClusterViewUniforms view = ApplyFirstPersonViewTransform(InstanceData, InstanceProperties, info.instanceID, Views[coord.z]);
        return coverData<1u>(evaluateSource(info, view), domain, tessellated, coord, depth, resultDepth);
    }

    SurfaceBarycentrics coordinates(SourceData value, uint3 coord, float depth, out float3 position)
    {
        ClusterViewUniforms view = ApplyFirstPersonViewTransform(InstanceData, InstanceProperties, value.Info.instanceID, Views[coord.z]);
        return ResolveBarycentricsAtDepth<VertexEvaluate.Vertex, DomainEvaluate>(
            Domain, coord, view, value.Triangle, value.Projected, depth, position);
    }

    bool coverData<let visibilityDepth : uint>(SourceData value, float3x2 domain, bool tessellated,
        uint3 coord, float depth, out float resultDepth)
    {
        resultDepth = depth;
        if (visibilityDepth == 0u)
        {
            float3 position;
            return coordinates(value,coord,depth,position).Valid != 0u;
        }
        TriangleInfo info = value.Info;
        uint source = info.visibility;
        DomainEvaluate.TriangleData triangle = value.Triangle;
        VisibilityEvaluate.Varying source0 = value.First, source1 = value.Second, source2 = value.Third;
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
        VisibilityEvaluate.Varying v0 = Visibility.interpolateVertex(source0, source1, source2, bary0);
        VisibilityEvaluate.Varying v1 = Visibility.interpolateVertex(source0, source1, source2, bary1);
        VisibilityEvaluate.Varying v2 = Visibility.interpolateVertex(source0, source1, source2, bary2);
        VisibilityPixelInput pixel = (VisibilityPixelInput)0;
        pixel.PixelCoord = coord;
        pixel.Depth = depth;
        pixel.EncodedDepth = asuint(1.0 - pixel.Depth);
        pixel.EncodedTargetDepth = asuint(1.0 - DepthBuffer.Load(int4(coord, 0)));
        pixel.Source = TransparencySourceRequest(source);
        pixel.TriangleID = info.triangleID;
        pixel.ViewIndex = coord.z;
        VisibilityPixelResult result = Visibility.evaluate(pixel, Visibility.interpolatePixel(v0, v1, v2, interpolation));
        resultDepth = result.Depth;
        if (result.Visible == 0u) return false;
        float3 position;
        return coordinates(value,coord,result.Depth,position).Valid != 0u;
    }

    bool reconstruct(SourceData value, uint3 coord, float depth, uint visibilityDepth,
        out float3 bary, out DomainEvaluate.Surface evaluated, out SurfacePixelInput pixel)
    {
        ClusterViewUniforms view = ApplyFirstPersonViewTransform(
            InstanceData, InstanceProperties, value.Info.instanceID, Views[coord.z]);
        float3 position;
        SurfaceBarycentrics coordinates = ResolveBarycentricsAtDepth<VertexEvaluate.Vertex, DomainEvaluate>(
            Domain, coord, view, value.Triangle, value.Projected, depth, position);
        bary = coordinates.Value;
        if (coordinates.Valid == 0u) { evaluated = {}; pixel = {}; return false; }
        float3 surfacePosition = Domain.position(value.Triangle, bary);
        EvaluateMaterialHit<VertexEvaluate.Vertex, DomainEvaluate>(Domain, value.Triangle, value.Info,
            coordinates, view, coord, depth, position,
            Domain.position(value.Triangle, bary + coordinates.Dx) - surfacePosition,
            Domain.position(value.Triangle, bary + coordinates.Dy) - surfacePosition,
            visibilityDepth, evaluated, pixel);
        return true;
    }

    float4 shadeData<let visibilityDepth : uint, let debugMode : uint>(SourceData value, uint3 coord, float depth)
    {
        float3 bary;
        DomainEvaluate.Surface evaluated;
        SurfacePixelInput pixel;
        if (!reconstruct(value, coord, depth, visibilityDepth, bary, evaluated, pixel)) return 0.0.xxxx;
        return Surface.evaluateTransparency(debugMode, pixel, evaluated, bary, value.Triangle);
    }

    void reconstruct(uint source, uint3 coord, float depth, uint visibilityDepth, out TriangleInfo info,
        out DomainEvaluate.TriangleData triangle, out float3 bary, out DomainEvaluate.Surface evaluated, out SurfacePixelInput pixel)
    {
        info = triangleInfo(source, coord.z);
        ClusterViewUniforms view = ApplyFirstPersonViewTransform(InstanceData, InstanceProperties, info.instanceID, Views[coord.z]);
        triangle = evaluateTriangle<VertexEvaluate, DomainEvaluate>(Vertex, Domain, info);
        float3 position;
        SurfaceBarycentrics coordinates = ResolveBarycentricsAtDepth<VertexEvaluate.Vertex, DomainEvaluate>(
            Domain, coord, view, triangle, info, depth, position);
        bary = coordinates.Value;
        float3 surfacePosition = Domain.position(triangle, bary);
        EvaluateMaterialHit<VertexEvaluate.Vertex, DomainEvaluate>(Domain, triangle, info,
            coordinates, view, coord, depth, position,
            Domain.position(triangle, bary + coordinates.Dx) - surfacePosition,
            Domain.position(triangle, bary + coordinates.Dy) - surfacePosition,
            visibilityDepth, evaluated, pixel);
    }

    float4 shade<let visibilityDepth : uint>(uint source, uint primitive, uint3 coord, float depth)
    {
        TriangleInfo info;
        DomainEvaluate.TriangleData triangle;
        float3 bary;
        DomainEvaluate.Surface evaluated;
        SurfacePixelInput pixel;
        reconstruct(source, coord, depth, visibilityDepth, info, triangle, bary, evaluated, pixel);
        return Surface.evaluateTransparency(Uniforms.DebugMode, pixel, evaluated, bary, triangle);
    }

};
#endif

#ifdef TRANSPARENCY_MATERIAL_TYPE
[TransparencyMaterial]
struct TRANSPARENCY_MATERIAL_TYPE
{
    TRANSPARENCY_EVALUATOR(TRANSPARENCY_MATERIAL_TYPE) Material;
    typealias Projection = TRANSPARENCY_EVALUATOR(TRANSPARENCY_MATERIAL_TYPE).Projection;
    typealias SourceData = TRANSPARENCY_EVALUATOR(TRANSPARENCY_MATERIAL_TYPE).SourceData;
    SourceData sourceData(Projection value) { return value.Source; }
    SourceData readSourceData(SourceData value, uint lane) { return Material.readSourceData(value,lane); }
    bool coverData<let visibilityDepth : uint>(SourceData value, float3x2 domain, bool tessellated,
        uint3 pixel, float depth, out float resultDepth)
    { return Material.coverData<visibilityDepth>(value,domain,tessellated,pixel,depth,resultDepth); }
    float4 shadeData<let visibilityDepth : uint, let debugMode : uint>(SourceData value, uint3 pixel, float depth)
    { return Material.shadeData<visibilityDepth,debugMode>(value,pixel,depth); }
    Projection beginProjection(uint source, uint view, bool tessellated, float4 screenBounds)
    { return Material.beginProjection(source, view, tessellated, screenBounds); }
    bool nextProjection(uint source, uint view, bool tessellated, inout Projection value,
        out uint primitive, out float3x2 domain, out float4 first, out float4 second, out float4 third)
    { return Material.nextProjection(source, view, tessellated, value, primitive, domain, first, second, third); }
    bool cover(uint source, float3x2 domain, bool tessellated, uint3 pixel, float depth, out float resultDepth)
    { return Material.cover(source, domain, tessellated, pixel, depth, resultDepth); }
    float4 shade<let visibilityDepth : uint>(uint source, uint primitive, uint3 pixel, float depth)
    { return Material.shade<visibilityDepth>(source, primitive, pixel, depth); }
};
#undef TRANSPARENCY_MATERIAL_TYPE
#endif
