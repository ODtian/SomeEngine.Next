#include "comparison_common.slang"
StructuredBuffer<ForwardTriangle> Vertices;
StructuredBuffer<float4> Colors;
Texture2DArray<float> OpaqueDepth;

struct VertexOutput
{
    float4 Position : SV_Position;
    nointerpolation uint TriangleId : TEXCOORD0;
    nointerpolation uint ColorIndex : TEXCOORD1;
};

[shader("vertex")]
VertexOutput DrawTransparency(uint vertexId : SV_VertexID)
{
    ForwardTriangle triangle = Vertices[vertexId / 3];
    VertexOutput result;
    result.Position = vertexId % 3 == 0 ? triangle.A : vertexId % 3 == 1 ? triangle.B : triangle.C;
    result.TriangleId = triangle.TriangleId;
    result.ColorIndex = triangle.ColorIndex;
    return result;
}

void ClipOpaque(VertexOutput input)
{
    uint2 pixel = uint2(input.Position.xy);
    clip(1 - OpaqueDepth.Load(int4(pixel, 0, 0)) - input.Position.z);
}
