using System.Numerics;

namespace SomeEngine.Graphics;

public enum QueueType : byte
{
    Graphics,
    Compute,
    Copy,
}

public enum MemoryType : byte
{
    DeviceLocal,
    Upload,
    Readback,
}

[Flags]
public enum HeapFlags : byte
{
    None = 0,
    Buffers = 1 << 0,
    Textures = 1 << 1,
    Attachments = 1 << 2,
    Shareable = 1 << 3,
}

[Flags]
public enum BufferUsages : uint
{
    None = 0,
    CopySource = 1u << 0,
    CopyDestination = 1u << 1,
    Constant = 1u << 2,
    ShaderRead = 1u << 3,
    ShaderWrite = 1u << 4,
    Vertex = 1u << 5,
    Index = 1u << 6,
    Indirect = 1u << 7,
    AccelerationStructure = 1u << 8,
    AccelerationStructureInput = 1u << 9,
    Predication = 1u << 10,
    StreamOutput = 1u << 11,
    QueryResolve = 1u << 12,
    Shareable = 1u << 13,
}

[Flags]
public enum TextureUsages : uint
{
    None = 0,
    CopySource = 1u << 0,
    CopyDestination = 1u << 1,
    Sampled = 1u << 2,
    Storage = 1u << 3,
    ColorAttachment = 1u << 4,
    DepthStencilAttachment = 1u << 5,
    ShadingRate = 1u << 6,
    SamplerFeedback = 1u << 7,
    Shareable = 1u << 8,
}

public enum Format : ushort
{
    R8UNorm = 1,
    R8SNorm,
    R8UInt,
    R8SInt,
    R8G8UNorm,
    R8G8SNorm,
    R8G8UInt,
    R8G8SInt,
    R8G8B8A8UNorm,
    R8G8B8A8UNormSrgb,
    R8G8B8A8SNorm,
    R8G8B8A8UInt,
    R8G8B8A8SInt,
    B8G8R8A8UNorm,
    B8G8R8A8UNormSrgb,
    R10G10B10A2UNorm,
    R11G11B10Float,
    R16UNorm,
    R16SNorm,
    R16UInt,
    R16SInt,
    R16Float,
    R16G16UNorm,
    R16G16SNorm,
    R16G16UInt,
    R16G16SInt,
    R16G16Float,
    R16G16B16A16UNorm,
    R16G16B16A16SNorm,
    R16G16B16A16UInt,
    R16G16B16A16SInt,
    R16G16B16A16Float,
    R32UInt,
    R32SInt,
    R32Float,
    R32G32UInt,
    R32G32SInt,
    R32G32Float,
    R32G32B32Float,
    R32G32B32A32UInt,
    R32G32B32A32SInt,
    R32G32B32A32Float,
    D16UNorm,
    D24UNormS8UInt,
    D32Float,
    D32FloatS8UInt,
    BC1UNorm,
    BC1UNormSrgb,
    BC2UNorm,
    BC2UNormSrgb,
    BC3UNorm,
    BC3UNormSrgb,
    BC4UNorm,
    BC4SNorm,
    BC5UNorm,
    BC5SNorm,
    BC6HUFloat,
    BC6HSFloat,
    BC7UNorm,
    BC7UNormSrgb,
}

public enum TextureDimension : byte
{
    Texture1D,
    Texture2D,
    Texture3D,
}

public enum TextureViewDimension : byte
{
    Texture1D,
    Texture1DArray,
    Texture2D,
    Texture2DArray,
    Texture2DMultisampled,
    Texture2DMultisampledArray,
    Cube,
    CubeArray,
    Texture3D,
}

[Flags]
public enum TextureAspects : byte
{
    None = 0,
    Color = 1 << 0,
    Depth = 1 << 1,
    Stencil = 1 << 2,
    Plane0 = 1 << 3,
    Plane1 = 1 << 4,
    Plane2 = 1 << 5,
}

public readonly record struct BufferRange(ulong Offset, ulong Size)
{
    public static BufferRange Whole => new(0, ulong.MaxValue);
    public bool IsWhole => Offset == 0 && Size == ulong.MaxValue;

    internal BufferRange Resolve(ulong bufferSize)
    {
        if (IsWhole)
            return new BufferRange(0, bufferSize);
        if (Size == 0 || Offset > bufferSize || Size > bufferSize - Offset)
            throw new ArgumentOutOfRangeException(nameof(Size));
        return this;
    }
}

public readonly record struct BufferRegion(Buffer Buffer, BufferRange Range);

public readonly record struct TextureSubresourceRange(
    uint FirstMipLevel,
    uint MipLevelCount,
    uint FirstArrayLayer,
    uint ArrayLayerCount,
    TextureAspects Aspects);

public readonly record struct HeapDesc(
    ulong Size,
    ulong Alignment,
    MemoryType MemoryType,
    HeapFlags Flags,
    uint CreationNodeMask = 1,
    uint VisibleNodeMask = 1,
    string? Label = null);

public readonly record struct BufferDesc(
    ulong Size,
    BufferUsages Usages,
    string? Label = null);

public readonly ref struct TextureDesc
{
    public TextureDesc(
        TextureDimension dimension,
        uint width,
        uint height,
        uint depth,
        uint mipLevelCount,
        uint arrayLayerCount,
        uint sampleCount,
        Format format,
        TextureUsages usages,
        ReadOnlySpan<Format> permittedViewFormats = default,
        string? label = null)
    {
        Dimension = dimension;
        Width = width;
        Height = height;
        Depth = depth;
        MipLevelCount = mipLevelCount;
        ArrayLayerCount = arrayLayerCount;
        SampleCount = sampleCount;
        Format = format;
        Usages = usages;
        PermittedViewFormats = permittedViewFormats;
        Label = label;
    }

    public TextureDimension Dimension { get; }
    public uint Width { get; }
    public uint Height { get; }
    public uint Depth { get; }
    public uint MipLevelCount { get; }
    public uint ArrayLayerCount { get; }
    public uint SampleCount { get; }
    public Format Format { get; }
    public TextureUsages Usages { get; }
    public ReadOnlySpan<Format> PermittedViewFormats { get; }
    public string? Label { get; }
}

public readonly record struct HeapInfo(
    ulong Size,
    ulong Alignment,
    MemoryType MemoryType,
    HeapFlags Flags,
    uint CreationNodeMask,
    uint VisibleNodeMask);

public readonly record struct BufferInfo(
    ulong Size,
    BufferUsages Usages,
    MemoryType MemoryType,
    ulong AllocationOffset,
    ulong AllocationSize);

public sealed class TextureInfo
{
    private readonly Format[] _permittedViewFormats;

    internal TextureInfo(
        TextureDimension dimension,
        uint width,
        uint height,
        uint depth,
        uint mipLevelCount,
        uint arrayLayerCount,
        uint sampleCount,
        Format format,
        TextureUsages usages,
        MemoryType memoryType,
        ReadOnlySpan<Format> permittedViewFormats,
        ulong allocationOffset,
        ulong allocationSize)
    {
        Dimension = dimension;
        Width = width;
        Height = height;
        Depth = depth;
        MipLevelCount = mipLevelCount;
        ArrayLayerCount = arrayLayerCount;
        SampleCount = sampleCount;
        Format = format;
        Usages = usages;
        MemoryType = memoryType;
        AllocationOffset = allocationOffset;
        AllocationSize = allocationSize;
        _permittedViewFormats = permittedViewFormats.ToArray();
    }

    public TextureDimension Dimension { get; }
    public uint Width { get; }
    public uint Height { get; }
    public uint Depth { get; }
    public uint MipLevelCount { get; }
    public uint ArrayLayerCount { get; }
    public uint SampleCount { get; }
    public Format Format { get; }
    public TextureUsages Usages { get; }
    public MemoryType MemoryType { get; }
    public ulong AllocationOffset { get; }
    public ulong AllocationSize { get; }
    public ReadOnlySpan<Format> PermittedViewFormats => _permittedViewFormats;
}

public readonly record struct MemoryRequirements(
    ulong Size,
    ulong Alignment,
    HeapFlags CompatibleHeapFlags);

public readonly record struct TextureCopyFootprint(
    ulong Offset,
    uint RowPitch,
    uint RowCount,
    ulong RowSize,
    ulong TotalSize);

public enum FilterType : byte
{
    Nearest,
    Linear,
}

public enum AddressType : byte
{
    Repeat,
    MirrorRepeat,
    ClampToEdge,
    ClampToBorder,
    MirrorOnce,
}

public enum CompareOperation : byte
{
    Never,
    Less,
    Equal,
    LessOrEqual,
    Greater,
    NotEqual,
    GreaterOrEqual,
    Always,
}

public readonly record struct SamplerDesc(
    FilterType MinFilter,
    FilterType MagFilter,
    FilterType MipFilter,
    AddressType AddressU,
    AddressType AddressV,
    AddressType AddressW,
    float MipLodBias = 0,
    uint MaximumAnisotropy = 1,
    CompareOperation? Comparison = null,
    Vector4 BorderColor = default,
    float MinimumLod = 0,
    float MaximumLod = float.MaxValue,
    string? Label = null);
