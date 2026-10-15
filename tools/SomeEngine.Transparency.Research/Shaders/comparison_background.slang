struct BackgroundDimensions { uint Width; uint Height; };
ConstantBuffer<BackgroundDimensions> Settings;
RWTexture2DArray<float4> SceneColor;

[shader("compute")]
[numthreads(8, 8, 1)]
void FillBackground(uint3 threadId : SV_DispatchThreadID)
{
    if (any(threadId.xy >= uint2(Settings.Width, Settings.Height))) return;
    SceneColor[uint3(threadId.xy, 0)] = float4(.125, .125, .125, 1);
}
