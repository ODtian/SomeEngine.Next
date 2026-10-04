struct CanvasData
{
    float2 Size;
    float Scale;
    float Padding;
    float4x4 ClipTransform;
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
    uint start = instance * 80;
    float4 rectangle = asfloat(Instances.Load4(start));
    float4 uv = asfloat(Instances.Load4(start + 16));
    float4 matrix = asfloat(Instances.Load4(start + 48));
    float2 translation = asfloat(Instances.Load2(start + 64));
    float2 corner = corners[vertex];
    float2 position = rectangle.xy + corner * rectangle.zw;
    float2 presented = position.x * matrix.xy + position.y * matrix.zw + translation;
    ImageVertex result;
    result.Position = mul(float4(presented, 0, 1), Canvas.ClipTransform);
    result.CanvasPosition = presented;
    result.Uv = uv.xy + corner * uv.zw;
    result.Instance = instance;
    return result;
}

[shader("fragment")]
float4 pixelMain(ImageVertex vertex) : SV_Target0
{
    uint start = vertex.Instance * 80;
    float4 clipRectangle = asfloat(Instances.Load4(start + 32));
    if (any(vertex.CanvasPosition < clipRectangle.xy) ||
        any(vertex.CanvasPosition >= clipRectangle.xy + clipRectangle.zw)) discard;
    float4 color = ImageTexture.Sample(ImageSampler, vertex.Uv);
    float alpha = color.a * asfloat(Instances.Load(start + 72));
    return float4(color.rgb * alpha, alpha);
}
