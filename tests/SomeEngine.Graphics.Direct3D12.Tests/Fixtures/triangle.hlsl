struct VertexOutput
{
    float4 Position : SV_Position;
    float4 Color : COLOR0;
};

VertexOutput VSMain(uint vertexId : SV_VertexID)
{
    const float2 positions[3] =
    {
        float2(0.0, 0.75),
        float2(0.75, -0.75),
        float2(-0.75, -0.75),
    };
    const float4 colors[3] =
    {
        float4(1.0, 0.0, 0.0, 1.0),
        float4(0.0, 1.0, 0.0, 1.0),
        float4(0.0, 0.0, 1.0, 1.0),
    };

    VertexOutput result;
    result.Position = float4(positions[vertexId], 0.0, 1.0);
    result.Color = colors[vertexId];
    return result;
}

float4 PSMain(VertexOutput input) : SV_Target0
{
    return input.Color;
}
