namespace SomeEngine.RenderGraph;

/// <summary>
/// Invocation-owned copy of an RHI texture description. The public RHI description is stack-only
/// because it borrows its permitted-format span; a graph invocation must own that variable-length
/// content until compilation and realization have completed.
/// </summary>
internal sealed class GraphTextureDescription
{
    private readonly Format[] _permittedViewFormats;

    internal GraphTextureDescription(in TextureDesc description)
        : this(
            description.Dimension,
            description.Width,
            description.Height,
            description.Depth,
            description.MipLevelCount,
            description.ArrayLayerCount,
            description.SampleCount,
            description.Format,
            description.Usages,
            description.PermittedViewFormats,
            description.Label)
    {
    }

    internal GraphTextureDescription(TextureInfo information, string? label)
        : this(
            information.Dimension,
            information.Width,
            information.Height,
            information.Depth,
            information.MipLevelCount,
            information.ArrayLayerCount,
            information.SampleCount,
            information.Format,
            information.Usages,
            information.PermittedViewFormats,
            label)
    {
    }

    private GraphTextureDescription(
        TextureDimension dimension,
        uint width,
        uint height,
        uint depth,
        uint mipLevelCount,
        uint arrayLayerCount,
        uint sampleCount,
        Format format,
        TextureUsages usages,
        ReadOnlySpan<Format> permittedViewFormats,
        string? label)
    {
        if (!Enum.IsDefined(dimension))
            throw new ArgumentOutOfRangeException(nameof(dimension));
        if (width == 0 || height == 0 || depth == 0)
            throw new ArgumentOutOfRangeException(nameof(width), "Texture extents must be nonzero.");
        if (mipLevelCount == 0 || arrayLayerCount == 0 || sampleCount == 0)
            throw new ArgumentOutOfRangeException(nameof(mipLevelCount), "Texture counts must be nonzero.");
        if (!Enum.IsDefined(format))
            throw new ArgumentOutOfRangeException(nameof(format));
        if (usages == TextureUsages.None || (usages & ~AllTextureUsages) != 0)
            throw new ArgumentOutOfRangeException(nameof(usages));
        if (width > int.MaxValue || height > int.MaxValue || depth > int.MaxValue ||
            mipLevelCount > int.MaxValue || arrayLayerCount > int.MaxValue || sampleCount > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(
                nameof(width),
                "Render Graph requires texture dimensions and subresource counts to fit its signed indexing domain.");
        }

        Dimension = dimension;
        Width = checked((int)width);
        Height = checked((int)height);
        Depth = checked((int)depth);
        MipLevels = checked((int)mipLevelCount);
        ArrayLayers = checked((int)arrayLayerCount);
        SampleCount = checked((int)sampleCount);
        Format = format;
        Usages = usages;
        Label = label;
        _permittedViewFormats = permittedViewFormats.ToArray();
    }

    internal TextureDimension Dimension { get; }
    internal int Width { get; }
    internal int Height { get; }
    internal int Depth { get; }
    internal int MipLevels { get; }
    internal int ArrayLayers { get; }
    internal int SampleCount { get; }
    internal Format Format { get; }
    internal TextureUsages Usages { get; }
    internal string? Label { get; }
    internal ReadOnlySpan<Format> PermittedViewFormats => _permittedViewFormats;

    internal TextureDesc ToRhiDescription() => new(
        Dimension,
        checked((uint)Width),
        checked((uint)Height),
        checked((uint)Depth),
        checked((uint)MipLevels),
        checked((uint)ArrayLayers),
        checked((uint)SampleCount),
        Format,
        Usages,
        _permittedViewFormats,
        Label);

    private const TextureUsages AllTextureUsages =
        TextureUsages.CopySource |
        TextureUsages.CopyDestination |
        TextureUsages.Sampled |
        TextureUsages.Storage |
        TextureUsages.ColorAttachment |
        TextureUsages.DepthStencilAttachment |
        TextureUsages.ShadingRate |
        TextureUsages.SamplerFeedback |
        TextureUsages.Shareable;
}

internal static class GraphDescriptionValidation
{
    private const BufferUsages AllBufferUsages =
        BufferUsages.CopySource |
        BufferUsages.CopyDestination |
        BufferUsages.Constant |
        BufferUsages.ShaderRead |
        BufferUsages.ShaderWrite |
        BufferUsages.Vertex |
        BufferUsages.Index |
        BufferUsages.Indirect |
        BufferUsages.AccelerationStructure |
        BufferUsages.AccelerationStructureInput |
        BufferUsages.Predication |
        BufferUsages.StreamOutput |
        BufferUsages.QueryResolve |
        BufferUsages.Shareable;

    internal static void Validate(in BufferDesc description)
    {
        if (description.Size == 0)
            throw new ArgumentOutOfRangeException(nameof(description), "Buffer size must be nonzero.");
        if (description.Usages == BufferUsages.None ||
            (description.Usages & ~AllBufferUsages) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(description), "Buffer usages are invalid.");
        }
    }
}

internal static class GraphFormat
{
    internal static bool IsDefined(Format format) => Enum.IsDefined(format);

    internal static bool IsDepth(Format format) => format is
        Format.D16UNorm or
        Format.D24UNormS8UInt or
        Format.D32Float or
        Format.D32FloatS8UInt;

    internal static bool HasStencil(Format format) => format is
        Format.D24UNormS8UInt or
        Format.D32FloatS8UInt;

    internal static TextureAspects AllowedAspects(Format format) => format switch
    {
        Format.D16UNorm or Format.D32Float => TextureAspects.Depth,
        Format.D24UNormS8UInt or Format.D32FloatS8UInt =>
            TextureAspects.Depth | TextureAspects.Stencil,
        _ when IsDefined(format) => TextureAspects.Color,
        _ => throw new ArgumentOutOfRangeException(nameof(format)),
    };
}

internal static class GraphTextureViewValidation
{
    internal static void Normalize(
        GraphTextureDescription description,
        TextureSubresourceRange? requestedRange,
        GraphTextureViewUsage usage,
        Format? requestedFormat,
        TextureViewDimension dimension,
        out TextureSubresourceRange range,
        out Format format)
    {
        ArgumentNullException.ThrowIfNull(description);
        const GraphTextureViewUsage allUsages =
            GraphTextureViewUsage.ShaderResource |
            GraphTextureViewUsage.Storage |
            GraphTextureViewUsage.ColorAttachment |
            GraphTextureViewUsage.DepthStencilAttachment |
            GraphTextureViewUsage.ResolveDestination;
        if (usage == GraphTextureViewUsage.None || (usage & ~allUsages) != 0)
            throw new ArgumentOutOfRangeException(nameof(usage));
        if (!Enum.IsDefined(dimension))
            throw new ArgumentOutOfRangeException(nameof(dimension));

        range = AccessNormalizer.NormalizeTexture(description, requestedRange);
        format = requestedFormat ?? description.Format;
        if (!GraphFormat.IsDefined(format))
            throw new ArgumentOutOfRangeException(nameof(requestedFormat));
        if (format != description.Format && !description.PermittedViewFormats.Contains(format))
        {
            throw new ArgumentException(
                $"Format {format} was not declared as a permitted view format for {description.Format}.",
                nameof(requestedFormat));
        }

        ValidateUsage(description, range, usage, format);
        ValidateDimension(description, range, dimension);
    }

    private static void ValidateUsage(
        GraphTextureDescription description,
        in TextureSubresourceRange range,
        GraphTextureViewUsage usage,
        Format format)
    {
        if ((usage & GraphTextureViewUsage.ShaderResource) != 0 &&
            (description.Usages & TextureUsages.Sampled) == 0)
            throw new ArgumentException("A shader-resource view requires Sampled texture usage.", nameof(usage));
        if ((usage & GraphTextureViewUsage.Storage) != 0 &&
            (description.Usages & TextureUsages.Storage) == 0)
            throw new ArgumentException("A storage view requires Storage texture usage.", nameof(usage));
        if ((usage & GraphTextureViewUsage.ColorAttachment) != 0 &&
            (description.Usages & TextureUsages.ColorAttachment) == 0)
            throw new ArgumentException("A color view requires ColorAttachment texture usage.", nameof(usage));
        if ((usage & GraphTextureViewUsage.DepthStencilAttachment) != 0 &&
            (description.Usages & TextureUsages.DepthStencilAttachment) == 0)
            throw new ArgumentException("A depth-stencil view requires DepthStencilAttachment texture usage.", nameof(usage));
        if ((usage & GraphTextureViewUsage.ResolveDestination) != 0 &&
            (description.Usages & TextureUsages.CopyDestination) == 0)
            throw new ArgumentException("A resolve-destination view requires CopyDestination texture usage.", nameof(usage));

        bool depthStencil = GraphFormat.IsDepth(format);
        if ((usage & GraphTextureViewUsage.ColorAttachment) != 0 && depthStencil)
            throw new ArgumentException("A depth-stencil format cannot be used as a color attachment.", nameof(format));
        if ((usage & GraphTextureViewUsage.DepthStencilAttachment) != 0 && !depthStencil)
            throw new ArgumentException("A depth-stencil attachment requires a depth-stencil format.", nameof(format));
        if (depthStencil && (range.Aspects & TextureAspects.Color) != 0 ||
            !depthStencil && range.Aspects != TextureAspects.Color)
            throw new ArgumentException("The view aspects do not match its format.", nameof(range));
    }

    private static void ValidateDimension(
        GraphTextureDescription description,
        in TextureSubresourceRange range,
        TextureViewDimension dimension)
    {
        bool valid = description.Dimension switch
        {
            TextureDimension.Texture1D => dimension is TextureViewDimension.Texture1D or TextureViewDimension.Texture1DArray,
            TextureDimension.Texture2D when description.SampleCount > 1 =>
                dimension is TextureViewDimension.Texture2DMultisampled or TextureViewDimension.Texture2DMultisampledArray,
            TextureDimension.Texture2D => dimension is
                TextureViewDimension.Texture2D or
                TextureViewDimension.Texture2DArray or
                TextureViewDimension.Cube or
                TextureViewDimension.CubeArray,
            TextureDimension.Texture3D => dimension == TextureViewDimension.Texture3D,
            _ => false,
        };
        if (!valid)
            throw new ArgumentException("The texture view dimension is incompatible with the texture.", nameof(dimension));
        if (dimension is TextureViewDimension.Texture1D or
            TextureViewDimension.Texture2D or
            TextureViewDimension.Texture2DMultisampled && range.ArrayLayerCount != 1)
        {
            throw new ArgumentException("A non-array texture view must select one array layer.", nameof(range));
        }
        if (dimension is TextureViewDimension.Cube or TextureViewDimension.CubeArray &&
            (range.FirstArrayLayer % 6 != 0 || range.ArrayLayerCount % 6 != 0))
        {
            throw new ArgumentException("Cube texture views must select complete groups of six array layers.", nameof(range));
        }
        if (description.SampleCount > 1 &&
            (range.FirstMipLevel != 0 || range.MipLevelCount != 1))
        {
            throw new ArgumentException("Multisampled views must select mip level zero only.", nameof(range));
        }
    }
}

internal static class GraphResolveValidation
{
    internal static void Validate(
        ResolveType type,
        GraphTextureAspect aspect,
        uint sourceMipLevel,
        uint sourceArrayLayer,
        uint destinationMipLevel,
        uint destinationArrayLayer,
        GraphTextureDescription source,
        GraphTextureDescription destination)
    {
        if (!Enum.IsDefined(type))
            throw new ArgumentOutOfRangeException(nameof(type));
        if (aspect != GraphTextureAspect.Color)
            throw new NotSupportedException("Render Graph integrated resolves support color subresources only.");
        if (source.Dimension != TextureDimension.Texture2D ||
            destination.Dimension != TextureDimension.Texture2D)
        {
            throw new NotSupportedException("Resolve source and destination must be two-dimensional textures.");
        }
        if (source.Format != destination.Format)
            throw new ArgumentException("Resolve source and destination formats must match exactly.");
        if (source.SampleCount <= 1 || destination.SampleCount != 1)
            throw new ArgumentException("Resolve requires a multisampled source and a single-sampled destination.");
        if ((source.Usages & TextureUsages.CopySource) == 0 ||
            (destination.Usages & TextureUsages.CopyDestination) == 0)
        {
            throw new ArgumentException("Resolve resources require CopySource and CopyDestination usage respectively.");
        }
        if (sourceMipLevel >= (uint)source.MipLevels ||
            sourceArrayLayer >= (uint)source.ArrayLayers ||
            destinationMipLevel >= (uint)destination.MipLevels ||
            destinationArrayLayer >= (uint)destination.ArrayLayers)
        {
            throw new ArgumentOutOfRangeException(nameof(sourceMipLevel), "Resolve subresource is outside its texture.");
        }

        int sourceWidth = Math.Max(1, source.Width >> checked((int)sourceMipLevel));
        int sourceHeight = Math.Max(1, source.Height >> checked((int)sourceMipLevel));
        int destinationWidth = Math.Max(1, destination.Width >> checked((int)destinationMipLevel));
        int destinationHeight = Math.Max(1, destination.Height >> checked((int)destinationMipLevel));
        if (sourceWidth != destinationWidth || sourceHeight != destinationHeight)
            throw new ArgumentException("Resolve source and destination subresources must have identical extents.");
    }
}

public readonly record struct GraphTextureRegion(
    uint MipLevel,
    uint ArrayLayer,
    TextureAspects Aspect,
    uint X,
    uint Y,
    uint Z,
    uint Width,
    uint Height,
    uint Depth = 1);

public readonly record struct GraphTextureCopy(
    GraphTextureRegion Source,
    GraphTextureRegion Destination);

public readonly record struct GraphBufferTextureCopy(
    ulong BufferOffset,
    uint BufferRowPitch,
    uint BufferImageHeight,
    GraphTextureRegion Texture);
