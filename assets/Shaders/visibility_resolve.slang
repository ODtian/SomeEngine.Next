#ifdef VISIBILITY_BUFFER
ByteAddressBuffer Winner;
#else
Texture2DArray<uint64_t> Winner;
#endif
struct VisibilityWinnerUniforms
{
    uint Width;
    uint Height;
    uint ViewCount;
    uint Pad0;
};
ConstantBuffer<VisibilityWinnerUniforms> Uniforms;
struct VSOutput
{
    float4 Pos : SV_POSITION;
    uint ViewIndex : SV_RenderTargetArrayIndex;
};
[shader("vertex")]
VSOutput VSFullscreen(uint vertexID : SV_VertexID, uint viewIndex : SV_InstanceID)
{
    VSOutput output;
    float2 uv = float2((vertexID << 1) & 2, vertexID & 2);
    output.Pos = float4(uv * 2.0 - 1.0, 0.0, 1.0);
    output.ViewIndex = viewIndex;
    return output;
}
struct PSWinnerOutput
{
    uint Visibility : SV_TARGET;
    float Depth : SV_Depth;
};
[shader("pixel")]
PSWinnerOutput PSResolveVisibilityWinner(VSOutput input)
{
    uint2 pixel = uint2(input.Pos.xy);
#ifdef VISIBILITY_BUFFER
    uint linear = (input.ViewIndex * Uniforms.Height + pixel.y) * Uniforms.Width + pixel.x;
    uint64_t winner = Winner.Load<uint64_t>(linear * 8u);
#else
    uint64_t winner = Winner[uint3(pixel, input.ViewIndex)];
#endif
    PSWinnerOutput output;
    output.Visibility = uint(winner);
    uint reversedDepth = uint(winner >> 32u);
    output.Depth = reversedDepth == 0u ? 1.0 : 1.0 - asfloat(reversedDepth);
    return output;
}
