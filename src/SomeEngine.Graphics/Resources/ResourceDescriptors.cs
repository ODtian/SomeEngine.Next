using System.Collections.Immutable;

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

public enum ResourceHeapClass : byte
{
    Buffer,
    Texture,
    RenderTargetOrDepth,
    All,
}

[Flags]
public enum BufferUsage : uint
{
    None = 0,
    CopySource = 1u << 0,
    CopyDestination = 1u << 1,
    Constant = 1u << 2,
    ShaderRead = 1u << 3,
    Vertex = 1u << 4,
    Index = 1u << 5,
    Indirect = 1u << 6,
    ShaderWrite = 1u << 7,
}

[Flags]
public enum TextureUsage : uint
{
    None = 0,
    CopySource = 1u << 0,
    CopyDestination = 1u << 1,
    Sampled = 1u << 2,
    Storage = 1u << 3,
    ColorAttachment = 1u << 4,
    DepthStencilAttachment = 1u << 5,
}

public enum Format : ushort
{
    Unknown,
    R8UNorm,
    R8G8UNorm,
    R8G8B8A8UNorm,
    R8G8B8A8UNormSrgb,
    B8G8R8A8UNorm,
    R16UInt,
    R16Float,
    R16G16Float,
    R16G16B16A16Float,
    R32UInt,
    R32Float,
    R32G32Float,
    R32G32B32Float,
    R32G32B32A32Float,
    D24UNormS8UInt,
    D32Float,
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
    Texture2DMS,
    Texture2DMSArray,
    Cube,
    CubeArray,
    Texture3D,
}

public readonly record struct BufferDesc(ulong Size, BufferUsage Usage, string? Name = null)
{
    public void Validate()
    {
        if (Size == 0) throw new ArgumentOutOfRangeException(nameof(Size));
        if (Usage == BufferUsage.None) throw new ArgumentOutOfRangeException(nameof(Usage));
    }
}

public readonly struct TextureDesc : IEquatable<TextureDesc>
{
    public TextureDesc(
        int Width,
        int Height,
        Format Format,
        TextureUsage Usage,
        int Depth = 1,
        int MipLevels = 1,
        int ArrayLayers = 1,
        int SampleCount = 1,
        string? Name = null,
        TextureDimension Dimension = TextureDimension.Texture2D,
        bool CubeCompatible = false,
        IEnumerable<Format>? AllowedViewFormats = null)
    {
        this.Width = Width;
        this.Height = Height;
        this.Format = Format;
        this.Usage = Usage;
        this.Depth = Depth;
        this.MipLevels = MipLevels;
        this.ArrayLayers = ArrayLayers;
        this.SampleCount = SampleCount;
        this.Name = Name;
        this.Dimension = Dimension;
        this.CubeCompatible = CubeCompatible;

        if (AllowedViewFormats is null)
        {
            this.AllowedViewFormats = ImmutableArray.Create(Format);
        }
        else
        {
            SortedSet<Format> normalized = [Format];
            foreach (Format allowed in AllowedViewFormats) normalized.Add(allowed);
            this.AllowedViewFormats = normalized.ToImmutableArray();
        }
    }

    public int Width { get; init; }
    public int Height { get; init; }
    public Format Format { get; init; }
    public TextureUsage Usage { get; init; }
    public int Depth { get; init; }
    public int MipLevels { get; init; }
    public int ArrayLayers { get; init; }
    public int SampleCount { get; init; }
    public string? Name { get; init; }
    public TextureDimension Dimension { get; init; }
    public bool CubeCompatible { get; init; }
    public ImmutableArray<Format> AllowedViewFormats { get; }

    public void Validate()
    {
        ValidateExtents();
        ValidateEnumsAndUsage();
        ValidateViewFormats();
        ValidateFormatUsage();
        ValidateDimension();
        ValidateMipLevels();
        ValidateMultisampling();
        ValidateCubeCompatibility();
    }

    private void ValidateExtents()
    {
        if (Width <= 0 || Height <= 0 || Depth <= 0) throw new ArgumentOutOfRangeException(nameof(Width));
        if (MipLevels <= 0 || ArrayLayers <= 0 || SampleCount <= 0) throw new ArgumentOutOfRangeException(nameof(MipLevels));
    }

    private void ValidateEnumsAndUsage()
    {
        if (!Enum.IsDefined(Dimension)) throw new ArgumentOutOfRangeException(nameof(Dimension));
        if (!Enum.IsDefined(Format) || Format == Format.Unknown) throw new ArgumentOutOfRangeException(nameof(Format));
        const TextureUsage allUsage = TextureUsage.CopySource | TextureUsage.CopyDestination |
                                        TextureUsage.Sampled | TextureUsage.Storage |
                                        TextureUsage.ColorAttachment | TextureUsage.DepthStencilAttachment;
        if (Usage == TextureUsage.None || (Usage & ~allUsage) != 0) throw new ArgumentOutOfRangeException(nameof(Usage));
    }

    private void ValidateViewFormats()
    {
        if (AllowedViewFormats.IsDefaultOrEmpty || !AllowedViewFormats.Contains(Format))
            throw new ArgumentException("Allowed view formats must contain the resource format.", nameof(AllowedViewFormats));
        foreach (Format allowed in AllowedViewFormats)
        {
            if (!Enum.IsDefined(allowed) || allowed == Format.Unknown || !AreViewFormatsCompatible(Format, allowed))
            {
                throw new ArgumentException(
                    $"View format {allowed} is not compatible with resource format {Format}.",
                    nameof(AllowedViewFormats));
            }
        }
    }

    private void ValidateFormatUsage()
    {
        bool depthFormat = Format is Format.D24UNormS8UInt or Format.D32Float;
        if (depthFormat && (Usage & (TextureUsage.ColorAttachment | TextureUsage.Storage)) != 0)
            throw new ArgumentException("Depth formats cannot be color attachments or storage textures.", nameof(Usage));
        if (!depthFormat && (Usage & TextureUsage.DepthStencilAttachment) != 0)
            throw new ArgumentException("A color format cannot be a depth-stencil attachment.", nameof(Usage));
    }

    private void ValidateDimension()
    {
        switch (Dimension)
        {
            case TextureDimension.Texture1D:
                if (Height != 1 || Depth != 1 || SampleCount != 1)
                    throw new ArgumentException("A one-dimensional texture has height/depth/sample-count equal to one.");
                break;
            case TextureDimension.Texture2D:
                if (Depth != 1)
                    throw new ArgumentException("A two-dimensional texture has depth equal to one.", nameof(Depth));
                break;
            case TextureDimension.Texture3D:
                if (ArrayLayers != 1 || SampleCount != 1 || CubeCompatible)
                    throw new ArgumentException("A three-dimensional texture has one array layer, one sample, and is not cube-compatible.");
                if ((Usage & TextureUsage.DepthStencilAttachment) != 0)
                    throw new ArgumentException("Three-dimensional depth-stencil attachments are not part of the portable surface.", nameof(Usage));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(Dimension));
        }
    }

    private void ValidateMipLevels()
    {
        int maximumMipLevels = 1 + (int)Math.Floor(Math.Log2(Dimension switch
        {
            TextureDimension.Texture1D => Width,
            TextureDimension.Texture2D => Math.Max(Width, Height),
            TextureDimension.Texture3D => Math.Max(Math.Max(Width, Height), Depth),
            _ => throw new ArgumentOutOfRangeException(nameof(Dimension)),
        }));
        if (MipLevels > maximumMipLevels)
            throw new ArgumentOutOfRangeException(nameof(MipLevels), "Texture mip count exceeds its largest extent.");
    }

    private void ValidateMultisampling()
    {
        if (SampleCount > 1 && (Dimension != TextureDimension.Texture2D || MipLevels != 1))
            throw new ArgumentException("A multisampled texture must be two-dimensional and expose exactly one mip level.", nameof(SampleCount));
        if (SampleCount > 1 && (Usage & TextureUsage.Storage) != 0)
        {
            throw new ArgumentException(
                "A multisampled texture cannot declare storage usage.",
                nameof(Usage));
        }
    }

    private void ValidateCubeCompatibility()
    {
        if (CubeCompatible &&
            (Dimension != TextureDimension.Texture2D || SampleCount != 1 || Width != Height ||
             ArrayLayers < 6 || ArrayLayers % 6 != 0))
        {
            throw new ArgumentException(
                "A cube-compatible texture is a single-sampled square 2D array with a positive multiple of six layers.",
                nameof(CubeCompatible));
        }
    }

    public bool AllowsViewFormat(Format format) =>
        !AllowedViewFormats.IsDefault && AllowedViewFormats.BinarySearch(format) >= 0;

    internal ulong CompatibilitySignature()
    {
        const ulong offsetBasis = 14695981039346656037UL;
        const ulong prime = 1099511628211UL;
        ulong value = offsetBasis;
        Add((ulong)Dimension);
        Add(CubeCompatible ? 1UL : 0UL);
        Add((ulong)Format);
        Add((ulong)AllowedViewFormats.Length);
        foreach (Format allowed in AllowedViewFormats) Add((ulong)allowed);
        return value;

        void Add(ulong item)
        {
            value ^= item;
            value *= prime;
        }
    }

    public bool Equals(TextureDesc other) =>
        Width == other.Width &&
        Height == other.Height &&
        Format == other.Format &&
        Usage == other.Usage &&
        Depth == other.Depth &&
        MipLevels == other.MipLevels &&
        ArrayLayers == other.ArrayLayers &&
        SampleCount == other.SampleCount &&
        Name == other.Name &&
        Dimension == other.Dimension &&
        CubeCompatible == other.CubeCompatible &&
        AllowedViewFormats.AsSpan().SequenceEqual(other.AllowedViewFormats.AsSpan());

    public override bool Equals(object? obj) => obj is TextureDesc other && Equals(other);

    public override int GetHashCode()
    {
        HashCode hash = new();
        hash.Add(Width);
        hash.Add(Height);
        hash.Add(Format);
        hash.Add(Usage);
        hash.Add(Depth);
        hash.Add(MipLevels);
        hash.Add(ArrayLayers);
        hash.Add(SampleCount);
        hash.Add(Name);
        hash.Add(Dimension);
        hash.Add(CubeCompatible);
        foreach (Format allowed in AllowedViewFormats) hash.Add(allowed);
        return hash.ToHashCode();
    }

    public static bool operator ==(TextureDesc left, TextureDesc right) => left.Equals(right);
    public static bool operator !=(TextureDesc left, TextureDesc right) => !left.Equals(right);

    private static bool AreViewFormatsCompatible(Format resource, Format view) =>
        resource == view ||
        (resource is Format.R8G8B8A8UNorm or Format.R8G8B8A8UNormSrgb) &&
        (view is Format.R8G8B8A8UNorm or Format.R8G8B8A8UNormSrgb);
}

public readonly record struct HeapDesc(
    ulong Size,
    MemoryType MemoryType,
    ResourceHeapClass ResourceClass,
    string? Name = null);

public readonly record struct ResourceRequirements(
    ulong Size,
    ulong Alignment,
    MemoryType MemoryType,
    ResourceHeapClass ResourceClass,
    ulong CompatibilityClass);

public readonly record struct BufferRange(ulong Offset, ulong Size)
{
    public static BufferRange Whole => new(0, ulong.MaxValue);
}

[Flags]
public enum TextureAspect : byte
{
    Color = 1 << 0,
    Depth = 1 << 1,
    Stencil = 1 << 2,
}

public readonly record struct TextureSubresourceRange(
    int FirstMip,
    int MipCount,
    int FirstLayer,
    int LayerCount,
    TextureAspect Aspect)
{
    public static TextureSubresourceRange WholeColor => new(0, int.MaxValue, 0, int.MaxValue, TextureAspect.Color);
}

[Flags]
public enum TextureViewUsage : byte
{
    ShaderResource = 1 << 0,
    Storage = 1 << 1,
    ColorAttachment = 1 << 2,
    DepthStencilAttachment = 1 << 3,
}

public readonly record struct TextureViewDesc(
    TextureHandle Texture,
    TextureSubresourceRange Range,
    TextureViewUsage Usage,
    Format Format = Format.Unknown,
    string? Name = null,
    TextureViewDimension Dimension = TextureViewDimension.Texture2D);

public readonly record struct BufferViewDesc(
    BufferHandle Buffer,
    BufferRange Range,
    BindingKind Kind,
    Format Format = Format.Unknown,
    uint Stride = 0,
    string? Name = null);

public enum FilterMode : byte
{
    Nearest,
    Linear,
}

public enum AddressMode : byte
{
    Repeat,
    Mirror,
    Clamp,
    Border,
}

public readonly record struct SamplerDesc(
    FilterMode MinFilter = FilterMode.Linear,
    FilterMode MagFilter = FilterMode.Linear,
    FilterMode MipFilter = FilterMode.Linear,
    AddressMode AddressU = AddressMode.Clamp,
    AddressMode AddressV = AddressMode.Clamp,
    AddressMode AddressW = AddressMode.Clamp,
    string? Name = null);
