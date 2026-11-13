struct StencilTransferParameters { uint Width, Height, Views, RowPitch; uint ViewBytes, Padding0, Padding1, Padding2; };
ConstantBuffer<StencilTransferParameters> StencilTransferUniforms;
Texture2DArray<uint> StencilRead;
RWTexture2DArray<uint> StencilWrite;
ByteAddressBuffer StencilBytesRead;
RWByteAddressBuffer StencilBytesWrite;
[shader("compute")] [numthreads(64,1,1)]
void ClearStencil(uint3 pixel : SV_DispatchThreadID)
{
    if (pixel.x < StencilTransferUniforms.Width) StencilWrite[pixel] = 0u;
}
[shader("compute")] [numthreads(64,1,1)]
void PackStencil(uint3 pixel : SV_DispatchThreadID)
{
    uint address = pixel.x * 4u;
    if (address >= StencilTransferUniforms.ViewBytes * StencilTransferUniforms.Views) return;
    uint view = address / StencilTransferUniforms.ViewBytes;
    uint offset = address % StencilTransferUniforms.ViewBytes;
    uint first = offset % StencilTransferUniforms.RowPitch;
    uint row = offset / StencilTransferUniforms.RowPitch;
    uint packed = 0u;
    for (uint component = 0u; component < 4u; component++)
        if (row < StencilTransferUniforms.Height && first + component < StencilTransferUniforms.Width)
            packed |= (StencilRead.Load(int4(first + component, row, view, 0)) & 255u) << (component * 8u);
    StencilBytesWrite.Store(address, packed);
}
[shader("compute")] [numthreads(64,1,1)]
void UnpackStencil(uint3 pixel : SV_DispatchThreadID)
{
    if (pixel.x >= StencilTransferUniforms.Width) return;
    uint address = pixel.z * StencilTransferUniforms.ViewBytes + pixel.y * StencilTransferUniforms.RowPitch + pixel.x;
    StencilWrite[pixel] = (StencilBytesRead.Load(address & ~3u) >> ((address & 3u) * 8u)) & 255u;
}
