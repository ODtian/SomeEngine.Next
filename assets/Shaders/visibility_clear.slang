struct VisibilityWinnerUniforms
{
    uint Width;
    uint Height;
    uint ViewCount;
    uint Pad0;
};
ConstantBuffer<VisibilityWinnerUniforms> Uniforms;
#ifdef VISIBILITY_BUFFER
RWByteAddressBuffer Winner;
[shader("compute")]
[numthreads(64, 1, 1)]
void ClearVisibilityWinner(uint3 threadId : SV_DispatchThreadID)
{
    uint pixelCount = Uniforms.Width * Uniforms.Height * Uniforms.ViewCount;
    uint index = threadId.x + threadId.y * (65535u * 64u);
    if (index < pixelCount)
        Winner.Store2(index * 8u, 0u.xx);
}
#else
RWTexture2DArray<uint64_t> Winner;
[shader("compute")]
[numthreads(8, 8, 1)]
void ClearVisibilityWinner(uint3 threadId : SV_DispatchThreadID)
{
    if (threadId.x < Uniforms.Width && threadId.y < Uniforms.Height &&
        threadId.z < Uniforms.ViewCount)
        Winner[threadId] = uint64_t(0);
}
#endif
