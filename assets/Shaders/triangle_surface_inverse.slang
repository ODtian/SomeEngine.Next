#ifndef TRIANGLE_SURFACE_INVERSE_SLANG
#define TRIANGLE_SURFACE_INVERSE_SLANG

#include "triangle_surface.slang"

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

float SurfaceBarycentricViolation(float3 value)
{
    return max(
        max(-value.x, -value.y),
        max(-value.z, abs(value.x + value.y + value.z - 1.0)));
}

float3 RayTriangleBarycentrics(
    float3 origin,
    float3 direction,
    float3 p0,
    float3 p1,
    float3 p2,
    out float determinant)
{
    float3 edge01 = p1 - p0;
    float3 edge02 = p2 - p0;
    float3 origin0 = origin - p0;
    float3 directionCross02 = cross(direction, edge02);
    determinant = dot(edge01, directionCross02);
    float inverse = abs(determinant) > 1e-12 ? rcp(determinant) : 0.0;

    float3 result;
    result.y = inverse * dot(origin0, directionCross02);
    result.z = inverse * dot(direction, cross(origin0, edge01));
    result.x = 1.0 - result.y - result.z;
    return result;
}

float3 ClosestPlaneBarycentrics(
    float3 point,
    float3 p0,
    float3 p1,
    float3 p2,
    out float determinant)
{
    float3 triangleNormal = cross(p2 - p0, p1 - p0);
    return RayTriangleBarycentrics(
        point,
        triangleNormal,
        p0,
        p1,
        p2,
        determinant);
}

SurfaceBarycentrics ReconstructNormalDisplacementSurface(
    Triangle triangle,
    SurfaceReconstructionInput input)
{
    float3 p0 = triangle.Position0;
    float3 p1 = triangle.Position1;
    float3 p2 = triangle.Position2;
    float closestDeterminant = 0.0;

    [unroll]
    for (uint iteration = 0u; iteration < 2u; iteration++)
    {
        float3 bary = ClosestPlaneBarycentrics(
            input.TargetPosition,
            p0,
            p1,
            p2,
            closestDeterminant);
        bary = ClampBarycentric(bary);

        float3 closestPoint =
            p0 * bary.x +
            p1 * bary.y +
            p2 * bary.z;
        float3 closestNormal =
            triangle.Normal0 * bary.x +
            triangle.Normal1 * bary.y +
            triangle.Normal2 * bary.z;
        float normalLengthSquared = dot(closestNormal, closestNormal);
        float displacement = normalLengthSquared > 1e-12
            ? dot(input.TargetPosition - closestPoint, closestNormal) / normalLengthSquared
            : 0.0;

        p0 += triangle.Normal0 * displacement;
        p1 += triangle.Normal1 * displacement;
        p2 += triangle.Normal2 * displacement;
    }

    float3 unresolved = ClosestPlaneBarycentrics(
        input.TargetPosition,
        p0,
        p1,
        p2,
        closestDeterminant);
    float3 value = ClampBarycentric(unresolved);
    float3 reconstructed = p0 * value.x + p1 * value.y + p2 * value.z;

    float determinantDx;
    float3 valueDx = RayTriangleBarycentrics(
        input.CameraPosition,
        input.TargetPositionDx - input.CameraPosition,
        p0,
        p1,
        p2,
        determinantDx);
    float determinantDy;
    float3 valueDy = RayTriangleBarycentrics(
        input.CameraPosition,
        input.TargetPositionDy - input.CameraPosition,
        p0,
        p1,
        p2,
        determinantDy);

    SurfaceBarycentrics result;
    result.Value = value;
    result.Dx = valueDx - value;
    result.Dy = valueDy - value;
    result.Residual = length(reconstructed - input.TargetPosition);
    result.Valid =
        abs(closestDeterminant) > 1e-12 &&
        abs(determinantDx) > 1e-12 &&
        abs(determinantDy) > 1e-12 &&
        SurfaceBarycentricViolation(unresolved) <= 1e-3 &&
        all(isfinite(value)) &&
        all(isfinite(result.Dx)) &&
        all(isfinite(result.Dy))
            ? 1u
            : 0u;
    return result;
}

bool ResolveParametricScreenDerivatives(
    SurfaceEval evaluation,
    SurfaceReconstructionInput input,
    out float3 barycentricDx,
    out float3 barycentricDy)
{
    float4 clip = mul(float4(evaluation.Position + input.WorldTranslation, 1.0), input.ViewProjection);
    float4 clipDu = mul(float4(evaluation.DpDu, 0.0), input.ViewProjection);
    float4 clipDv = mul(float4(evaluation.DpDv, 0.0), input.ViewProjection);
    float inverseW2 = abs(clip.w) > 1e-8 ? rcp(clip.w * clip.w) : 0.0;
    float2 ndcDu = (clipDu.xy * clip.w - clip.xy * clipDu.w) * inverseW2;
    float2 ndcDv = (clipDv.xy * clip.w - clip.xy * clipDv.w) * inverseW2;
    float2 screenScale = float2(
        0.5 * float(input.ScreenSize.x),
        -0.5 * float(input.ScreenSize.y));
    float2 pixelDu = ndcDu * screenScale;
    float2 pixelDv = ndcDv * screenScale;
    float determinant = pixelDu.x * pixelDv.y - pixelDv.x * pixelDu.y;
    float inverseDeterminant = abs(determinant) > 1e-10 ? rcp(determinant) : 0.0;
    float2 parameterDx = float2(pixelDv.y, -pixelDu.y) * inverseDeterminant;
    float2 parameterDy = float2(-pixelDv.x, pixelDu.x) * inverseDeterminant;
    barycentricDx = float3(
        -parameterDx.x - parameterDx.y,
        parameterDx.x,
        parameterDx.y);
    barycentricDy = float3(
        -parameterDy.x - parameterDy.y,
        parameterDy.x,
        parameterDy.y);
    return abs(determinant) > 1e-10;
}

#endif
