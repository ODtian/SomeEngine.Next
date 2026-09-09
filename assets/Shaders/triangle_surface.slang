#ifndef TRIANGLE_SURFACE_SLANG
#define TRIANGLE_SURFACE_SLANG

struct SurfaceVertex
{
    float3 Position;
    float3 Normal;
    float4 Tangent;
    float2 TexCoord;
};

struct Triangle
{
    float3 Position0;
    float3 Position1;
    float3 Position2;
    float3 Normal0;
    float3 Normal1;
    float3 Normal2;
    float4 Tangent0;
    float4 Tangent1;
    float4 Tangent2;
    float2 TexCoord0;
    float2 TexCoord1;
    float2 TexCoord2;
};

struct SurfaceEval
{
    float3 Position;
    float3 Normal;
    float4 Tangent;
    float2 TexCoord;
    float3 DpDu;
    float3 DpDv;
};

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
    float4x4 ViewProjection;
    uint2 ScreenSize;
};

// Conservative geometric approximation error for one triangular domain patch.
// Edge components correspond to (bary0,bary1), (bary1,bary2), and (bary2,bary0).
// Interior bounds the complete patch relative to the triangle formed by its three
// evaluated corner positions. Cluster owns the screen-space tolerance and split policy.
struct DomainErrorBounds
{
    float3 Edge;
    float Interior;
};

interface IDomainEvaluate
{
    float3 position(Triangle triangle, float3 barycentric);
    SurfaceEval eval(Triangle triangle, float3 barycentric);
    SurfaceBarycentrics reconstruct(
        Triangle triangle,
        SurfaceReconstructionInput input);
    void bounds(
        Triangle triangle,
        out float3 minimum,
        out float3 maximum);
    void patchBounds(
        Triangle triangle,
        float3 bary0,
        float3 bary1,
        float3 bary2,
        out float3 minimum,
        out float3 maximum);
    DomainErrorBounds errorBounds(
        Triangle triangle,
        float3 bary0,
        float3 bary1,
        float3 bary2,
        float3 position0,
        float3 position1,
        float3 position2);
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

float3 SafeNormalize(float3 value, float3 fallback)
{
    float lengthSquared = dot(value, value);
    return lengthSquared > 1e-12 ? value * rsqrt(lengthSquared) : fallback;
}

SurfaceVertex Corner(Triangle triangle, uint index)
{
    SurfaceVertex result;
    if (index == 0u)
    {
        result.Position = triangle.Position0;
        result.Normal = triangle.Normal0;
        result.Tangent = triangle.Tangent0;
        result.TexCoord = triangle.TexCoord0;
    }
    else if (index == 1u)
    {
        result.Position = triangle.Position1;
        result.Normal = triangle.Normal1;
        result.Tangent = triangle.Tangent1;
        result.TexCoord = triangle.TexCoord1;
    }
    else
    {
        result.Position = triangle.Position2;
        result.Normal = triangle.Normal2;
        result.Tangent = triangle.Tangent2;
        result.TexCoord = triangle.TexCoord2;
    }
    return result;
}

Triangle MakeTriangle(
    SurfaceVertex first,
    SurfaceVertex second,
    SurfaceVertex third)
{
    Triangle result;
    result.Position0 = first.Position;
    result.Position1 = second.Position;
    result.Position2 = third.Position;
    result.Normal0 = first.Normal;
    result.Normal1 = second.Normal;
    result.Normal2 = third.Normal;
    result.Tangent0 = first.Tangent;
    result.Tangent1 = second.Tangent;
    result.Tangent2 = third.Tangent;
    result.TexCoord0 = first.TexCoord;
    result.TexCoord1 = second.TexCoord;
    result.TexCoord2 = third.TexCoord;
    return result;
}

Triangle ReorderTriangle(Triangle triangle, uint3 order)
{
    return MakeTriangle(
        Corner(triangle, order.x),
        Corner(triangle, order.y),
        Corner(triangle, order.z));
}

SurfaceEval EvalLinearSurface(Triangle triangle, float3 barycentric)
{
    SurfaceEval result;
    result.Position =
        triangle.Position0 * barycentric.x +
        triangle.Position1 * barycentric.y +
        triangle.Position2 * barycentric.z;
    result.Normal = SafeNormalize(
        triangle.Normal0 * barycentric.x +
        triangle.Normal1 * barycentric.y +
        triangle.Normal2 * barycentric.z,
        float3(0.0, 0.0, 1.0));
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
    result.DpDu = triangle.Position1 - triangle.Position0;
    result.DpDv = triangle.Position2 - triangle.Position0;
    return result;
}

float3 EvalLinearPosition(Triangle triangle, float3 barycentric)
{
    return
        triangle.Position0 * barycentric.x +
        triangle.Position1 * barycentric.y +
        triangle.Position2 * barycentric.z;
}

struct LinearSurface : IDomainEvaluate
{
    float3 position(Triangle triangle, float3 barycentric)
    {
        return EvalLinearPosition(triangle, barycentric);
    }

    SurfaceEval eval(Triangle triangle, float3 barycentric)
    {
        return EvalLinearSurface(triangle, barycentric);
    }

    SurfaceBarycentrics reconstruct(
        Triangle triangle,
        SurfaceReconstructionInput input)
    {
        SurfaceBarycentrics result;
        result.Value = ClampBarycentric(input.ParentValue);
        result.Dx = input.ParentDx;
        result.Dy = input.ParentDy;
        result.Residual = length(
            EvalLinearPosition(triangle, result.Value) - input.TargetPosition);
        result.Valid = all(isfinite(result.Value)) ? 1u : 0u;
        return result;
    }

    void bounds(
        Triangle triangle,
        out float3 minimum,
        out float3 maximum)
    {
        minimum = min(
            triangle.Position0,
            min(triangle.Position1, triangle.Position2));
        maximum = max(
            triangle.Position0,
            max(triangle.Position1, triangle.Position2));
    }

    void patchBounds(
        Triangle triangle,
        float3 bary0,
        float3 bary1,
        float3 bary2,
        out float3 minimum,
        out float3 maximum)
    {
        float3 p0 = EvalLinearPosition(triangle, bary0);
        float3 p1 = EvalLinearPosition(triangle, bary1);
        float3 p2 = EvalLinearPosition(triangle, bary2);
        minimum = min(p0, min(p1, p2));
        maximum = max(p0, max(p1, p2));
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
        DomainErrorBounds result;
        result.Edge = 0.0.xxx;
        result.Interior = 0.0;
        return result;
    }
};

LinearSurface MakeLinearSurface()
{
    LinearSurface result = {};
    return result;
}

#endif
