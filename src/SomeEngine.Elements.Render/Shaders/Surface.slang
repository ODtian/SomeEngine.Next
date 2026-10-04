struct CanvasData
{
    float2 Size;
    float Scale;
    float Padding;
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
    float4 rectangle = asfloat(Instances.Load4(instance * 80));
    float margin = 1 / Canvas.Scale;
    float2 position = rectangle.xy - margin + corners[vertex] * (rectangle.zw + 2 * margin);
    float2 pixels = position * Canvas.Scale;
    SurfaceVertex result;
    result.Position = float4(pixels.x * 2 / Canvas.Size.x - 1,
        1 - pixels.y * 2 / Canvas.Size.y, 0, 1);
    result.CanvasPosition = position;
    result.Instance = instance;
    return result;
}

[shader("fragment")]
float4 pixelMain(SurfaceVertex vertex) : SV_Target0
{
    uint start = vertex.Instance * 80;
    float4 rectangle = asfloat(Instances.Load4(start));
    float4 fill = asfloat(Instances.Load4(start + 16));
    float4 border = asfloat(Instances.Load4(start + 32));
    float4 clipRectangle = asfloat(Instances.Load4(start + 48));
    float2 shape = asfloat(Instances.Load2(start + 64));
    float2 position = vertex.CanvasPosition;
    if (any(position < clipRectangle.xy) || any(position >= clipRectangle.xy + clipRectangle.zw) ||
        any(rectangle.zw <= 0)) discard;

    float2 halfSize = rectangle.zw * 0.5;
    float radius = clamp(shape.x, 0, min(halfSize.x, halfSize.y));
    float2 corner = abs(position - rectangle.xy - halfSize) - halfSize + radius;
    float distance = length(max(corner, 0)) + min(max(corner.x, corner.y), 0) - radius;
    float outer = saturate(0.5 - distance * Canvas.Scale);
    float inner = saturate(0.5 - (distance + max(shape.y, 0)) * Canvas.Scale);
    float fillAlpha = fill.a * inner;
    float borderAlpha = border.a * (outer - inner);
    return float4(fill.rgb * fillAlpha + border.rgb * borderAlpha, fillAlpha + borderAlpha);
}
