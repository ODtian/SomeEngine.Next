#ifndef DOMAIN_INTERFACES_SLANG
#define DOMAIN_INTERFACES_SLANG

struct SurfaceBarycentrics
{
    float3 Value;
    float3 Dx;
    float3 Dy;
    float Residual;
    uint Valid;
};

struct SurfaceReconstructionInput
{
    float3 TargetPosition;
    float3 TargetPositionDx;
    float3 TargetPositionDy;
    float3 CameraPosition;
    float3 ParentValue;
    float3 ParentDx;
    float3 ParentDy;
    float3 WorldTranslation;
    float4x4 ViewProjection;
    uint2 ScreenSize;
};

// Conservative geometric approximation error for one triangular domain patch.
// Edge components correspond to (bary0,bary1), (bary1,bary2), and (bary2,bary0).
struct DomainErrorBounds
{
    float3 Edge;
    float Interior;
};

float3 Barycentric(float2 uv)
{
    return float3(1.0 - uv.x - uv.y, uv.x, uv.y);
}

float3 ClampBarycentric(float3 value)
{
    value = max(value, 0.0);
    float sum = value.x + value.y + value.z;
    return sum > 1e-8 ? value / sum : float3(1.0, 0.0, 0.0);
}

float3 TransformPatchBarycentric(
    float3 bary0,
    float3 bary1,
    float3 bary2,
    float3 local)
{
    return bary0 * local.x + bary1 * local.y + bary2 * local.z;
}

// A domain owns both its triangle representation and evaluated surface payload.
// Cluster code only supplies opaque evaluated vertices and calls these behaviors.
interface IDomainEvaluate<TVertex>
{
    associatedtype TriangleData;
    associatedtype Surface;

    // Construction depends on the supplied vertices. Matching triangles may share
    // one construction; the domain transports its complete representation.
    TriangleData makeTriangle(TVertex first, TVertex second, TVertex third);
    TriangleData readLane(TriangleData triangle, uint lane);
    float3 cornerPosition(TriangleData triangle, uint corner);
    float3 position(TriangleData triangle, float3 barycentric);
    Surface eval(TriangleData triangle, float3 barycentric);
    SurfaceBarycentrics reconstruct(
        TriangleData triangle,
        SurfaceReconstructionInput input);
    void bounds(
        TriangleData triangle,
        out float3 minimum,
        out float3 maximum);
    void patchBounds(
        TriangleData triangle,
        float3 bary0,
        float3 bary1,
        float3 bary2,
        out float3 minimum,
        out float3 maximum);
    DomainErrorBounds errorBounds(
        TriangleData triangle,
        float3 bary0,
        float3 bary1,
        float3 bary2,
        float3 position0,
        float3 position1,
        float3 position2);
};

#endif
