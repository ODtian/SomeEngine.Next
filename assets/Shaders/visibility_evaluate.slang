#ifndef VISIBILITY_EVALUATE_SLANG
#define VISIBILITY_EVALUATE_SLANG

#include "common.slang"
#include "cluster_structures.slang"

[__AttributeUsage(_AttributeTargets.Struct)]
struct VisibilityDepthAttribute { string behavior; };

struct VisibilityVertexInput
{
    DrawRequest Source;
    uint LocalVertexIndex;
};

struct VisibilityInterpolationInput
{
    // Weights relative to the original source triangle. PerspectiveBarycentric
    // matches ordinary perspective-correct hardware interpolation. LinearBarycentric
    // matches noperspective interpolation, including triangles clipped by SW raster.
    float3 PerspectiveBarycentric;
    float3 LinearBarycentric;
    float3 PerspectiveBarycentricDx;
    float3 PerspectiveBarycentricDy;
    float3 LinearBarycentricDx;
    float3 LinearBarycentricDy;
};

struct VisibilityPixelInput
{
    uint3 PixelCoord;
    // Public material depth is forward even when the target raster uses reverse-Z.
    float Depth;
    // Incoming depth in the same reversed integer representation as the target.
    uint EncodedDepth;
    // Reversed integer depth already stored at the resolved target pixel.
    // A shader retaining input.Depth can reject covered target pixels itself.
    uint EncodedTargetDepth;
    DrawRequest Source;
    uint TriangleID;
    uint ViewIndex;
    uint TargetPolicy;
    uint TargetViewIndex;
};

struct VisibilityPixelResult
{
    uint Visible;
    float Depth;
};

// Visibility data is owned end-to-end by the user shader. Cluster only asks for
// a per-vertex varying, cross-lane transport, interpolation, and the final
// visibility/depth decision. The Varying fields and their hardware interpolation
// semantics are not known to the renderer.
interface IVisibilityEvaluate<TVertex>
{
    associatedtype Varying;

    Varying makeVarying(TVertex vertex, VisibilityVertexInput input);
    Varying readLane(Varying value, uint lane);

    // Used when tessellation creates a new raster vertex inside the source triangle.
    Varying interpolateVertex(
        Varying first,
        Varying second,
        Varying third,
        float3 sourceBarycentric);

    // Used by software raster at a covered pixel. Hardware raster performs this
    // interpolation natively from the user-defined Varying stage interface.
    Varying interpolatePixel(
        Varying first,
        Varying second,
        Varying third,
        VisibilityInterpolationInput input);

    VisibilityPixelResult evaluate(VisibilityPixelInput input, Varying varying);
};

struct EmptyVisibilityVarying
{
};

[VisibilityDepth("opaque")]
[MaterialDepthInputs("")]
struct OpaqueVisibilityEvaluate<TVertex> : IVisibilityEvaluate<TVertex>
{
    typealias Varying = EmptyVisibilityVarying;

    Varying makeVarying(TVertex vertex, VisibilityVertexInput input)
    {
        return {};
    }

    Varying readLane(Varying value, uint lane)
    {
        return {};
    }

    Varying interpolateVertex(
        Varying first,
        Varying second,
        Varying third,
        float3 sourceBarycentric)
    {
        return {};
    }

    Varying interpolatePixel(
        Varying first,
        Varying second,
        Varying third,
        VisibilityInterpolationInput input)
    {
        return {};
    }

    VisibilityPixelResult evaluate(VisibilityPixelInput input, Varying varying)
    {
        VisibilityPixelResult result;
        result.Visible = input.TargetPolicy == 0u ||
            input.EncodedTargetDepth < input.EncodedDepth ? 1u : 0u;
        result.Depth = input.Depth;
        return result;
    }
};

#endif
