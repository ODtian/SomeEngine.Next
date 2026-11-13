struct CanvasData
{
    float2 Size;
    float Scale;
    float DistanceSpread;
    float4x4 ClipTransform;
};

ByteAddressBuffer Instances;
ConstantBuffer<CanvasData> Canvas;
Texture2D<float> Atlas;
SamplerState AtlasSampler;

struct GlyphVertex
{
    float4 Position : SV_Position;
    float2 CanvasPosition : TEXCOORD0;
    float2 UV : TEXCOORD1;
    nointerpolation uint Instance : TEXCOORD2;
};

[shader("vertex")]
GlyphVertex vertexMain(uint vertex : SV_VertexID, uint instance : SV_InstanceID)
{
    const float2 corners[6] = { float2(0, 0), float2(1, 0), float2(0, 1),
        float2(0, 1), float2(1, 0), float2(1, 1) };
    uint start = instance * 96;
    float4 rectangle = asfloat(Instances.Load4(start));
    float4 uv = asfloat(Instances.Load4(start + 16));
    float4 matrix = asfloat(Instances.Load4(start + 64));
    float2 translation = asfloat(Instances.Load2(start + 80));
    float2 position = rectangle.xy + corners[vertex] * rectangle.zw;
    float2 presented = position.x * matrix.xy + position.y * matrix.zw + translation;
    GlyphVertex result;
    result.Position = mul(float4(presented, 0, 1), Canvas.ClipTransform);
    result.CanvasPosition = presented;
    result.UV = lerp(uv.xy, uv.zw, corners[vertex]);
    result.Instance = instance;
    return result;
}

[shader("fragment")]
float4 pixelMain(GlyphVertex vertex) : SV_Target0
{
    uint start = vertex.Instance * 96;
    float4 clip = asfloat(Instances.Load4(start + 48));
    if (any(vertex.CanvasPosition < clip.xy) || any(vertex.CanvasPosition >= clip.xy + clip.zw)) discard;
    float4 rectangle = asfloat(Instances.Load4(start));
    float4 uv = asfloat(Instances.Load4(start + 16));
    float4 color = asfloat(Instances.Load4(start + 32));
    uint width, height;
    Atlas.GetDimensions(width, height);
    float2 texelPosition = vertex.UV * float2(width, height);
    float2 horizontal = ddx(texelPosition);
    float2 vertical = ddy(texelPosition);
    float pixelsPerTexel = rsqrt(max(max(dot(horizontal, horizontal), dot(vertical, vertical)), 0.000001));
    float distance = (Atlas.SampleLevel(AtlasSampler, vertex.UV, 0) * 255 - 128)
        * Canvas.DistanceSpread / 128 * pixelsPerTexel;
    float coverage = saturate(0.5 + distance);
    float alpha = color.a * coverage * asfloat(Instances.Load(start + 88));
    return float4(color.rgb * alpha, alpha);
}
