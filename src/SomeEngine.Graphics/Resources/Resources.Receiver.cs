namespace SomeEngine.Graphics;

public partial interface IGraphicsBackend
{
    MemoryRequirements GetBufferMemoryRequirements(
        Device device,
        in BufferDesc desc,
        MemoryType memoryType = MemoryType.DeviceLocal);

    MemoryRequirements GetTextureMemoryRequirements(Device device, in TextureDesc desc);

    TextureCopyFootprint GetTextureCopyFootprint(
        Device device,
        in TextureDesc desc,
        in BufferTextureCopy copy,
        ulong requestedBufferOffset = 0);

    Heap CreateHeap(Device device, in HeapDesc desc);

    Buffer CreateBuffer(
        Device device,
        in BufferDesc desc,
        MemoryType memoryType = MemoryType.DeviceLocal);

    Buffer CreatePlacedBuffer(
        Device device,
        Heap heap,
        ulong offset,
        in BufferDesc desc);

    Texture CreateTexture(Device device, in TextureDesc desc);

    Texture CreatePlacedTexture(
        Device device,
        Heap heap,
        ulong offset,
        in TextureDesc desc);

    BufferCbv CreateBufferCbv(Device device, in BufferCbvDesc desc);
    BufferSrv CreateBufferSrv(Device device, in BufferSrvDesc desc);
    BufferUav CreateBufferUav(Device device, in BufferUavDesc desc);
    TextureSrv CreateTextureSrv(Device device, in TextureSrvDesc desc);
    TextureUav CreateTextureUav(Device device, in TextureUavDesc desc);
    ColorAttachmentView CreateColorAttachmentView(Device device, in ColorAttachmentViewDesc desc);
    DepthStencilView CreateDepthStencilView(Device device, in DepthStencilViewDesc desc);
    Sampler CreateSampler(Device device, in SamplerDesc desc);

    MappedBuffer Map(Buffer buffer, MapType type, in BufferRange range);
}
