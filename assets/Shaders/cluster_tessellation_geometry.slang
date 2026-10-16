#ifndef CLUSTER_TESSELLATION_GEOMETRY_SLANG
#define CLUSTER_TESSELLATION_GEOMETRY_SLANG

#include "cluster_tessellation_types.slang"
#include "cluster_view.slang"
#include "domain_interfaces.slang"

float2 TessellationScreenPosition(float4 clip, ClusterViewUniforms view)
{
    if (clip.w <= 1e-5) return 1e20.xx;
    return (clip.xy / clip.w * float2(0.5, -0.5) + 0.5) * float2(view.ScreenWidth, view.ScreenHeight);
}
uint TessellationRequestedEdgeFactor(float edgePixels, float targetPixels)
{
    float requestedFloat = ceil(max(edgePixels, 0.0) / targetPixels);
    uint requested = requestedFloat >= 65536.0
        ? 65536u
        : max(1u, uint(requestedFloat));
    return TessellationNextPowerOfTwo(requested);
}

float CalculateTessellationEdgePixels(float3 first, float3 second, ClusterViewUniforms view)
{
    float3 firstView = mul(float4(ClusterPositionForView(first, view), 1.0), view.View).xyz;
    float3 secondView = mul(float4(ClusterPositionForView(second, view), 1.0), view.View).xyz;
    float edgeDepth = ClusterProjectionScales(view).z <= 1e-8
        ? 1.0 : max(min(-firstView.z, -secondView.z), view.NearPlane);
    return length(firstView - secondView) * CalculateClusterViewPixelScale(view) *
        view.GeometryLodScale / edgeDepth;
}

uint TessellationMaximumSplitDepth(uint maxFactor)
{
    uint factor = max(maxFactor, TESS_PATTERN_MAX_FACTOR);
    uint depth = 0u;
    while (factor > TESS_PATTERN_MAX_FACTOR)
    {
        factor >>= 1u;
        depth++;
    }
    return depth;
}

void EvaluateTessellationPatch<TVertex, TDomain>(
    uniform TDomain domain,
    TDomain.TriangleData triangle,
    ClusterViewUniforms view,
    float3 bary0,
    float3 bary1,
    float3 bary2,
    float targetEdgePixels,
    out uint3 edgeFactors,
    out uint splitMask,
    out bool splitInterior)
    where TDomain : IDomainEvaluate<TVertex>
{
    // Evaluate each corner exactly once. The domain receives those positions again when
    // producing error bounds so texture/analytic implementations do not resample them.
    float3 position0 = domain.position(triangle, bary0);
    float3 position1 = domain.position(triangle, bary1);
    float3 position2 = domain.position(triangle, bary2);
    DomainErrorBounds error = domain.errorBounds(
        triangle,
        bary0,
        bary1,
        bary2,
        position0,
        position1,
        position2);

    float4 clip0 = mul(float4(ClusterPositionForView(position0, view), 1.0), view.ViewProj);
    float4 clip1 = mul(float4(ClusterPositionForView(position1, view), 1.0), view.ViewProj);
    float4 clip2 = mul(float4(ClusterPositionForView(position2, view), 1.0), view.ViewProj);
    float2 screen0 = TessellationScreenPosition(clip0, view);
    float2 screen1 = TessellationScreenPosition(clip1, view);
    float2 screen2 = TessellationScreenPosition(clip2, view);
    float targetPixels = view.TessellationEdgePixels > 0.0
        ? view.TessellationEdgePixels : targetEdgePixels;
    float3 edgePixels = view.TargetPolicy != 0u
        ? float3(CalculateTessellationEdgePixels(position0, position1, view),
            CalculateTessellationEdgePixels(position1, position2, view),
            CalculateTessellationEdgePixels(position2, position0, view))
        : float3(length(screen1 - screen0), length(screen2 - screen1), length(screen0 - screen2));
    edgeFactors = uint3(
        TessellationRequestedEdgeFactor(edgePixels.x, targetPixels),
        TessellationRequestedEdgeFactor(edgePixels.y, targetPixels),
        TessellationRequestedEdgeFactor(edgePixels.z, targetPixels));

    float3 scales = ClusterProjectionScales(view);
    uint2 size = uint2(view.ScreenWidth, view.ScreenHeight);
    float3 edgeErrorPixels = float3(
        ClusterProjectedError(min(clip0.w, clip1.w), max(abs(clip0.xy), abs(clip1.xy)), error.Edge.x, scales, size),
        ClusterProjectedError(min(clip1.w, clip2.w), max(abs(clip1.xy), abs(clip2.xy)), error.Edge.y, scales, size),
        ClusterProjectedError(min(clip2.w, clip0.w), max(abs(clip2.xy), abs(clip0.xy)), error.Edge.z, scales, size));
    float interiorErrorPixels = ClusterProjectedError(min(clip0.w, min(clip1.w, clip2.w)),
        max(abs(clip0.xy), max(abs(clip1.xy), abs(clip2.xy))), error.Interior, scales, size);
    if (view.TargetPolicy != 0u)
    {
        edgeErrorPixels *= view.GeometryLodScale;
        interiorErrorPixels *= view.GeometryLodScale;
    }
    // A final patch may still be diced into smaller triangles. If both the actual
    // surface and every diced vertex stay within E of the patch's corner triangle,
    // the piecewise diced approximation is within at most 2E. Use half the requested
    // pixel tolerance here so the final approximation remains conservative.
    float errorTarget = targetPixels * 0.5;
    bool3 splitEdges =
        (edgeFactors > TESS_PATTERN_MAX_FACTOR.xxx) ||
        (edgeErrorPixels > errorTarget.xxx);
    splitMask =
        (splitEdges.x ? 1u : 0u) |
        (splitEdges.y ? 2u : 0u) |
        (splitEdges.z ? 4u : 0u);
    splitInterior = interiorErrorPixels > errorTarget;
}

TessellationPatch MakeTessellationChild(
    TessellationPatch parent,
    float3 bary0,
    float3 bary1,
    float3 bary2)
{
    TessellationPatch child = parent;
    SetTessellationPatchBarycentrics(child, bary0, bary1, bary2);
    SetTessellationPatchState(
        child,
        min(TessellationPatchDepth(parent) + 1u, 255u),
        1u.xxx);
    return child;
}

uint BuildTessellationChildren(
    TessellationPatch parent,
    uint splitMask,
    bool splitInterior,
    out TessellationPatch children[4])
{
    float3 a = DecodeTessellationPatchBarycentric(parent, 0u);
    float3 b = DecodeTessellationPatchBarycentric(parent, 1u);
    float3 c = DecodeTessellationPatchBarycentric(parent, 2u);
    if (splitMask == 0u)
    {
        if (!splitInterior)
            return 0u;

        // Interior refinement must not invent new points on a shared outer edge.
        // A centroid split preserves all three boundary segments exactly.
        float3 center = (a + b + c) / 3.0;
        children[0] = MakeTessellationChild(parent, a, b, center);
        children[1] = MakeTessellationChild(parent, b, c, center);
        children[2] = MakeTessellationChild(parent, c, a, center);
        return 3u;
    }

    float3 ab = (a + b) * 0.5;
    float3 bc = (b + c) * 0.5;
    float3 ca = (c + a) * 0.5;
    if (splitMask == 1u)
    {
        children[0] = MakeTessellationChild(parent, a, ab, c);
        children[1] = MakeTessellationChild(parent, ab, b, c);
        return 2u;
    }
    if (splitMask == 2u)
    {
        children[0] = MakeTessellationChild(parent, b, bc, a);
        children[1] = MakeTessellationChild(parent, bc, c, a);
        return 2u;
    }
    if (splitMask == 4u)
    {
        children[0] = MakeTessellationChild(parent, c, ca, b);
        children[1] = MakeTessellationChild(parent, ca, a, b);
        return 2u;
    }
    if (splitMask == 3u)
    {
        children[0] = MakeTessellationChild(parent, a, ab, c);
        children[1] = MakeTessellationChild(parent, ab, b, bc);
        children[2] = MakeTessellationChild(parent, ab, bc, c);
        return 3u;
    }
    if (splitMask == 5u)
    {
        children[0] = MakeTessellationChild(parent, a, ab, ca);
        children[1] = MakeTessellationChild(parent, ab, b, c);
        children[2] = MakeTessellationChild(parent, ab, c, ca);
        return 3u;
    }
    if (splitMask == 6u)
    {
        children[0] = MakeTessellationChild(parent, a, b, bc);
        children[1] = MakeTessellationChild(parent, a, bc, ca);
        children[2] = MakeTessellationChild(parent, bc, c, ca);
        return 3u;
    }

    children[0] = MakeTessellationChild(parent, a, ab, ca);
    children[1] = MakeTessellationChild(parent, ab, b, bc);
    children[2] = MakeTessellationChild(parent, ca, bc, c);
    children[3] = MakeTessellationChild(parent, ab, bc, ca);
    return 4u;
}

// A depth-first traversal retains siblings for the five supported split levels.
// Each emitted patch can be diced before advancing, so storage follows depth.
struct TessellationTraversal
{
    TessellationPatch Patches[16];
    uint Count;
};

TessellationTraversal BeginTessellationTraversal()
{
    TessellationTraversal traversal = (TessellationTraversal)0;
    TessellationPatch root = (TessellationPatch)0;
    SetTessellationPatchBarycentrics(root, float3(1, 0, 0), float3(0, 1, 0), float3(0, 0, 1));
    SetTessellationPatchState(root, 0u, 1u.xxx);
    traversal.Patches[0] = root;
    traversal.Count = 1u;
    return traversal;
}

bool NextTessellationPatch<TVertex, TDomain>(
    uniform TDomain domain,
    TDomain.TriangleData triangle,
    ClusterViewUniforms view,
    uint maxFactor,
    float targetEdgePixels,
    inout TessellationTraversal traversal,
    out TessellationPatch patch)
    where TDomain : IDomainEvaluate<TVertex>
{
    while (traversal.Count != 0u)
    {
        patch = traversal.Patches[--traversal.Count];
        uint3 edgeFactors;
        uint splitMask;
        bool splitInterior;
        EvaluateTessellationPatch<TVertex, TDomain>(domain, triangle, view,
            DecodeTessellationPatchBarycentric(patch, 0u),
            DecodeTessellationPatchBarycentric(patch, 1u),
            DecodeTessellationPatchBarycentric(patch, 2u), targetEdgePixels,
            edgeFactors, splitMask, splitInterior);
        uint depth = TessellationPatchDepth(patch);
        if ((splitMask != 0u || splitInterior) && depth < TessellationMaximumSplitDepth(maxFactor))
        {
            TessellationPatch children[4];
            uint count = BuildTessellationChildren(patch, splitMask, splitInterior, children);
            for (uint index = count; index > 0u; index--)
                traversal.Patches[traversal.Count++] = children[index - 1u];
            continue;
        }
        edgeFactors = clamp(edgeFactors, 1u.xxx, min(maxFactor, TESS_PATTERN_MAX_FACTOR).xxx);
        SetTessellationPatchState(patch, depth, edgeFactors);
        return true;
    }
    patch = (TessellationPatch)0;
    return false;
}

#endif
