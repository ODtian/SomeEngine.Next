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
        firstMip = ResolveStart(whole, range.FirstMip);
        firstLayer = ResolveStart(whole, range.FirstLayer);

        // Validate the first indices before subtracting so sentinel expansion cannot turn an
        // invalid negative/out-of-range index into an apparently valid count.
        ValidateStarts(desc, range, firstMip, firstLayer);

        mipCount = ResolveCount(whole, range.MipCount, desc.MipLevels, firstMip);
        layerCount = ResolveCount(whole, range.LayerCount, desc.ArrayLayers, firstLayer);
        TextureAspect allowed = AllowedAspects(desc.Format);
        aspects = whole ? allowed : range.Aspect;
        ValidateResolvedRange(desc, range, firstMip, mipCount, firstLayer, layerCount, aspects, allowed);
    }

    private static int ResolveStart(bool whole, int requested) => whole ? 0 : requested;

    private static int ResolveCount(bool whole, int requested, int available, int first) =>
        whole || requested == int.MaxValue ? available - first : requested;

    private static void ValidateStarts(
        in TextureDesc desc,
        in TextureSubresourceRange range,
        int firstMip,
        int firstLayer)
    {
        if ((uint)firstMip >= (uint)desc.MipLevels || (uint)firstLayer >= (uint)desc.ArrayLayers)
            throw new ArgumentOutOfRangeException(nameof(range), "Texture subresource range starts outside the resource.");
    }

    private static void ValidateResolvedRange(
        in TextureDesc desc,
        in TextureSubresourceRange range,
        int firstMip,
        int mipCount,
        int firstLayer,
        int layerCount,
        TextureAspect aspects,
        TextureAspect allowed)
    {
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
