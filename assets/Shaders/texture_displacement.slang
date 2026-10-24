#ifndef TEXTURE_DISPLACEMENT_SLANG
#define TEXTURE_DISPLACEMENT_SLANG

#include "triangle_surface_inverse.slang"

struct TextureDisplacement : IDomainEvaluate<StandardVertex>
{
    typealias TriangleData = Triangle;
    typealias Surface = SurfaceEval;

    Texture2D<float> Map;
    // Each texel stores the exact min/max source-map value for its footprint.
    // Mips form a conservative min/max hierarchy for domain-range queries.
    Texture2D<float2> BoundsMap;
    // Must be linear-repeat with no comparison or LOD bias. Position evaluation
    // uses the hardware filter; analytic derivatives use the continuous bilinear field.
    SamplerState LinearRepeatSampler;
    float2 SourceRange;
    float Scale;
    float Bias;

    Triangle makeTriangle(
        StandardVertex first,
        StandardVertex second,
        StandardVertex third,
        uint materialIndex)
    {
        return MakeStandardTriangle(first, second, third);
    }

    float3 cornerPosition(Triangle triangle, uint corner)
    {
        if (corner == 0u) return triangle.Position0;
        if (corner == 1u) return triangle.Position1;
        return triangle.Position2;
    }

    float2 uv(Triangle triangle, float3 barycentric)
    {
        return
            triangle.TexCoord0 * barycentric.x +
            triangle.TexCoord1 * barycentric.y +
            triangle.TexCoord2 * barycentric.z;
    }

    float2 footprintRange(
        Triangle triangle,
        float3 bary0,
        float3 bary1,
        float3 bary2)
    {
        float2 uv0 = uv(triangle, bary0);
        float2 uv1 = uv(triangle, bary1);
        float2 uv2 = uv(triangle, bary2);
        float2 minimumUv = min(uv0, min(uv1, uv2));
        float2 maximumUv = max(uv0, max(uv1, uv2));
        float2 sourceRange = float2(
            min(SourceRange.x, SourceRange.y),
            max(SourceRange.x, SourceRange.y));

        // The hierarchy covers one normalized texture domain. Arbitrary addressing is
        // still supported conservatively by falling back to the complete source range.
        if (any(minimumUv < 0.0.xx) || any(maximumUv > 1.0.xx))
            return sourceRange;

        uint width;
        uint height;
        uint levelCount;
        BoundsMap.GetDimensions(0u, width, height, levelCount);
        width = max(width, 1u);
        height = max(height, 1u);
        levelCount = max(levelCount, 1u);

        // Include the level-0 filtering footprint before choosing a hierarchy level.
        float2 texel = 1.0 / float2(width, height);
        if (any(minimumUv < texel) || any(maximumUv > 1.0.xx - texel))
            return sourceRange;
        minimumUv = saturate(minimumUv - texel);
        maximumUv = saturate(maximumUv + texel);
        float2 sourceSpan = (maximumUv - minimumUv) * float2(width, height);
        float maximumSpan = max(sourceSpan.x, sourceSpan.y);
        uint level = min(
            uint(ceil(log2(max(maximumSpan, 1.0)))),
            levelCount - 1u);

        uint levelWidth;
        uint levelHeight;
        uint ignoredLevelCount;
        BoundsMap.GetDimensions(level, levelWidth, levelHeight, ignoredLevelCount);
        levelWidth = max(levelWidth, 1u);
        levelHeight = max(levelHeight, 1u);
        uint2 maximumCoordinate = uint2(levelWidth - 1u, levelHeight - 1u);
        uint2 minimumCoordinate = min(
            uint2(floor(minimumUv * float2(levelWidth, levelHeight))),
            maximumCoordinate);
        uint2 maximumCoordinateForPatch = min(
            uint2(floor(maximumUv * float2(levelWidth, levelHeight))),
            maximumCoordinate);

        float2 result = float2(1e30, -1e30);
        [unroll]
        for (uint y = 0u; y < 2u; y++)
        {
            [unroll]
            for (uint x = 0u; x < 2u; x++)
            {
                uint2 coordinate = min(
                    minimumCoordinate + uint2(x, y),
                    maximumCoordinateForPatch);
                float2 range = BoundsMap.Load(int3(coordinate, level));
                result.x = min(result.x, range.x);
                result.y = max(result.y, range.y);
            }
        }
        return result;
    }

    float4 texels(float2 texCoord, out float2 fraction, out float2 dimensions)
    {
        uint width, heightValue, levelCount;
        Map.GetDimensions(0u, width, heightValue, levelCount);
        int2 size = int2(max(width, 1u), max(heightValue, 1u));
        dimensions = float2(size);
        float2 location = frac(texCoord) * dimensions - 0.5;
        int2 first = int2(floor(location));
        fraction = frac(location);
        // frac has already reduced the coordinate to one repeat. Each first
        // index is in [-1, size-1], so wrapping needs one boundary selection,
        // not integer division. This also covers size one and non-power-of-two maps.
        int2 p0 = int2(first.x < 0 ? first.x + size.x : first.x,
            first.y < 0 ? first.y + size.y : first.y);
        int2 p1 = int2(p0.x + 1 == size.x ? 0 : p0.x + 1,
            p0.y + 1 == size.y ? 0 : p0.y + 1);
        return float4(Map.Load(int3(p0, 0)), Map.Load(int3(p1.x, p0.y, 0)),
            Map.Load(int3(p0.x, p1.y, 0)), Map.Load(int3(p1, 0)));
    }

    float3 sample(float2 texCoord)
    {
        float2 fraction, dimensions;
        float4 values = texels(texCoord, fraction, dimensions);
        float value = lerp(lerp(values.x, values.y, fraction.x),
            lerp(values.z, values.w, fraction.x), fraction.y);
        float2 gradient = float2(lerp(values.y - values.x, values.w - values.z, fraction.y),
            lerp(values.z - values.x, values.w - values.y, fraction.x)) * dimensions;
        return float3(value * Scale + Bias, gradient * Scale);
    }

    // The API permits quantized filter weights. Include their error in the
    // geometric bounds rather than treating hardware filtering as exact arithmetic.
    float filterError()
    {
        float weightError;
        __target_switch
        {
        case hlsl: weightError = 1.0 / 256.0; break;
        default: weightError = 1.0 / 16.0; break;
        }
        return abs(SourceRange.y - SourceRange.x) * (2.0 * weightError) +
            max(abs(SourceRange.x), abs(SourceRange.y)) * 1e-6;
    }

    float2 rawRange(Triangle triangle, float3 bary0, float3 bary1, float3 bary2)
    {
        float2 range = footprintRange(triangle, bary0, bary1, bary2);
        float2 uv0 = uv(triangle, bary0), uv1 = uv(triangle, bary1), uv2 = uv(triangle, bary2);
        float2 minimum = min(uv0, min(uv1, uv2)), maximum = max(uv0, max(uv1, uv2));
        float2 fraction, dimensions;
        float4 values = texels((minimum + maximum) * 0.5, fraction, dimensions);
        float center = lerp(lerp(values.x, values.y, fraction.x),
            lerp(values.z, values.w, fraction.x), fraction.y);
        // Each bilinear derivative is bounded by the sample range times the texture
        // dimension. Intersecting this Lipschitz interval makes even cell-crossing
        // patches converge as their UV footprint shrinks, including repeat seams.
        float radius = (range.y - range.x) * dot((maximum - minimum) * 0.5, dimensions) + filterError();
        return float2(max(range.x, center - radius), min(range.y, center + radius));
    }

    float2 displacementRange(
        Triangle triangle,
        float3 bary0,
        float3 bary1,
        float3 bary2)
    {
        float2 range = rawRange(triangle, bary0, bary1, bary2);
        float first = range.x * Scale + Bias;
        float second = range.y * Scale + Bias;
        return float2(min(first, second), max(first, second));
    }

    float height(Triangle triangle, float3 barycentric)
    {
        return Map.SampleLevel(LinearRepeatSampler, frac(uv(triangle, barycentric)), 0.0) * Scale + Bias;
    }

    float3 position(Triangle triangle, float3 barycentric)
    {
        SurfaceEval linear = EvalLinearSurface(triangle, barycentric);
        return linear.Position + linear.Normal * height(triangle, barycentric);
    }

    SurfaceEval eval(Triangle triangle, float3 barycentric)
    {
        SurfaceEval result = EvalLinearSurface(triangle, barycentric);
        float3 heightSample = sample(result.TexCoord);
        float displacement = height(triangle, barycentric);
        result.Position += result.Normal * displacement;

        float3 rawNormal =
            triangle.Normal0 * barycentric.x +
            triangle.Normal1 * barycentric.y +
            triangle.Normal2 * barycentric.z;
        float rawNormalLengthSquared = dot(rawNormal, rawNormal);
        float3 dRawNormalDu = triangle.Normal1 - triangle.Normal0;
        float3 dRawNormalDv = triangle.Normal2 - triangle.Normal0;
        float3 dNormalDu = 0.0.xxx;
        float3 dNormalDv = 0.0.xxx;
        if (rawNormalLengthSquared > 1e-12)
        {
            float inverseLength = rsqrt(rawNormalLengthSquared);
            float3 unitNormal = rawNormal * inverseLength;
            dNormalDu =
                (dRawNormalDu - unitNormal * dot(unitNormal, dRawNormalDu)) *
                inverseLength;
            dNormalDv =
                (dRawNormalDv - unitNormal * dot(unitNormal, dRawNormalDv)) *
                inverseLength;
        }

        float2 gradient = heightSample.yz;
        float2 texCoordDu = triangle.TexCoord1 - triangle.TexCoord0;
        float2 texCoordDv = triangle.TexCoord2 - triangle.TexCoord0;
        float heightDu = dot(gradient, texCoordDu);
        float heightDv = dot(gradient, texCoordDv);
        result.DpDu += dNormalDu * displacement + result.Normal * heightDu;
        result.DpDv += dNormalDv * displacement + result.Normal * heightDv;
        result.Normal = SafeNormalize(
            cross(result.DpDu, result.DpDv),
            result.Normal);
        return result;
    }

    SurfaceBarycentrics reconstruct(
        Triangle triangle,
        SurfaceReconstructionInput input)
    {
        // The final position already contains the sampled displacement. Recover the
        // original-triangle coordinates by solving an effective scalar displacement
        // along the interpolated normal; no displacement texture sample is required.
        return ReconstructNormalDisplacementSurface(triangle, input);
    }

    void displacementVectorBounds(
        Triangle triangle,
        float3 bary0,
        float3 bary1,
        float3 bary2,
        out float3 minimum,
        out float3 maximum)
    {
        float3 raw0 =
            triangle.Normal0 * bary0.x +
            triangle.Normal1 * bary0.y +
            triangle.Normal2 * bary0.z;
        float3 raw1 =
            triangle.Normal0 * bary1.x +
            triangle.Normal1 * bary1.y +
            triangle.Normal2 * bary1.z;
        float3 raw2 =
            triangle.Normal0 * bary2.x +
            triangle.Normal1 * bary2.y +
            triangle.Normal2 * bary2.z;
        float3 rawMinimum = min(raw0, min(raw1, raw2));
        float3 rawMaximum = max(raw0, max(raw1, raw2));

        // Raw normal interpolation is affine, so component extrema over the patch
        // occur at its three corners. The distance from the component AABB to zero is
        // a conservative lower bound on |rawNormal|; triangle inequality makes the
        // largest corner length a conservative upper bound. These two scalars turn the
        // raw component intervals into proven component intervals for normalize(raw).
        float3 distanceToZero = float3(
            rawMinimum.x > 0.0 ? rawMinimum.x :
                (rawMaximum.x < 0.0 ? -rawMaximum.x : 0.0),
            rawMinimum.y > 0.0 ? rawMinimum.y :
                (rawMaximum.y < 0.0 ? -rawMaximum.y : 0.0),
            rawMinimum.z > 0.0 ? rawMinimum.z :
                (rawMaximum.z < 0.0 ? -rawMaximum.z : 0.0));
        float minimumLength = length(distanceToZero);
        float maximumLength = max(length(raw0), max(length(raw1), length(raw2)));

        float3 normalMinimum;
        float3 normalMaximum;
        if (minimumLength <= 1e-8 || maximumLength <= 1e-8)
        {
            // The normalized field can become arbitrarily sensitive when the affine raw
            // normal region approaches zero. Full component range is the safe answer.
            normalMinimum = -1.0.xxx;
            normalMaximum = 1.0.xxx;
        }
        else
        {
            normalMinimum.x = rawMinimum.x >= 0.0
                ? rawMinimum.x / maximumLength
                : rawMinimum.x / minimumLength;
            normalMinimum.y = rawMinimum.y >= 0.0
                ? rawMinimum.y / maximumLength
                : rawMinimum.y / minimumLength;
            normalMinimum.z = rawMinimum.z >= 0.0
                ? rawMinimum.z / maximumLength
                : rawMinimum.z / minimumLength;
            normalMaximum.x = rawMaximum.x <= 0.0
                ? rawMaximum.x / maximumLength
                : rawMaximum.x / minimumLength;
            normalMaximum.y = rawMaximum.y <= 0.0
                ? rawMaximum.y / maximumLength
                : rawMaximum.y / minimumLength;
            normalMaximum.z = rawMaximum.z <= 0.0
                ? rawMaximum.z / maximumLength
                : rawMaximum.z / minimumLength;
            normalMinimum = clamp(normalMinimum, -1.0.xxx, 1.0.xxx);
            normalMaximum = clamp(normalMaximum, -1.0.xxx, 1.0.xxx);
        }

        float2 range = displacementRange(triangle, bary0, bary1, bary2);
        float3 p00 = normalMinimum * range.x;
        float3 p01 = normalMinimum * range.y;
        float3 p10 = normalMaximum * range.x;
        float3 p11 = normalMaximum * range.y;
        minimum = min(min(p00, p01), min(p10, p11));
        maximum = max(max(p00, p01), max(p10, p11));
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
        float3 p0 = EvalLinearPosition(triangle, bary0);
        float3 p1 = EvalLinearPosition(triangle, bary1);
        float3 p2 = EvalLinearPosition(triangle, bary2);
        minimum = min(p0, min(p1, p2));
        maximum = max(p0, max(p1, p2));
        float3 displacementMinimum;
        float3 displacementMaximum;
        displacementVectorBounds(
            triangle,
            bary0,
            bary1,
            bary2,
            displacementMinimum,
            displacementMaximum);
        minimum += displacementMinimum;
        maximum += displacementMaximum;
    }

    float regionErrorBound(
        Triangle triangle,
        float3 bary0,
        float3 bary1,
        float3 bary2,
        float3 displacement0,
        float3 displacement1,
        float3 displacement2)
    {
        float3 displacementMinimum;
        float3 displacementMaximum;
        displacementVectorBounds(
            triangle,
            bary0,
            bary1,
            bary2,
            displacementMinimum,
            displacementMaximum);
        float3 linearMinimum = min(
            displacement0,
            min(displacement1, displacement2));
        float3 linearMaximum = max(
            displacement0,
            max(displacement1, displacement2));
        float3 errorExtent = max(
            abs(displacementMinimum - linearMaximum),
            abs(displacementMaximum - linearMinimum));
        float intervalError = length(errorExtent);

        float3 raw0 = triangle.Normal0 * bary0.x + triangle.Normal1 * bary0.y + triangle.Normal2 * bary0.z;
        float3 raw1 = triangle.Normal0 * bary1.x + triangle.Normal1 * bary1.y + triangle.Normal2 * bary1.z;
        float3 raw2 = triangle.Normal0 * bary2.x + triangle.Normal1 * bary2.y + triangle.Normal2 * bary2.z;
        float3 rawMinimum = min(raw0, min(raw1, raw2)), rawMaximum = max(raw0, max(raw1, raw2));
        float minimumLength = length(max(max(rawMinimum, -rawMaximum), 0.0.xxx));
        if (minimumLength <= 1e-6)
            return intervalError;

        float2 uv0 = uv(triangle, bary0), uv1 = uv(triangle, bary1), uv2 = uv(triangle, bary2);
        float2 minimumUv = min(uv0, min(uv1, uv2)), maximumUv = max(uv0, max(uv1, uv2));
        float2 range = displacementRange(triangle, bary0, bary1, bary2);
        float heightError = range.y - range.x;
        float2 fraction, dimensions;
        float4 values = texels((minimumUv + maximumUv) * 0.5, fraction, dimensions);
        if (all(floor(minimumUv * dimensions - 0.5) == floor(maximumUv * dimensions - 0.5)))
        {
            // Inside one bilinear cell the affine terms interpolate without error.
            // Only the uv cross term remains; its covariance is at most du*dv/4.
            float2 span = (maximumUv - minimumUv) * dimensions;
            heightError = min(heightError, abs((values.x - values.y - values.z + values.w) * Scale) * span.x * span.y * 0.25);
        }
        float rawDiameter = max(length(raw0 - raw1), max(length(raw1 - raw2), length(raw2 - raw0)));
        float normalDiameter = min(2.0, rawDiameter / minimumLength);
        // The second directional derivative of normalize(r) is bounded by
        // 3*|dr|^2/min|r|^2. Taylor interpolation therefore contributes at most
        // 1.5*diameter^2/min|r|^2. The product's corner covariance contributes
        // range(h)*diameter(n)/3 because sum(i<j, lambda_i*lambda_j) <= 1/3.
        float normalError = min(2.0, 1.5 * rawDiameter * rawDiameter / (minimumLength * minimumLength));
        float interpolationError = max(abs(range.x), abs(range.y)) * normalError + heightError +
            (range.y - range.x) * normalDiameter / 3.0;
        return min(intervalError, interpolationError + 2.0 * abs(Scale) * filterError() + 1e-6);
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
        float3 base0 = EvalLinearPosition(triangle, bary0);
        float3 base1 = EvalLinearPosition(triangle, bary1);
        float3 base2 = EvalLinearPosition(triangle, bary2);
        float3 displacement0 = position0 - base0;
        float3 displacement1 = position1 - base1;
        float3 displacement2 = position2 - base2;

        DomainErrorBounds result;
        result.Edge.x = regionErrorBound(
            triangle,
            bary0,
            bary1,
            bary0,
            displacement0,
            displacement1,
            displacement0);
        result.Edge.y = regionErrorBound(
            triangle,
            bary1,
            bary2,
            bary1,
            displacement1,
            displacement2,
            displacement1);
        result.Edge.z = regionErrorBound(
            triangle,
            bary2,
            bary0,
            bary2,
            displacement2,
            displacement0,
            displacement2);
        result.Interior = regionErrorBound(
            triangle,
            bary0,
            bary1,
            bary2,
            displacement0,
            displacement1,
            displacement2);
        return result;
    }
};

TextureDisplacement MakeTextureDisplacement(
    Texture2D<float> map,
    Texture2D<float2> boundsMap,
    SamplerState linearRepeatSampler,
    float2 sourceRange,
    float scale,
    float bias)
{
    TextureDisplacement result;
    result.Map = map;
    result.BoundsMap = boundsMap;
    result.LinearRepeatSampler = linearRepeatSampler;
    result.SourceRange = sourceRange;
    result.Scale = scale;
    result.Bias = bias;
    return result;
}

#endif
