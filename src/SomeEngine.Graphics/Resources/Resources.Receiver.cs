using System.Runtime.CompilerServices;

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

    BindlessBufferCbv CreateBindlessBufferCbv(Device device, in BufferCbvDesc desc);
    BindlessBufferSrv CreateBindlessBufferSrv(Device device, in BufferSrvDesc desc);
    BindlessBufferUav CreateBindlessBufferUav(Device device, in BufferUavDesc desc);
    BindlessTextureSrv CreateBindlessTextureSrv(Device device, in TextureSrvDesc desc);
    BindlessTextureUav CreateBindlessTextureUav(Device device, in TextureUavDesc desc);
    BindlessSampler CreateBindlessSampler(Device device, in SamplerDesc desc);

    MappedBuffer Map(Buffer buffer, MapType type, in BufferRange range);
}

public sealed partial class Graphics<TBackend>
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public MemoryRequirements GetBufferMemoryRequirements(
        Device device,
        in BufferDesc desc,
        MemoryType memoryType = MemoryType.DeviceLocal) =>
        Receiver.GetBufferMemoryRequirements(device, desc, memoryType);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public MemoryRequirements GetTextureMemoryRequirements(Device device, in TextureDesc desc) =>
        Receiver.GetTextureMemoryRequirements(device, desc);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public TextureCopyFootprint GetTextureCopyFootprint(
        Device device,
        in TextureDesc desc,
        in BufferTextureCopy copy,
        ulong requestedBufferOffset = 0) =>
        Receiver.GetTextureCopyFootprint(device, desc, copy, requestedBufferOffset);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Heap CreateHeap(Device device, in HeapDesc desc) => Receiver.CreateHeap(device, desc);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Buffer CreateBuffer(
        Device device,
        in BufferDesc desc,
        MemoryType memoryType = MemoryType.DeviceLocal) =>
        Receiver.CreateBuffer(device, desc, memoryType);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Buffer CreatePlacedBuffer(Device device, Heap heap, ulong offset, in BufferDesc desc) =>
        Receiver.CreatePlacedBuffer(device, heap, offset, desc);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Texture CreateTexture(Device device, in TextureDesc desc) =>
        Receiver.CreateTexture(device, desc);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Texture CreatePlacedTexture(Device device, Heap heap, ulong offset, in TextureDesc desc) =>
        Receiver.CreatePlacedTexture(device, heap, offset, desc);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public BufferCbv CreateBufferCbv(Device device, in BufferCbvDesc desc) =>
        Receiver.CreateBufferCbv(device, desc);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public BufferSrv CreateBufferSrv(Device device, in BufferSrvDesc desc) =>
        Receiver.CreateBufferSrv(device, desc);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public BufferUav CreateBufferUav(Device device, in BufferUavDesc desc) =>
        Receiver.CreateBufferUav(device, desc);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public TextureSrv CreateTextureSrv(Device device, in TextureSrvDesc desc) =>
        Receiver.CreateTextureSrv(device, desc);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public TextureUav CreateTextureUav(Device device, in TextureUavDesc desc) =>
        Receiver.CreateTextureUav(device, desc);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ColorAttachmentView CreateColorAttachmentView(
        Device device,
        in ColorAttachmentViewDesc desc) =>
        Receiver.CreateColorAttachmentView(device, desc);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public DepthStencilView CreateDepthStencilView(
        Device device,
        in DepthStencilViewDesc desc) =>
        Receiver.CreateDepthStencilView(device, desc);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Sampler CreateSampler(Device device, in SamplerDesc desc) =>
        Receiver.CreateSampler(device, desc);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public BindlessBufferCbv CreateBindlessBufferCbv(Device device, in BufferCbvDesc desc) =>
        Receiver.CreateBindlessBufferCbv(device, desc);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public BindlessBufferSrv CreateBindlessBufferSrv(Device device, in BufferSrvDesc desc) =>
        Receiver.CreateBindlessBufferSrv(device, desc);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public BindlessBufferUav CreateBindlessBufferUav(Device device, in BufferUavDesc desc) =>
        Receiver.CreateBindlessBufferUav(device, desc);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public BindlessTextureSrv CreateBindlessTextureSrv(Device device, in TextureSrvDesc desc) =>
        Receiver.CreateBindlessTextureSrv(device, desc);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public BindlessTextureUav CreateBindlessTextureUav(Device device, in TextureUavDesc desc) =>
        Receiver.CreateBindlessTextureUav(device, desc);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public BindlessSampler CreateBindlessSampler(Device device, in SamplerDesc desc) =>
        Receiver.CreateBindlessSampler(device, desc);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public MappedBuffer Map(Buffer buffer, MapType type, in BufferRange range) =>
        Receiver.Map(buffer, type, range);
}
