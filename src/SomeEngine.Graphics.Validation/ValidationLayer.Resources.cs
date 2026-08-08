namespace SomeEngine.Graphics.Validation;

public sealed partial class ValidationLayer<TBackend>
{
    public MemoryRequirements GetBufferMemoryRequirements(
        Device device,
        in BufferDesc desc,
        MemoryType memoryType = MemoryType.DeviceLocal)
    {
        RequireDevice(device);
        return Backend.GetBufferMemoryRequirements(device, desc, memoryType);
    }

    public MemoryRequirements GetTextureMemoryRequirements(Device device, in TextureDesc desc)
    {
        RequireDevice(device);
        return Backend.GetTextureMemoryRequirements(device, desc);
    }

    public TextureCopyFootprint GetTextureCopyFootprint(
        Device device,
        in TextureDesc desc,
        in BufferTextureCopy copy,
        ulong requestedBufferOffset = 0)
    {
        RequireDevice(device);
        return Backend.GetTextureCopyFootprint(device, desc, copy, requestedBufferOffset);
    }

    public Heap CreateHeap(Device device, in HeapDesc desc)
    {
        RequireDevice(device);
        return Track(Backend.CreateHeap(device, desc), device);
    }

    public Buffer CreateBuffer(
        Device device,
        in BufferDesc desc,
        MemoryType memoryType = MemoryType.DeviceLocal)
    {
        RequireDevice(device);
        return Track(Backend.CreateBuffer(device, desc, memoryType), device);
    }

    public Buffer CreatePlacedBuffer(Device device, Heap heap, ulong offset, in BufferDesc desc)
    {
        RequireDevice(device);
        RequireOnDevice(device, heap, "Heap");
        return Track(Backend.CreatePlacedBuffer(device, heap, offset, desc), heap);
    }

    public Texture CreateTexture(Device device, in TextureDesc desc)
    {
        RequireDevice(device);
        return Track(Backend.CreateTexture(device, desc), device);
    }

    public Texture CreatePlacedTexture(Device device, Heap heap, ulong offset, in TextureDesc desc)
    {
        RequireDevice(device);
        RequireOnDevice(device, heap, "Heap");
        return Track(Backend.CreatePlacedTexture(device, heap, offset, desc), heap);
    }

    public BufferCbv CreateBufferCbv(Device device, in BufferCbvDesc desc)
    {
        RequireDevice(device);
        RequireOnDevice(device, desc.Buffer, "Buffer");
        return Track(Backend.CreateBufferCbv(device, desc), desc.Buffer);
    }

    public BufferSrv CreateBufferSrv(Device device, in BufferSrvDesc desc)
    {
        RequireDevice(device);
        RequireOnDevice(device, desc.Buffer, "Buffer");
        return Track(Backend.CreateBufferSrv(device, desc), desc.Buffer);
    }

    public BufferUav CreateBufferUav(Device device, in BufferUavDesc desc)
    {
        RequireDevice(device);
        RequireOnDevice(device, desc.Buffer, "Buffer");
        if (desc.CounterBuffer is not null)
            RequireOnDevice(device, desc.CounterBuffer, "Counter Buffer");
        return Track(Backend.CreateBufferUav(device, desc), desc.Buffer);
    }

    public TextureSrv CreateTextureSrv(Device device, in TextureSrvDesc desc)
    {
        RequireDevice(device);
        RequireOnDevice(device, desc.Texture, "Texture");
        return Track(Backend.CreateTextureSrv(device, desc), desc.Texture);
    }

    public TextureUav CreateTextureUav(Device device, in TextureUavDesc desc)
    {
        RequireDevice(device);
        RequireOnDevice(device, desc.Texture, "Texture");
        return Track(Backend.CreateTextureUav(device, desc), desc.Texture);
    }

    public ColorAttachmentView CreateColorAttachmentView(
        Device device,
        in ColorAttachmentViewDesc desc)
    {
        RequireDevice(device);
        RequireOnDevice(device, desc.Texture, "Texture");
        return Track(Backend.CreateColorAttachmentView(device, desc), desc.Texture);
    }

    public DepthStencilView CreateDepthStencilView(
        Device device,
        in DepthStencilViewDesc desc)
    {
        RequireDevice(device);
        RequireOnDevice(device, desc.Texture, "Texture");
        return Track(Backend.CreateDepthStencilView(device, desc), desc.Texture);
    }

    public Sampler CreateSampler(Device device, in SamplerDesc desc)
    {
        RequireDevice(device);
        return Track(Backend.CreateSampler(device, desc), device);
    }

    public BindlessBufferCbv CreateBindlessBufferCbv(Device device, in BufferCbvDesc desc)
    {
        RequireDevice(device);
        RequireOnDevice(device, desc.Buffer, "Buffer");
        return Track(Backend.CreateBindlessBufferCbv(device, desc), desc.Buffer);
    }

    public BindlessBufferSrv CreateBindlessBufferSrv(Device device, in BufferSrvDesc desc)
    {
        RequireDevice(device);
        RequireOnDevice(device, desc.Buffer, "Buffer");
        return Track(Backend.CreateBindlessBufferSrv(device, desc), desc.Buffer);
    }

    public BindlessBufferUav CreateBindlessBufferUav(Device device, in BufferUavDesc desc)
    {
        RequireDevice(device);
        RequireOnDevice(device, desc.Buffer, "Buffer");
        if (desc.CounterBuffer is not null)
            RequireOnDevice(device, desc.CounterBuffer, "Counter Buffer");
        return Track(Backend.CreateBindlessBufferUav(device, desc), desc.Buffer);
    }

    public BindlessTextureSrv CreateBindlessTextureSrv(Device device, in TextureSrvDesc desc)
    {
        RequireDevice(device);
        RequireOnDevice(device, desc.Texture, "Texture");
        return Track(Backend.CreateBindlessTextureSrv(device, desc), desc.Texture);
    }

    public BindlessTextureUav CreateBindlessTextureUav(Device device, in TextureUavDesc desc)
    {
        RequireDevice(device);
        RequireOnDevice(device, desc.Texture, "Texture");
        return Track(Backend.CreateBindlessTextureUav(device, desc), desc.Texture);
    }

    public BindlessSampler CreateBindlessSampler(Device device, in SamplerDesc desc)
    {
        RequireDevice(device);
        return Track(Backend.CreateBindlessSampler(device, desc), device);
    }

    public MappedBuffer Map(Buffer buffer, MapType type, in BufferRange range)
    {
        Require(buffer);
        return Backend.Map(buffer, type, range);
    }

    private void RequireSameDevice(Device expected, Device actual, string objectType)
    {
        if (!ReferenceEquals(expected, actual))
            Reject("Ownership", $"{objectType} belongs to another Device.");
    }
}
