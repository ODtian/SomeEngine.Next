namespace SomeEngine.Graphics;

public abstract class Heap : DeviceResource
{
    internal Heap(Device device, in HeapInfo info, string? label)
        : base(device, label)
    {
        Info = info;
    }

    public HeapInfo Info { get; }
}

public abstract class Resource : DeviceResource
{
    internal Resource(
        Device device,
        Heap? heap,
        PipelineSync initialSync,
        ResourceAccess initialAccess,
        string? label,
        QueueType? initialQueueType = null)
        : base(device, label)
    {
        Heap = heap;
        InitialSync = initialSync;
        InitialAccess = initialAccess;
        InitialQueueType = initialQueueType;
    }

    public Heap? Heap { get; }
    public PipelineSync InitialSync { get; }
    public ResourceAccess InitialAccess { get; }
    public QueueType? InitialQueueType { get; }
}

public abstract class Buffer : Resource
{
    internal Buffer(
        Device device,
        Heap? heap,
        in BufferInfo info,
        PipelineSync initialSync,
        ResourceAccess initialAccess,
        string? label,
        QueueType? initialQueueType = null)
        : base(device, heap, initialSync, initialAccess, label, initialQueueType)
    {
        Info = info;
    }

    public BufferInfo Info { get; }
}

public abstract class Texture : Resource
{
    internal Texture(
        Device device,
        Heap? heap,
        TextureInfo info,
        PipelineSync initialSync,
        ResourceAccess initialAccess,
        TextureLayout initialLayout,
        string? label,
        QueueType? initialQueueType = null)
        : base(device, heap, initialSync, initialAccess, label, initialQueueType)
    {
        Info = info ?? throw new ArgumentNullException(nameof(info));
        InitialLayout = initialLayout;
    }

    public TextureInfo Info { get; }
    public TextureLayout InitialLayout { get; }
}

public readonly record struct BufferCbvDesc(
    Buffer Buffer,
    BufferRange Range,
    string? Label = null);

public readonly record struct BufferSrvDesc(
    Buffer Buffer,
    BufferRange Range,
    Format? Format = null,
    uint StructureStride = 0,
    string? Label = null);

public readonly record struct BufferUavDesc(
    Buffer Buffer,
    BufferRange Range,
    Format? Format = null,
    uint StructureStride = 0,
    Buffer? CounterBuffer = null,
    ulong CounterOffset = 0,
    string? Label = null);

public readonly record struct TextureSrvDesc(
    Texture Texture,
    TextureSubresourceRange Range,
    Format Format,
    TextureViewDimension Dimension,
    string? Label = null);

public readonly record struct TextureUavDesc(
    Texture Texture,
    TextureSubresourceRange Range,
    Format Format,
    TextureViewDimension Dimension,
    string? Label = null);

public readonly record struct ColorAttachmentViewDesc(
    Texture Texture,
    TextureSubresourceRange Range,
    Format Format,
    TextureViewDimension Dimension,
    string? Label = null);

public readonly record struct DepthStencilViewDesc(
    Texture Texture,
    TextureSubresourceRange Range,
    Format Format,
    TextureViewDimension Dimension,
    bool ReadOnlyDepth = false,
    bool ReadOnlyStencil = false,
    string? Label = null);

public abstract class BufferCbv : DeviceResource
{
    internal BufferCbv(Device device, in BufferCbvDesc description)
        : base(device, description.Label) => Description = description;

    public BufferCbvDesc Description { get; }
    public Buffer Resource => Description.Buffer;
}

public abstract class BufferSrv : DeviceResource
{
    internal BufferSrv(Device device, in BufferSrvDesc description)
        : base(device, description.Label) => Description = description;

    public BufferSrvDesc Description { get; }
    public Buffer Resource => Description.Buffer;
}

public abstract class BufferUav : DeviceResource
{
    internal BufferUav(Device device, in BufferUavDesc description)
        : base(device, description.Label) => Description = description;

    public BufferUavDesc Description { get; }
    public Buffer Resource => Description.Buffer;
}

public abstract class TextureSrv : DeviceResource
{
    internal TextureSrv(Device device, in TextureSrvDesc description)
        : base(device, description.Label) => Description = description;

    public TextureSrvDesc Description { get; }
    public Texture Resource => Description.Texture;
}

public abstract class TextureUav : DeviceResource
{
    internal TextureUav(Device device, in TextureUavDesc description)
        : base(device, description.Label) => Description = description;

    public TextureUavDesc Description { get; }
    public Texture Resource => Description.Texture;
}

public abstract class ColorAttachmentView : DeviceResource
{
    internal ColorAttachmentView(Device device, in ColorAttachmentViewDesc description)
        : base(device, description.Label) => Description = description;

    public ColorAttachmentViewDesc Description { get; }
    public Texture Resource => Description.Texture;
}

public abstract class DepthStencilView : DeviceResource
{
    internal DepthStencilView(Device device, in DepthStencilViewDesc description)
        : base(device, description.Label) => Description = description;

    public DepthStencilViewDesc Description { get; }
    public Texture Resource => Description.Texture;
}

public abstract class Sampler : DeviceResource
{
    internal Sampler(Device device, in SamplerDesc description)
        : base(device, description.Label) => Description = description;

    public SamplerDesc Description { get; }
}

public abstract class BindlessBufferCbv : BufferCbv
{
    internal BindlessBufferCbv(Device device, in BufferCbvDesc description, uint descriptorIndex)
        : base(device, description) => DescriptorIndex = descriptorIndex;

    public uint DescriptorIndex { get; }
}

public abstract class BindlessBufferSrv : BufferSrv
{
    internal BindlessBufferSrv(Device device, in BufferSrvDesc description, uint descriptorIndex)
        : base(device, description) => DescriptorIndex = descriptorIndex;

    public uint DescriptorIndex { get; }
}

public abstract class BindlessBufferUav : BufferUav
{
    internal BindlessBufferUav(Device device, in BufferUavDesc description, uint descriptorIndex)
        : base(device, description) => DescriptorIndex = descriptorIndex;

    public uint DescriptorIndex { get; }
}

public abstract class BindlessTextureSrv : TextureSrv
{
    internal BindlessTextureSrv(Device device, in TextureSrvDesc description, uint descriptorIndex)
        : base(device, description) => DescriptorIndex = descriptorIndex;

    public uint DescriptorIndex { get; }
}

public abstract class BindlessTextureUav : TextureUav
{
    internal BindlessTextureUav(Device device, in TextureUavDesc description, uint descriptorIndex)
        : base(device, description) => DescriptorIndex = descriptorIndex;

    public uint DescriptorIndex { get; }
}

public abstract class BindlessSampler : Sampler
{
    internal BindlessSampler(Device device, in SamplerDesc description, uint descriptorIndex)
        : base(device, description) => DescriptorIndex = descriptorIndex;

    public uint DescriptorIndex { get; }
}
