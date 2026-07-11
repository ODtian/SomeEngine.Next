namespace SomeEngine.Graphics.Null;

internal static class TextureLayout
{
    public static ulong GetByteSize(in TextureDesc desc)
    {
        ulong total = 0;
        for (int layer = 0; layer < desc.ArrayLayers; layer++)
        {
            for (int mip = 0; mip < desc.MipLevels; mip++)
            {
                total = checked(total + GetMipByteSize(desc, mip));
            }
        }

        return total;
    }

    public static ulong GetMipByteSize(in TextureDesc desc, int mip)
    {
        (int width, int height, int depth) = GetMipExtent(desc, mip);
        ulong texels = checked((ulong)width * (ulong)height * (ulong)depth * (ulong)desc.SampleCount);
        ulong bytes = 0;
        foreach (TextureAspect aspect in EnumerateAspects(desc.Format))
        {
            bytes = checked(bytes + texels * (ulong)GetBytesPerTexel(desc.Format, aspect));
        }
        return bytes;
    }

    public static ulong GetSubresourceOffset(in TextureDesc desc, int mip, int layer, TextureAspect aspect)
    {
        ValidateSubresource(desc, mip, layer);
        ValidateSingleAspect(desc.Format, aspect);
        ulong offset = 0;
        for (int currentLayer = 0; currentLayer < layer; currentLayer++)
        {
            for (int currentMip = 0; currentMip < desc.MipLevels; currentMip++)
            {
                offset = checked(offset + GetMipByteSize(desc, currentMip));
            }
        }

        for (int currentMip = 0; currentMip < mip; currentMip++)
        {
            offset = checked(offset + GetMipByteSize(desc, currentMip));
        }

        (int width, int height, int depth) = GetMipExtent(desc, mip);
        ulong texels = checked((ulong)width * (ulong)height * (ulong)depth * (ulong)desc.SampleCount);
        foreach (TextureAspect plane in EnumerateAspects(desc.Format))
        {
            if (plane == aspect) break;
            offset = checked(offset + texels * (ulong)GetBytesPerTexel(desc.Format, plane));
        }

        return offset;
    }

    public static (int Width, int Height, int Depth) GetMipExtent(in TextureDesc desc, int mip)
    {
        if ((uint)mip >= (uint)desc.MipLevels) throw new ArgumentOutOfRangeException(nameof(mip));
        return (
            Math.Max(1, desc.Width >> mip),
            Math.Max(1, desc.Height >> mip),
            Math.Max(1, desc.Depth >> mip));
    }

    public static int GetBytesPerTexel(Format format, TextureAspect aspect)
    {
        ValidateSingleAspect(format, aspect);
        return (format, aspect) switch
        {
            (Format.R8UNorm, TextureAspect.Color) => 1,
            (Format.R8G8UNorm or Format.R16UInt or Format.R16Float, TextureAspect.Color) => 2,
            (Format.R8G8B8A8UNorm or Format.R8G8B8A8UNormSrgb or Format.B8G8R8A8UNorm or
                Format.R16G16Float or Format.R32UInt or Format.R32Float, TextureAspect.Color) => 4,
            (Format.R16G16B16A16Float or Format.R32G32Float, TextureAspect.Color) => 8,
            (Format.R32G32B32Float, TextureAspect.Color) => 12,
            (Format.R32G32B32A32Float, TextureAspect.Color) => 16,
            (Format.D32Float, TextureAspect.Depth) => 4,
            (Format.D24UNormS8UInt, TextureAspect.Depth) => 4,
            (Format.D24UNormS8UInt, TextureAspect.Stencil) => 1,
            _ => throw new ArgumentOutOfRangeException(nameof(format), format, "Unsupported texture format/aspect."),
        };
    }

    public static IEnumerable<int> EnumerateSubresources(TextureDesc desc, TextureSubresourceRange range)
    {
        NormalizeRange(desc, range, out int firstMip, out int mipCount, out int firstLayer, out int layerCount, out TextureAspect aspects);
        foreach (TextureAspect aspect in EnumerateAspects(aspects))
        {
            int plane = GetPlaneIndex(desc.Format, aspect);
            for (int layer = firstLayer; layer < firstLayer + layerCount; layer++)
            {
                for (int mip = firstMip; mip < firstMip + mipCount; mip++)
                {
                    yield return checked(mip + layer * desc.MipLevels + plane * desc.MipLevels * desc.ArrayLayers);
                }
            }
        }
    }

    public static void NormalizeRange(
        in TextureDesc desc,
        in TextureSubresourceRange range,
        out int firstMip,
        out int mipCount,
        out int firstLayer,
        out int layerCount) =>
        NormalizeRange(desc, range, out firstMip, out mipCount, out firstLayer, out layerCount, out _);

    public static void NormalizeRange(
        in TextureDesc desc,
        in TextureSubresourceRange range,
        out int firstMip,
        out int mipCount,
        out int firstLayer,
        out int layerCount,
        out TextureAspect aspects)
        => TextureSubresourceRangeValidation.Normalize(
            desc,
            range,
            out firstMip,
            out mipCount,
            out firstLayer,
            out layerCount,
            out aspects);

    public static (int Width, int Height, int Depth, int BytesPerTexel) ValidateCopyRegion(
        in TextureDesc desc,
        in TextureCopyRegion region)
    {
        if (desc.SampleCount != 1) throw new NotSupportedException("Multisampled textures must be resolved before linear-buffer copies.");
        ValidateSingleAspect(desc.Format, region.Aspect);
        if ((uint)region.MipLevel >= (uint)desc.MipLevels) throw new ArgumentOutOfRangeException(nameof(region));
        if (desc.Dimension == TextureDimension.Texture3D)
        {
            if (region.ArrayLayer != 0) throw new ArgumentOutOfRangeException(nameof(region), "Three-dimensional textures do not expose array layers.");
        }
        else if ((uint)region.ArrayLayer >= (uint)desc.ArrayLayers)
        {
            throw new ArgumentOutOfRangeException(nameof(region));
        }

        (int mipWidth, int mipHeight, int mipDepth) = GetMipExtent(desc, region.MipLevel);
        if (region.X < 0 || region.Y < 0 || region.Z < 0 ||
            region.Width <= 0 || region.Height <= 0 || region.Depth <= 0 ||
            region.X > mipWidth - region.Width ||
            region.Y > mipHeight - region.Height ||
            region.Z > mipDepth - region.Depth)
        {
            throw new ArgumentOutOfRangeException(nameof(region), "Texture copy region exceeds the selected mip.");
        }
        if (desc.Dimension == TextureDimension.Texture1D && (region.Y != 0 || region.Height != 1))
            throw new ArgumentOutOfRangeException(nameof(region), "A one-dimensional texture copy has Y=0 and Height=1.");
        if (desc.Dimension != TextureDimension.Texture3D && (region.Z != 0 || region.Depth != 1))
            throw new ArgumentOutOfRangeException(nameof(region), "A non-3D texture copy has Z=0 and Depth=1.");
        return (region.Width, region.Height, region.Depth, GetBytesPerTexel(desc.Format, region.Aspect));
    }

    public static int GetStateCount(in TextureDesc desc) =>
        checked(desc.MipLevels * desc.ArrayLayers * GetPlaneCount(desc.Format));

    public static int GetPlaneCount(Format format) => format == Format.D24UNormS8UInt ? 2 : 1;

    public static int GetPlaneIndex(Format format, TextureAspect aspect)
    {
        ValidateSingleAspect(format, aspect);
        return aspect == TextureAspect.Stencil ? 1 : 0;
    }

    public static TextureAspect AllowedAspects(Format format) =>
        TextureSubresourceRangeValidation.AllowedAspects(format);

    public static IEnumerable<TextureAspect> EnumerateAspects(Format format) => EnumerateAspects(AllowedAspects(format));

    public static IEnumerable<TextureAspect> EnumerateAspects(TextureAspect aspects)
    {
        if ((aspects & TextureAspect.Color) != 0) yield return TextureAspect.Color;
        if ((aspects & TextureAspect.Depth) != 0) yield return TextureAspect.Depth;
        if ((aspects & TextureAspect.Stencil) != 0) yield return TextureAspect.Stencil;
    }

    public static void ValidateSingleAspect(Format format, TextureAspect aspect)
    {
        TextureAspect allowed = AllowedAspects(format);
        byte bits = (byte)aspect;
        if (bits == 0 || (bits & (bits - 1)) != 0 || (aspect & allowed) == 0)
            throw new ArgumentOutOfRangeException(nameof(aspect), $"Format {format} does not expose the single plane {aspect}.");
    }

    public static bool IsDepth(Format format) => format is Format.D24UNormS8UInt or Format.D32Float;

    private static void ValidateSubresource(in TextureDesc desc, int mip, int layer)
    {
        if ((uint)mip >= (uint)desc.MipLevels) throw new ArgumentOutOfRangeException(nameof(mip));
        if ((uint)layer >= (uint)desc.ArrayLayers) throw new ArgumentOutOfRangeException(nameof(layer));
    }
}
