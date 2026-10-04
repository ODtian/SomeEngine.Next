struct CanvasData
{
    float2 Size;
    float Scale;
    float Padding;
};

ByteAddressBuffer Instances;
ConstantBuffer<CanvasData> Canvas;
Texture2D<float4> ImageTexture;
SamplerState ImageSampler;

struct ImageVertex
{
    float4 Position : SV_Position;
    float2 CanvasPosition : TEXCOORD0;
    float2 Uv : TEXCOORD1;
    nointerpolation uint Instance : TEXCOORD2;
};

[shader("vertex")]
ImageVertex vertexMain(uint vertex : SV_VertexID, uint instance : SV_InstanceID)
{
    const float2 corners[6] = { float2(0, 0), float2(1, 0), float2(0, 1),
        float2(0, 1), float2(1, 0), float2(1, 1) };
    float4 rectangle = asfloat(Instances.Load4(instance * 48));
    float4 uv = asfloat(Instances.Load4(instance * 48 + 16));
    float2 corner = corners[vertex];
    float2 position = rectangle.xy + corner * rectangle.zw;
    float2 pixels = position * Canvas.Scale;
    ImageVertex result;
    result.Position = float4(pixels.x * 2 / Canvas.Size.x - 1,
        1 - pixels.y * 2 / Canvas.Size.y, 0, 1);
    result.CanvasPosition = position;
    result.Uv = uv.xy + corner * uv.zw;
    result.Instance = instance;
    return result;
}

[shader("fragment")]
float4 pixelMain(ImageVertex vertex) : SV_Target0
{
    float4 clipRectangle = asfloat(Instances.Load4(vertex.Instance * 48 + 32));
    if (any(vertex.CanvasPosition < clipRectangle.xy) ||
        any(vertex.CanvasPosition >= clipRectangle.xy + clipRectangle.zw)) discard;
    float4 color = ImageTexture.Sample(ImageSampler, vertex.Uv);
    return float4(color.rgb * color.a, color.a);
}
