#ifndef TRIANGLE_SURFACE_INVERSE_SLANG
#define TRIANGLE_SURFACE_INVERSE_SLANG

#include "triangle_surface.slang"

struct SurfaceBarycentrics
{
    float3 Value;
    float3 Dx;
    float3 Dy;
    float Residual;
    uint Valid;
};

float2 SolveSurfaceNormalEquations(
    float a,
    float b,
    float c,
    float2 rightHandSide)
{
    float determinant = a * c - b * b;
    float inverse = abs(determinant) > 1e-12 ? rcp(determinant) : 0.0;
    return float2(
        c * rightHandSide.x - b * rightHandSide.y,
        a * rightHandSide.y - b * rightHandSide.x) * inverse;
}

// Fixed-cost, surface-type-independent inverse used by deferred visibility shading. The user
// surface supplies only eval(); the pipeline owns reconstruction policy. No surface discriminator,
// permutation, or auxiliary visibility payload is required.
SurfaceBarycentrics ResolveTriangleSurface<TSurface : ITriangleSurface>(
    uniform TSurface surface,
    Triangle triangle,
    float3 targetPosition,
    float3 parentTriangleSeed,
    float4x4 viewProjection,
    uint2 screenSize)
{
    float2 parameters = parentTriangleSeed.yz;
    [unroll]
    for (uint iteration = 0u; iteration < 2u; iteration++)
    {
        SurfaceEval evaluation = surface.eval(triangle, Barycentric(parameters));
        float3 residual = evaluation.Position - targetPosition;
        float a = dot(evaluation.DpDu, evaluation.DpDu);
        float b = dot(evaluation.DpDu, evaluation.DpDv);
        float c = dot(evaluation.DpDv, evaluation.DpDv);
        float damping = max(1e-10, (a + c) * 1e-7);
        float2 delta = SolveSurfaceNormalEquations(
            a + damping,
            b,
            c + damping,
            float2(
                dot(evaluation.DpDu, residual),
                dot(evaluation.DpDv, residual)));
        float maximumStep = max(abs(delta.x), abs(delta.y));
        delta *= min(1.0, 0.25 / max(maximumStep, 1e-12));
        parameters -= delta;
    }

    float3 unresolved = Barycentric(parameters);
    float3 value = ClampBarycentric(unresolved);
    SurfaceEval evaluation = surface.eval(triangle, value);
    float3 finalResidual = evaluation.Position - targetPosition;

    float4 clip = mul(float4(evaluation.Position, 1.0), viewProjection);
    float4 clipDu = mul(float4(evaluation.DpDu, 0.0), viewProjection);
    float4 clipDv = mul(float4(evaluation.DpDv, 0.0), viewProjection);
    float inverseW2 = abs(clip.w) > 1e-8 ? rcp(clip.w * clip.w) : 0.0;
    float2 ndcDu = (clipDu.xy * clip.w - clip.xy * clipDu.w) * inverseW2;
    float2 ndcDv = (clipDv.xy * clip.w - clip.xy * clipDv.w) * inverseW2;
    float2 screenScale = float2(0.5 * float(screenSize.x), -0.5 * float(screenSize.y));
    float2 pixelDu = ndcDu * screenScale;
    float2 pixelDv = ndcDv * screenScale;
    float determinant = pixelDu.x * pixelDv.y - pixelDv.x * pixelDu.y;
    float inverseDeterminant = abs(determinant) > 1e-10 ? rcp(determinant) : 0.0;
    float2 parameterDx = float2(pixelDv.y, -pixelDu.y) * inverseDeterminant;
    float2 parameterDy = float2(-pixelDv.x, pixelDu.x) * inverseDeterminant;

    float barycentricViolation = max(
        max(-unresolved.x, -unresolved.y),
        max(-unresolved.z, abs(unresolved.x + unresolved.y + unresolved.z - 1.0)));

    SurfaceBarycentrics result;
    result.Value = value;
    result.Dx = float3(-parameterDx.x - parameterDx.y, parameterDx.x, parameterDx.y);
    result.Dy = float3(-parameterDy.x - parameterDy.y, parameterDy.x, parameterDy.y);
    result.Residual = length(finalResidual);
    result.Valid =
        abs(determinant) > 1e-10 &&
        barycentricViolation <= 1e-3 &&
        all(isfinite(evaluation.Position)) &&
        all(isfinite(evaluation.DpDu)) &&
        all(isfinite(evaluation.DpDv));
    return result;
}

#endif
