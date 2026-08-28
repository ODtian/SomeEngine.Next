#ifndef TEXTURE_DISPLACEMENT_SLANG
#define TEXTURE_DISPLACEMENT_SLANG

#include "triangle_surface.slang"

struct TextureDisplacement : ITriangleSurface
{
    Texture2D<float> Map;
    SamplerState Sampler;
    float Scale;
    float Bias;

    float height(Triangle triangle, float3 barycentric)
    {
        float2 uv =
            triangle.TexCoord0 * barycentric.x +
            triangle.TexCoord1 * barycentric.y +
            triangle.TexCoord2 * barycentric.z;
        return Map.SampleLevel(Sampler, uv, 0.0) * Scale + Bias;
    }

    float3 position(Triangle triangle, float3 barycentric)
    {
        SurfaceEval linear = EvalLinearSurface(triangle, barycentric);
        return linear.Position + linear.Normal * height(triangle, barycentric);
    }

    SurfaceEval eval(Triangle triangle, float3 barycentric)
    {
        SurfaceEval result = EvalLinearSurface(triangle, barycentric);
        float displacement = height(triangle, barycentric);
        result.Position += result.Normal * displacement;

        // The generic visibility inverse needs a differential of the authored
        // surface. Two one-sided samples keep this user implementation compact;
        // dicing calls position() and does not pay this cost.
        const float step = 1.0 / 1024.0;
        float3 baryU = Barycentric(barycentric.yz + float2(step, 0.0));
        float3 baryV = Barycentric(barycentric.yz + float2(0.0, step));
        result.DpDu = (position(triangle, baryU) - result.Position) / step;
        result.DpDv = (position(triangle, baryV) - result.Position) / step;
        return result;
    }

    void bounds(Triangle triangle, out float3 minimum, out float3 maximum)
    {
        LinearSurface linear = MakeLinearSurface();
        linear.bounds(triangle, minimum, maximum);

        float3 centerNormal = SafeNormalize(
            triangle.Normal0 + triangle.Normal1 + triangle.Normal2,
            triangle.Normal0);
        float coneCos = min(
            dot(centerNormal, triangle.Normal0),
            min(
                dot(centerNormal, triangle.Normal1),
                dot(centerNormal, triangle.Normal2)));
        float3 a = coneCos * abs(centerNormal);
        float3 b = sqrt(
            saturate(1.0 - coneCos * coneCos) *
            saturate(1.0 - centerNormal * centerNormal));
        float3 capMin = a - b;
        float3 capMax = a + b;
        capMax.x = abs(centerNormal.x) >= coneCos ? 1.0 : capMax.x;
        capMax.y = abs(centerNormal.y) >= coneCos ? 1.0 : capMax.y;
        capMax.z = abs(centerNormal.z) >= coneCos ? 1.0 : capMax.z;

        float displacement0 = Bias;
        float displacement1 = Bias + Scale;
        float displacementMin = min(displacement0, displacement1);
        float displacementMax = max(displacement0, displacement1);
        float3 displacedMin =
            (displacementMin > 0.0 ? capMin : capMax) * displacementMin;
        float3 displacedMax =
            (displacementMax > 0.0 ? capMax : capMin) * displacementMax;
        minimum.x += centerNormal.x >= 0.0 ? displacedMin.x : -displacedMax.x;
        minimum.y += centerNormal.y >= 0.0 ? displacedMin.y : -displacedMax.y;
        minimum.z += centerNormal.z >= 0.0 ? displacedMin.z : -displacedMax.z;
        maximum.x += centerNormal.x >= 0.0 ? displacedMax.x : -displacedMin.x;
        maximum.y += centerNormal.y >= 0.0 ? displacedMax.y : -displacedMin.y;
        maximum.z += centerNormal.z >= 0.0 ? displacedMax.z : -displacedMin.z;
    }
};

TextureDisplacement MakeTextureDisplacement(
    Texture2D<float> map,
    SamplerState sampler,
    float scale,
    float bias)
{
    TextureDisplacement result;
    result.Map = map;
    result.Sampler = sampler;
    result.Scale = scale;
    result.Bias = bias;
    return result;
}

#endif
