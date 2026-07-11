namespace SomeEngine.Graphics;

internal static class TextureResolveValidation
{
    public static void Validate(
        in TextureResolveRegion resolve,
        in TextureDesc source,
        in TextureDesc destination)
    {
        if (!Enum.IsDefined(resolve.Mode))
            throw new ArgumentOutOfRangeException(nameof(resolve), "The resolve mode is not defined.");
        if (resolve.Mode != ResolveMode.Average)
        {
            throw new NotSupportedException(
                $"Resolve mode {resolve.Mode} is not part of the portable color-resolve surface yet.");
        }
        if (resolve.Aspect != TextureAspect.Color)
            throw new NotSupportedException("The portable resolve surface currently supports only color subresources.");
        if (source.Dimension != TextureDimension.Texture2D || destination.Dimension != TextureDimension.Texture2D)
        {
            throw new NotSupportedException(
                "The portable resolve surface requires explicitly two-dimensional source and destination textures.");
        }
        if (!SupportsAverageColor(source.Format))
        {
            throw new NotSupportedException(
                $"Format {source.Format} does not support the portable normalized/floating-point Average resolve.");
        }
        if (source.Format != destination.Format)
            throw new ArgumentException("Resolve source and destination formats must match exactly.", nameof(resolve));
        if (source.SampleCount <= 1)
            throw new ArgumentException("Resolve source must be multisampled.", nameof(resolve));
        if (destination.SampleCount != 1)
            throw new ArgumentException("Resolve destination must be single-sampled.", nameof(resolve));
        if ((source.Usage & TextureUsage.CopySource) == 0)
            throw new ArgumentException("Resolve source is missing CopySource usage.", nameof(resolve));
        if ((destination.Usage & TextureUsage.CopyDestination) == 0)
            throw new ArgumentException("Resolve destination is missing CopyDestination usage.", nameof(resolve));

        ValidateSubresource(source, resolve.SourceMipLevel, resolve.SourceArrayLayer, "source");
        ValidateSubresource(destination, resolve.DestinationMipLevel, resolve.DestinationArrayLayer, "destination");

        int sourceWidth = Math.Max(1, source.Width >> resolve.SourceMipLevel);
        int sourceHeight = Math.Max(1, source.Height >> resolve.SourceMipLevel);
        int destinationWidth = Math.Max(1, destination.Width >> resolve.DestinationMipLevel);
        int destinationHeight = Math.Max(1, destination.Height >> resolve.DestinationMipLevel);
        if (sourceWidth != destinationWidth || sourceHeight != destinationHeight)
        {
            throw new ArgumentException(
                "Resolve source and destination subresources must have identical extents.",
                nameof(resolve));
        }
    }

    public static bool SupportsAverageColor(Format format) => format is
        Format.R8UNorm or
        Format.R8G8UNorm or
        Format.R8G8B8A8UNorm or
        Format.B8G8R8A8UNorm or
        Format.R16Float or
        Format.R16G16Float or
        Format.R16G16B16A16Float or
        Format.R32Float or
        Format.R32G32Float or
        Format.R32G32B32Float or
        Format.R32G32B32A32Float;

    private static void ValidateSubresource(
        in TextureDesc desc,
        int mipLevel,
        int arrayLayer,
        string role)
    {
        if ((uint)mipLevel >= (uint)desc.MipLevels)
            throw new ArgumentOutOfRangeException(nameof(mipLevel), $"Resolve {role} mip level is out of range.");
        if ((uint)arrayLayer >= (uint)desc.ArrayLayers)
            throw new ArgumentOutOfRangeException(nameof(arrayLayer), $"Resolve {role} array layer is out of range.");
    }
}
