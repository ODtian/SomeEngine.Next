namespace SomeEngine.Graphics;

internal readonly record struct ValidatedTextureViewDescription(
    TextureSubresourceRange Range,
    TextureViewUsage Usage,
    Format Format,
    TextureViewDimension Dimension);

internal static class TextureViewValidation
{
    private const TextureViewUsage AllUsage =
        TextureViewUsage.ShaderResource |
        TextureViewUsage.Storage |
        TextureViewUsage.ColorAttachment |
        TextureViewUsage.DepthStencilAttachment;

    public static ValidatedTextureViewDescription Validate(
        in TextureDesc texture,
        in TextureSubresourceRange requestedRange,
        TextureViewUsage usage,
        Format requestedFormat,
        TextureViewDimension dimension)
    {
        texture.Validate();
        if (usage == 0 || (usage & ~AllUsage) != 0) throw new ArgumentOutOfRangeException(nameof(usage));
        if (!Enum.IsDefined(dimension)) throw new ArgumentOutOfRangeException(nameof(dimension));

        Format format = requestedFormat == Format.Unknown ? texture.Format : requestedFormat;
        if (!Enum.IsDefined(format) || format == Format.Unknown || !texture.AllowsViewFormat(format))
            throw new ArgumentException($"Texture view format {format} is not in the resource's allowed view-format set.", nameof(requestedFormat));

        ValidateUsage(texture, usage, format);
        TextureSubresourceRange range = NormalizeRange(texture, requestedRange, usage, format);
        ValidateDimension(texture, range, usage, dimension);
        return new ValidatedTextureViewDescription(range, usage, format, dimension);
    }

    private static void ValidateUsage(in TextureDesc texture, TextureViewUsage usage, Format format)
    {
        ValidateResourceUsage(texture, usage);
        ValidateFormatUsage(usage, format);
    }

    private static void ValidateResourceUsage(in TextureDesc texture, TextureViewUsage usage)
    {
        if ((usage & TextureViewUsage.ShaderResource) != 0 && (texture.Usage & TextureUsage.Sampled) == 0)
            throw new ArgumentException("Shader-resource view usage requires a sampled texture.", nameof(usage));
        if ((usage & TextureViewUsage.Storage) != 0 && (texture.Usage & TextureUsage.Storage) == 0)
            throw new ArgumentException("Storage view usage requires a storage texture.", nameof(usage));
        if ((usage & TextureViewUsage.ColorAttachment) != 0 && (texture.Usage & TextureUsage.ColorAttachment) == 0)
            throw new ArgumentException("Color-attachment view usage requires a color-attachment texture.", nameof(usage));
        if ((usage & TextureViewUsage.DepthStencilAttachment) != 0 &&
            (texture.Usage & TextureUsage.DepthStencilAttachment) == 0)
        {
            throw new ArgumentException("Depth-stencil view usage requires a depth-stencil texture.", nameof(usage));
        }
    }

    private static void ValidateFormatUsage(TextureViewUsage usage, Format format)
    {
        bool depth = IsDepth(format);
        if (depth && (usage & (TextureViewUsage.ColorAttachment | TextureViewUsage.Storage)) != 0)
            throw new ArgumentException("A depth format cannot have color-attachment or storage view usage.", nameof(usage));
        if (!depth && (usage & TextureViewUsage.DepthStencilAttachment) != 0)
            throw new ArgumentException("A color format cannot have depth-stencil view usage.", nameof(usage));
        if (format == Format.R8G8B8A8UNormSrgb && (usage & TextureViewUsage.Storage) != 0)
            throw new ArgumentException("An sRGB view cannot be a storage view.", nameof(usage));
    }

    private static TextureSubresourceRange NormalizeRange(
        in TextureDesc texture,
        in TextureSubresourceRange requested,
        TextureViewUsage usage,
        Format format)
    {
        TextureAspect allowed = AllowedAspects(format);
        TextureAspect defaultAspect = GetDefaultAspect(format, usage, allowed);
        bool whole = requested == default;
        int firstMip = whole ? 0 : requested.FirstMip;
        int firstLayer = whole ? 0 : requested.FirstLayer;
        int mipCount = ResolveCount(whole, requested.MipCount, texture.MipLevels, firstMip);
        int layerCount = ResolveCount(whole, requested.LayerCount, texture.ArrayLayers, firstLayer);
        TextureAspect aspect = whole ? defaultAspect : requested.Aspect;

        ValidateRange(texture, requested, firstMip, mipCount, firstLayer, layerCount);
        ValidateAspect(requested, usage, aspect, allowed);
        return new TextureSubresourceRange(firstMip, mipCount, firstLayer, layerCount, aspect);
    }

    private static TextureAspect GetDefaultAspect(
        Format format,
        TextureViewUsage usage,
        TextureAspect allowed)
    {
        if (format == Format.D24UNormS8UInt &&
            (usage & TextureViewUsage.DepthStencilAttachment) != 0)
        {
            return TextureAspect.Depth | TextureAspect.Stencil;
        }

        return allowed == TextureAspect.Color ? TextureAspect.Color : TextureAspect.Depth;
    }

    private static int ResolveCount(bool whole, int requested, int available, int first) =>
        whole || requested == int.MaxValue ? available - first : requested;

    private static void ValidateRange(
        in TextureDesc texture,
        in TextureSubresourceRange requested,
        int firstMip,
        int mipCount,
        int firstLayer,
        int layerCount)
    {
        if (firstMip < 0 || mipCount <= 0 || firstMip > texture.MipLevels - mipCount)
            throw new ArgumentOutOfRangeException(nameof(requested), "Texture view mip range exceeds the resource.");
        if (firstLayer < 0 || layerCount <= 0 || firstLayer > texture.ArrayLayers - layerCount)
            throw new ArgumentOutOfRangeException(nameof(requested), "Texture view layer range exceeds the resource.");
    }

    private static void ValidateAspect(
        in TextureSubresourceRange requested,
        TextureViewUsage usage,
        TextureAspect aspect,
        TextureAspect allowed)
    {
        if (aspect == 0 || (aspect & ~allowed) != 0)
            throw new ArgumentException("Texture view aspect is not exposed by its view format.", nameof(requested));
        if ((usage & TextureViewUsage.ShaderResource) != 0 &&
            aspect != TextureAspect.Color &&
            aspect != TextureAspect.Depth &&
            aspect != TextureAspect.Stencil)
        {
            throw new ArgumentException("A shader-resource view selects exactly one texture aspect.", nameof(requested));
        }
    }

    private static void ValidateDimension(
        in TextureDesc texture,
        in TextureSubresourceRange range,
        TextureViewUsage usage,
        TextureViewDimension dimension)
    {
        bool attachment = (usage & (TextureViewUsage.ColorAttachment | TextureViewUsage.DepthStencilAttachment)) != 0;
        bool storage = (usage & TextureViewUsage.Storage) != 0;
        if ((attachment || storage) && range.MipCount != 1)
            throw new ArgumentException("Attachment and storage views select exactly one mip level.", nameof(range));

        switch (dimension)
        {
            case TextureViewDimension.Texture1D:
                RequireResourceDimension(texture, TextureDimension.Texture1D, dimension);
                RequireSingleSample(texture, dimension);
                RequireFirstLayer(range, dimension);
                break;
            case TextureViewDimension.Texture1DArray:
                RequireResourceDimension(texture, TextureDimension.Texture1D, dimension);
                RequireSingleSample(texture, dimension);
                break;
            case TextureViewDimension.Texture2D:
                RequireResourceDimension(texture, TextureDimension.Texture2D, dimension);
                RequireSingleSample(texture, dimension);
                RequireFirstLayer(range, dimension);
                break;
            case TextureViewDimension.Texture2DArray:
                RequireResourceDimension(texture, TextureDimension.Texture2D, dimension);
                RequireSingleSample(texture, dimension);
                break;
            case TextureViewDimension.Texture2DMS:
                RequireResourceDimension(texture, TextureDimension.Texture2D, dimension);
                RequireMultisample(texture, range, dimension);
                RequireFirstLayer(range, dimension);
                break;
            case TextureViewDimension.Texture2DMSArray:
                RequireResourceDimension(texture, TextureDimension.Texture2D, dimension);
                RequireMultisample(texture, range, dimension);
                break;
            case TextureViewDimension.Cube:
                ValidateCube(texture, range, usage, dimension, array: false);
                break;
            case TextureViewDimension.CubeArray:
                ValidateCube(texture, range, usage, dimension, array: true);
                break;
            case TextureViewDimension.Texture3D:
                RequireResourceDimension(texture, TextureDimension.Texture3D, dimension);
                RequireSingleSample(texture, dimension);
                if (range.FirstLayer != 0 || range.LayerCount != 1)
                    throw new ArgumentException("A 3D view addresses the volume rather than array or W slices.", nameof(range));
                if (attachment)
                    throw new NotSupportedException("Three-dimensional W-slice attachments are not part of the portable view contract.");
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(dimension));
        }

        if (texture.SampleCount > 1 && storage)
            throw new ArgumentException("A multisampled texture cannot have a storage view.", nameof(usage));
    }

    private static void ValidateCube(
        in TextureDesc texture,
        in TextureSubresourceRange range,
        TextureViewUsage usage,
        TextureViewDimension dimension,
        bool array)
    {
        RequireResourceDimension(texture, TextureDimension.Texture2D, dimension);
        RequireSingleSample(texture, dimension);
        if (!texture.CubeCompatible)
            throw new ArgumentException("Cube views require a cube-compatible texture resource.", nameof(texture));
        if (usage != TextureViewUsage.ShaderResource)
            throw new ArgumentException("Cube and cube-array dimensions are shader-resource views only.", nameof(usage));
        if (range.FirstLayer % 6 != 0 || range.LayerCount < 6 || range.LayerCount % 6 != 0)
            throw new ArgumentException("Cube views select six-layer-aligned complete cubes.", nameof(range));
        if (!array && (range.FirstLayer != 0 || range.LayerCount != 6))
            throw new ArgumentException("A non-array cube view selects the resource's first six layers.", nameof(range));
    }

    private static void RequireResourceDimension(
        in TextureDesc texture,
        TextureDimension expected,
        TextureViewDimension view)
    {
        if (texture.Dimension != expected)
            throw new ArgumentException($"View dimension {view} is incompatible with resource dimension {texture.Dimension}.", nameof(view));
    }

    private static void RequireFirstLayer(in TextureSubresourceRange range, TextureViewDimension dimension)
    {
        if (range.FirstLayer != 0 || range.LayerCount != 1)
        {
            throw new ArgumentException(
                $"Non-array view dimension {dimension} selects only array layer zero; use its array dimension for any other layer.",
                nameof(range));
        }
    }

    private static void RequireSingleSample(in TextureDesc texture, TextureViewDimension dimension)
    {
        if (texture.SampleCount != 1)
            throw new ArgumentException($"View dimension {dimension} requires a single-sampled texture.", nameof(dimension));
    }

    private static void RequireMultisample(
        in TextureDesc texture,
        in TextureSubresourceRange range,
        TextureViewDimension dimension)
    {
        if (texture.SampleCount <= 1 || range.FirstMip != 0 || range.MipCount != 1)
            throw new ArgumentException($"View dimension {dimension} requires a multisampled mip-zero texture.", nameof(dimension));
    }

    private static TextureAspect AllowedAspects(Format format) => format switch
    {
        Format.D24UNormS8UInt => TextureAspect.Depth | TextureAspect.Stencil,
        Format.D32Float => TextureAspect.Depth,
        _ => TextureAspect.Color,
    };

    private static bool IsDepth(Format format) => format is Format.D24UNormS8UInt or Format.D32Float;
}
