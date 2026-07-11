namespace SomeEngine.Graphics;

/// <summary>Strictly normalizes a portable texture subresource range.</summary>
internal static class TextureSubresourceRangeValidation
{
    public static void Normalize(
        in TextureDesc desc,
        in TextureSubresourceRange range,
        out int firstMip,
        out int mipCount,
        out int firstLayer,
        out int layerCount,
        out TextureAspect aspects)
    {
        bool whole = range == default;
        firstMip = whole ? 0 : range.FirstMip;
        firstLayer = whole ? 0 : range.FirstLayer;

        // Validate the first indices before subtracting so sentinel expansion cannot turn an
        // invalid negative/out-of-range index into an apparently valid count.
        if ((uint)firstMip >= (uint)desc.MipLevels || (uint)firstLayer >= (uint)desc.ArrayLayers)
            throw new ArgumentOutOfRangeException(nameof(range), "Texture subresource range starts outside the resource.");

        mipCount = whole || range.MipCount == int.MaxValue
            ? desc.MipLevels - firstMip
            : range.MipCount;
        layerCount = whole || range.LayerCount == int.MaxValue
            ? desc.ArrayLayers - firstLayer
            : range.LayerCount;
        TextureAspect allowed = AllowedAspects(desc.Format);
        aspects = whole ? allowed : range.Aspect;

        if (mipCount <= 0 || mipCount > desc.MipLevels - firstMip ||
            layerCount <= 0 || layerCount > desc.ArrayLayers - firstLayer ||
            aspects == 0 || (aspects & ~allowed) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(range), "Texture subresource range exceeds the resource.");
        }
    }

    public static TextureAspect AllowedAspects(Format format) => format switch
    {
        Format.D24UNormS8UInt => TextureAspect.Depth | TextureAspect.Stencil,
        Format.D32Float => TextureAspect.Depth,
        _ => TextureAspect.Color,
    };
}
