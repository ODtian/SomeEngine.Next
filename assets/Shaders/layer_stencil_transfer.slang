struct LayerStencilTransferParameters
{
    uint Width, Height, Views, RowPitch;
    uint ViewBytes, Padding0, Padding1, Padding2;
};
ConstantBuffer<LayerStencilTransferParameters> LayerStencilTransfer;
Texture2DArray<uint> LayerStencilRead;
RWTexture2DArray<uint> LayerStencilWrite;
ByteAddressBuffer LayerStencilBytesRead;
RWByteAddressBuffer LayerStencilBytesWrite;

[shader("compute")][numthreads(64,1,1)]
void LayerPackStencil(uint3 thread : SV_DispatchThreadID)
{
    uint address = thread.x * 4u;
    if (address >= LayerStencilTransfer.ViewBytes * LayerStencilTransfer.Views) return;
    uint view = address / LayerStencilTransfer.ViewBytes;
    uint offset = address % LayerStencilTransfer.ViewBytes;
    uint first = offset % LayerStencilTransfer.RowPitch;
    uint row = offset / LayerStencilTransfer.RowPitch;
    uint packed = 0u;
    for (uint component = 0u; component < 4u; component++)
        if (row < LayerStencilTransfer.Height && first + component < LayerStencilTransfer.Width)
            packed |= (LayerStencilRead.Load(int4(first + component, row, view, 0)) & 255u) << (component * 8u);
    LayerStencilBytesWrite.Store(address, packed);
}

[shader("compute")][numthreads(8,8,1)]
void LayerUnpackStencil(uint3 pixel : SV_DispatchThreadID)
{
    if (pixel.x >= LayerStencilTransfer.Width || pixel.y >= LayerStencilTransfer.Height || pixel.z >= LayerStencilTransfer.Views) return;
    uint address = pixel.z * LayerStencilTransfer.ViewBytes + pixel.y * LayerStencilTransfer.RowPitch + pixel.x;
    LayerStencilWrite[pixel] = (LayerStencilBytesRead.Load(address & ~3u) >> ((address & 3u) * 8u)) & 255u;
}
