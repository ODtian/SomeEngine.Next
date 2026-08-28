#ifndef TRIANGLE_SURFACE_SLANG
#define TRIANGLE_SURFACE_SLANG

struct SurfaceVertex
{
    float3 Position;
    float3 Normal;
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
    float2 TexCoord0;
    float2 TexCoord1;
    float2 TexCoord2;
};

struct SurfaceEval
{
    float3 Position;
    float3 Normal;
    float2 TexCoord;
    float3 DpDu;
    float3 DpDv;
};

interface ITriangleSurface
{
    float3 position(Triangle triangle, float3 barycentric);
    SurfaceEval eval(Triangle triangle, float3 barycentric);
    void bounds(Triangle triangle, out float3 minimum, out float3 maximum);
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
        result.TexCoord = triangle.TexCoord0;
    }
    else if (index == 1u)
    {
        result.Position = triangle.Position1;
        result.Normal = triangle.Normal1;
        result.TexCoord = triangle.TexCoord1;
    }
    else
    {
        result.Position = triangle.Position2;
        result.Normal = triangle.Normal2;
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

struct LinearSurface : ITriangleSurface
{
    float3 position(Triangle triangle, float3 barycentric)
    {
        return EvalLinearPosition(triangle, barycentric);
    }

    SurfaceEval eval(Triangle triangle, float3 barycentric)
    {
        return EvalLinearSurface(triangle, barycentric);
    }

    void bounds(Triangle triangle, out float3 minimum, out float3 maximum)
    {
        minimum = min(
            triangle.Position0,
            min(triangle.Position1, triangle.Position2));
        maximum = max(
            triangle.Position0,
            max(triangle.Position1, triangle.Position2));
    }
};

LinearSurface MakeLinearSurface()
{
    LinearSurface result = {};
    return result;
}

#endif
