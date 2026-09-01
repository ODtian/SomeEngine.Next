#ifndef PHONG_TESSELLATION_SLANG
#define PHONG_TESSELLATION_SLANG

#include "triangle_surface_inverse.slang"

struct PhongCoefficients
{
    float3 Constant;
    float3 LinearU;
    float3 LinearV;
    float3 QuadraticUU;
    float3 QuadraticUV;
    float3 QuadraticVV;
};

float3 ProjectPlane(float3 value, float3 point, float3 normal)
{
    return value - dot(value - point, normal) * normal;
}

float3 ProjectDirection(float3 value, float3 normal)
{
    return value - dot(value, normal) * normal;
}

PhongCoefficients BuildPhongCoefficients(Triangle triangle)
{
    float3 normal0 = SafeNormalize(triangle.Normal0, float3(0.0, 0.0, 1.0));
    float3 normal1 = SafeNormalize(triangle.Normal1, normal0);
    float3 normal2 = SafeNormalize(triangle.Normal2, normal0);
    float3 directionU = triangle.Position1 - triangle.Position0;
    float3 directionV = triangle.Position2 - triangle.Position0;

    float3 point0 = ProjectPlane(triangle.Position0, triangle.Position0, normal0);
    float3 point1 = ProjectPlane(triangle.Position0, triangle.Position1, normal1);
    float3 point2 = ProjectPlane(triangle.Position0, triangle.Position2, normal2);
    float3 directionU0 = ProjectDirection(directionU, normal0);
    float3 directionU1 = ProjectDirection(directionU, normal1);
    float3 directionU2 = ProjectDirection(directionU, normal2);
    float3 directionV0 = ProjectDirection(directionV, normal0);
    float3 directionV1 = ProjectDirection(directionV, normal1);
    float3 directionV2 = ProjectDirection(directionV, normal2);

    PhongCoefficients result;
    result.Constant = point0;
    result.LinearU = directionU0 + point1 - point0;
    result.LinearV = directionV0 + point2 - point0;
    result.QuadraticUU = directionU1 - directionU0;
    result.QuadraticUV = directionV1 - directionV0 + directionU2 - directionU0;
    result.QuadraticVV = directionV2 - directionV0;
    return result;
}

SurfaceEval EvalPhongSurface(
    Triangle triangle,
    PhongCoefficients coefficients,
    float3 barycentric)
{
    float u = barycentric.y;
    float v = barycentric.z;
    SurfaceEval result;
    result.Position =
        coefficients.Constant +
        coefficients.LinearU * u +
        coefficients.LinearV * v +
        coefficients.QuadraticUU * (u * u) +
        coefficients.QuadraticUV * (u * v) +
        coefficients.QuadraticVV * (v * v);
    result.DpDu =
        coefficients.LinearU +
        coefficients.QuadraticUU * (2.0 * u) +
        coefficients.QuadraticUV * v;
    result.DpDv =
        coefficients.LinearV +
        coefficients.QuadraticUV * u +
        coefficients.QuadraticVV * (2.0 * v);
    result.Normal = SafeNormalize(
        triangle.Normal0 * barycentric.x +
        triangle.Normal1 * barycentric.y +
        triangle.Normal2 * barycentric.z,
        SafeNormalize(cross(result.DpDu, result.DpDv), float3(0.0, 0.0, 1.0)));
    result.TexCoord =
        triangle.TexCoord0 * barycentric.x +
        triangle.TexCoord1 * barycentric.y +
        triangle.TexCoord2 * barycentric.z;
    return result;
}

struct PhongTessellation : ITriangleSurface
{
    float3 position(Triangle triangle, float3 barycentric)
    {
        return EvalPhongSurface(
            triangle,
            BuildPhongCoefficients(triangle),
            barycentric).Position;
    }

    SurfaceEval eval(Triangle triangle, float3 barycentric)
    {
        return EvalPhongSurface(
            triangle,
            BuildPhongCoefficients(triangle),
            barycentric);
    }

    SurfaceBarycentrics reconstruct(
        Triangle triangle,
        SurfaceReconstructionInput input)
    {
        PhongCoefficients coefficients = BuildPhongCoefficients(triangle);
        float2 parameters = input.ParentValue.yz;
        [unroll]
        for (uint iteration = 0u; iteration < 2u; iteration++)
        {
            SurfaceEval evaluation = EvalPhongSurface(
                triangle,
                coefficients,
                Barycentric(parameters));
            float3 residual = evaluation.Position - input.TargetPosition;
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
        SurfaceEval evaluation = EvalPhongSurface(triangle, coefficients, value);
        float3 barycentricDx;
        float3 barycentricDy;
        bool derivativesValid = ResolveParametricScreenDerivatives(
            evaluation,
            input,
            barycentricDx,
            barycentricDy);

        SurfaceBarycentrics result;
        result.Value = value;
        result.Dx = barycentricDx;
        result.Dy = barycentricDy;
        result.Residual = length(evaluation.Position - input.TargetPosition);
        result.Valid =
            derivativesValid &&
            SurfaceBarycentricViolation(unresolved) <= 1e-3 &&
            all(isfinite(evaluation.Position)) &&
            all(isfinite(evaluation.DpDu)) &&
            all(isfinite(evaluation.DpDv))
                ? 1u
                : 0u;
        return result;
    }

    void bounds(Triangle triangle, out float3 minimum, out float3 maximum)
    {
        PhongCoefficients coefficients = BuildPhongCoefficients(triangle);
        float3 control[6];
        control[0] = triangle.Position0;
        control[1] = triangle.Position1;
        control[2] = triangle.Position2;
        float3 midpoint01 = EvalPhongSurface(
            triangle, coefficients, float3(0.5, 0.5, 0.0)).Position;
        float3 midpoint12 = EvalPhongSurface(
            triangle, coefficients, float3(0.0, 0.5, 0.5)).Position;
        float3 midpoint20 = EvalPhongSurface(
            triangle, coefficients, float3(0.5, 0.0, 0.5)).Position;
        control[3] = 2.0 * midpoint01 - 0.5 * (control[0] + control[1]);
        control[4] = 2.0 * midpoint12 - 0.5 * (control[1] + control[2]);
        control[5] = 2.0 * midpoint20 - 0.5 * (control[2] + control[0]);
        minimum = control[0];
        maximum = control[0];
        [unroll]
        for (uint index = 1u; index < 6u; index++)
        {
            minimum = min(minimum, control[index]);
            maximum = max(maximum, control[index]);
        }
    }
};

PhongTessellation MakePhongTessellation()
{
    PhongTessellation result = {};
    return result;
}

#endif
