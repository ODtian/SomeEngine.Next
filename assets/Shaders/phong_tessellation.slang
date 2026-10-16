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

float BoundQuadraticTriangle(float3 controlError)
{
    // The norm is bounded by 2*(a*u*v + b*v*w + c*w*u), u+v+w=1.
    // Its boundary maxima are a/2, b/2, c/2. A positive interior stationary
    // point exists only when a,b,c satisfy the strict triangle inequalities.
    float scale = max(controlError.x, max(controlError.y, controlError.z));
    if (scale <= 1e-12)
        return scale;
    float3 d = controlError / scale;
    float maximum = 0.5;
    if (all(d < (d.yzx + d.zxy)))
    {
        float denominator = 2.0 * (d.x * d.y + d.y * d.z + d.z * d.x) - dot(d, d);
        maximum = max(maximum, 2.0 * d.x * d.y * d.z / max(denominator, 1e-12));
    }
    return min(scale, maximum * scale * 1.00001 + 1e-7);
}

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

float3 EvalPhongEdgePosition(
    float3 position0, float3 position1,
    float3 normal0, float3 normal1, float weight)
{
    // Adjacent source triangles evaluate the same edge in the same order.
    if (position0.x > position1.x ||
        (position0.x == position1.x && (position0.y > position1.y ||
        (position0.y == position1.y && position0.z > position1.z))))
    {
        float3 position = position0; position0 = position1; position1 = position;
        float3 normal = normal0; normal0 = normal1; normal1 = normal;
        weight = 1.0 - weight;
    }
    normal0 = SafeNormalize(normal0, float3(0.0, 0.0, 1.0));
    normal1 = SafeNormalize(normal1, normal0);
    precise float3 edge = position1 - position0;
    precise float3 correction = dot(edge, normal1) * normal1 - dot(edge, normal0) * normal0;
    precise float3 control = (position0 + position1 + correction) * 0.5;
    precise float firstWeight = 1.0 - weight;
    precise float3 position = position0 * (firstWeight * firstWeight) +
        position1 * (weight * weight) + control * (2.0 * firstWeight * weight);
    return position;
}

float3 EvalPhongPosition(
    Triangle triangle,
    PhongCoefficients coefficients,
    float3 barycentric)
{
    if (barycentric.x == 0.0)
        return EvalPhongEdgePosition(triangle.Position1, triangle.Position2,
            triangle.Normal1, triangle.Normal2, barycentric.z);
    if (barycentric.y == 0.0)
        return EvalPhongEdgePosition(triangle.Position0, triangle.Position2,
            triangle.Normal0, triangle.Normal2, barycentric.z);
    if (barycentric.z == 0.0)
        return EvalPhongEdgePosition(triangle.Position0, triangle.Position1,
            triangle.Normal0, triangle.Normal1, barycentric.y);
    float u = barycentric.y;
    float v = barycentric.z;
    return
        coefficients.Constant +
        coefficients.LinearU * u +
        coefficients.LinearV * v +
        coefficients.QuadraticUU * (u * u) +
        coefficients.QuadraticUV * (u * v) +
        coefficients.QuadraticVV * (v * v);
}

SurfaceEval EvalPhongSurface(
    Triangle triangle,
    PhongCoefficients coefficients,
    float3 barycentric)
{
    float u = barycentric.y;
    float v = barycentric.z;
    SurfaceEval result;
    result.Position = EvalPhongPosition(triangle, coefficients, barycentric);
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
    float4 tangent =
        triangle.Tangent0 * barycentric.x +
        triangle.Tangent1 * barycentric.y +
        triangle.Tangent2 * barycentric.z;
    result.Tangent = float4(
        SafeNormalize(tangent.xyz, float3(1.0, 0.0, 0.0)),
        tangent.w < 0.0 ? -1.0 : 1.0);
    result.TexCoord =
        triangle.TexCoord0 * barycentric.x +
        triangle.TexCoord1 * barycentric.y +
        triangle.TexCoord2 * barycentric.z;
    return result;
}

struct PhongTessellation : IDomainEvaluate<StandardVertex>
{
    typealias TriangleData = Triangle;
    typealias Surface = SurfaceEval;

    Triangle readLane(Triangle triangle, uint lane) { return ReadTriangleLane(triangle, lane); }

    Triangle makeTriangle(
        StandardVertex first,
        StandardVertex second,
        StandardVertex third)
    {
        return MakeStandardTriangle(first, second, third);
    }

    float3 cornerPosition(Triangle triangle, uint corner)
    {
        if (corner == 0u) return triangle.Position0;
        if (corner == 1u) return triangle.Position1;
        return triangle.Position2;
    }

    float3 position(Triangle triangle, float3 barycentric)
    {
        return EvalPhongPosition(triangle, BuildPhongCoefficients(triangle), barycentric);
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
            float4 clip = mul(float4(evaluation.Position + input.WorldTranslation, 1.0), input.ViewProjection);
            float2 pixel = (clip.xy / clip.w * float2(.5, -.5) + .5) * float2(input.ScreenSize);
            float3 barycentricDx, barycentricDy;
            ResolveParametricScreenDerivatives(evaluation, input, barycentricDx, barycentricDy);
            float2 residual = pixel - input.PixelCenter;
            float2 delta = barycentricDx.yz * residual.x + barycentricDy.yz * residual.y;
            float maximumStep = max(abs(delta.x), abs(delta.y));
            delta *= min(1.0, 0.25 / max(maximumStep, 1e-12));
            parameters -= delta;
        }

        float3 unresolved = Barycentric(parameters);
        // Raster coverage selects this source domain and its surface boundary.
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
            all(isfinite(evaluation.Position)) &&
            all(isfinite(evaluation.DpDu)) &&
            all(isfinite(evaluation.DpDv))
                ? 1u
                : 0u;
        return result;
    }

    void bounds(
        Triangle triangle,
        out float3 minimum,
        out float3 maximum)
    {
        patchBounds(
            triangle,
            float3(1.0, 0.0, 0.0),
            float3(0.0, 1.0, 0.0),
            float3(0.0, 0.0, 1.0),
            minimum,
            maximum);
    }

    void patchBounds(
        Triangle triangle,
        float3 bary0,
        float3 bary1,
        float3 bary2,
        out float3 minimum,
        out float3 maximum)
    {
        PhongCoefficients coefficients = BuildPhongCoefficients(triangle);
        float3 control[6];
        control[0] = EvalPhongPosition(triangle, coefficients, bary0);
        control[1] = EvalPhongPosition(triangle, coefficients, bary1);
        control[2] = EvalPhongPosition(triangle, coefficients, bary2);
        float3 midpoint01 = EvalPhongPosition(triangle, coefficients, (bary0 + bary1) * 0.5);
        float3 midpoint12 = EvalPhongPosition(triangle, coefficients, (bary1 + bary2) * 0.5);
        float3 midpoint20 = EvalPhongPosition(triangle, coefficients, (bary2 + bary0) * 0.5);
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

    DomainErrorBounds errorBounds(
        Triangle triangle,
        float3 bary0,
        float3 bary1,
        float3 bary2,
        float3 position0,
        float3 position1,
        float3 position2)
    {
        PhongCoefficients coefficients = BuildPhongCoefficients(triangle);
        float3 midpoint01 = EvalPhongPosition(triangle, coefficients, (bary0 + bary1) * 0.5);
        float3 midpoint12 = EvalPhongPosition(triangle, coefficients, (bary1 + bary2) * 0.5);
        float3 midpoint20 = EvalPhongPosition(triangle, coefficients, (bary2 + bary0) * 0.5);

        // For a quadratic Bezier edge, D = C - (P0 + P1) / 2 and the maximum
        // deviation from the endpoint chord is |D| / 2. Bound the positive
        // quadratic blend inside the triangle including its boundary maxima.
        float3 d01 = 2.0 * midpoint01 - position0 - position1;
        float3 d12 = 2.0 * midpoint12 - position1 - position2;
        float3 d20 = 2.0 * midpoint20 - position2 - position0;
        float3 controlError = float3(length(d01), length(d12), length(d20));

        DomainErrorBounds result;
        result.Edge = 0.5 * controlError;
        result.Interior = BoundQuadraticTriangle(controlError);
        return result;
    }
};

PhongTessellation MakePhongTessellation()
{
    PhongTessellation result = {};
    return result;
}

#endif
