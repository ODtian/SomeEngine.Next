struct CanvasData
{
    float2 Size;
    float Scale;
    float Padding;
    float4x4 ClipTransform;
};

ByteAddressBuffer Instances;
ConstantBuffer<CanvasData> Canvas;

struct SurfaceVertex
{
    float4 Position : SV_Position;
    float2 CanvasPosition : TEXCOORD0;
    nointerpolation uint Instance : TEXCOORD1;
};

[shader("vertex")]
SurfaceVertex vertexMain(uint vertex : SV_VertexID, uint instance : SV_InstanceID)
{
    const float2 corners[6] = { float2(0, 0), float2(1, 0), float2(0, 1),
        float2(0, 1), float2(1, 0), float2(1, 1) };
    uint start = instance * 112;
    float4 rectangle = asfloat(Instances.Load4(start));
    float4 shadow = asfloat(Instances.Load4(start + 80));
    float3 shadowShape = asfloat(Instances.Load3(start + 96));
    float margin = 1 / Canvas.Scale;
    float2 minimum = rectangle.xy - margin;
    float2 maximum = rectangle.xy + rectangle.zw + margin;
    if (shadow.a > 0)
    {
        float blur = max(shadowShape.z, 0);
        minimum = min(minimum, rectangle.xy + shadowShape.xy - blur - margin);
        maximum = max(maximum, rectangle.xy + rectangle.zw + shadowShape.xy + blur + margin);
    }
    float2 position = lerp(minimum, maximum, corners[vertex]);
    SurfaceVertex result;
    result.Position = mul(float4(position, 0, 1), Canvas.ClipTransform);
    result.CanvasPosition = position;
    result.Instance = instance;
    return result;
}

float measureRectangleDistance(float2 position, float4 rectangle, float radius)
{
    float2 halfSize = rectangle.zw * 0.5;
    float2 corner = abs(position - rectangle.xy - halfSize) - halfSize + radius;
    return length(max(corner, 0)) + min(max(corner.x, corner.y), 0) - radius;
}

[shader("fragment")]
float4 pixelMain(SurfaceVertex vertex) : SV_Target0
{
    uint start = vertex.Instance * 112;
    float4 rectangle = asfloat(Instances.Load4(start));
    float4 fill = asfloat(Instances.Load4(start + 16));
    float4 border = asfloat(Instances.Load4(start + 32));
    float4 clipRectangle = asfloat(Instances.Load4(start + 48));
    float2 shape = asfloat(Instances.Load2(start + 64));
    float4 shadow = asfloat(Instances.Load4(start + 80));
    float3 shadowShape = asfloat(Instances.Load3(start + 96));
    float2 position = vertex.CanvasPosition;
    if (any(position < clipRectangle.xy) || any(position >= clipRectangle.xy + clipRectangle.zw) ||
        any(rectangle.zw <= 0)) discard;

    float2 halfSize = rectangle.zw * 0.5;
    float radius = clamp(shape.x, 0, min(halfSize.x, halfSize.y));
    float distance = measureRectangleDistance(position, rectangle, radius);
    float pixelWidth = max(fwidth(distance), 0.000001);
    float outer = saturate(0.5 - distance / pixelWidth);
    float inner = saturate(0.5 - (distance + max(shape.y, 0)) / pixelWidth);
    float fillAlpha = fill.a * inner;
    float borderAlpha = border.a * (outer - inner);
    float surfaceAlpha = fillAlpha + borderAlpha;
    float shadowAlpha = 0;
    if (shadow.a > 0)
    {
        float shadowDistance = measureRectangleDistance(position - shadowShape.xy, rectangle, radius);
        float shadowPixelWidth = max(fwidth(shadowDistance), 0.000001);
        float coverage = shadowShape.z > 0
            ? 1 - smoothstep(-shadowShape.z, shadowShape.z, shadowDistance)
            : saturate(0.5 - shadowDistance / shadowPixelWidth);
        shadowAlpha = shadow.a * coverage * (1 - surfaceAlpha);
    }
    return float4(fill.rgb * fillAlpha + border.rgb * borderAlpha + shadow.rgb * shadowAlpha,
        surfaceAlpha + shadowAlpha);
}
