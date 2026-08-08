namespace SomeEngine.RenderGraph;

internal static class AccessNormalizer
{
    public static BufferRange NormalizeBuffer(in BufferDesc desc, BufferRange? requested) =>
        NormalizeBuffer(desc.Size, requested);

    public static BufferRange NormalizeBuffer(ulong bufferSize, BufferRange? requested)
    {
        BufferRange exact = requested ?? new BufferRange(0, bufferSize);
        ulong offset = exact.Offset;
        if (offset >= bufferSize) throw new ArgumentOutOfRangeException(nameof(requested), "Buffer access starts outside the resource.");
        ulong size = exact.Size;
        if (size == 0 || size > bufferSize - offset) throw new ArgumentOutOfRangeException(nameof(requested), "Buffer access exceeds the resource.");
        return new BufferRange(offset, size);
    }

    public static TextureSubresourceRange NormalizeTexture(in GraphTextureDescription desc, TextureSubresourceRange? requested)
    {
        TextureSubresourceRange exact = requested ?? new TextureSubresourceRange(
            0,
            checked((uint)desc.MipLevels),
            0,
            checked((uint)desc.ArrayLayers),
            GraphFormat.AllowedAspects(desc.Format));
        uint firstMip = exact.FirstMipLevel;
        uint firstLayer = exact.FirstArrayLayer;
        if (firstMip >= (uint)desc.MipLevels || firstLayer >= (uint)desc.ArrayLayers)
            throw new ArgumentOutOfRangeException(nameof(requested), "Texture access starts outside the resource.");
        uint mipCount = exact.MipLevelCount;
        uint layerCount = exact.ArrayLayerCount;
        if (mipCount == 0 || mipCount > (uint)desc.MipLevels - firstMip ||
            layerCount == 0 || layerCount > (uint)desc.ArrayLayers - firstLayer)
            throw new ArgumentOutOfRangeException(nameof(requested), "Texture access exceeds the resource.");
        TextureAspects allowed = GraphFormat.AllowedAspects(desc.Format);
        if (exact.Aspects == TextureAspects.None || (exact.Aspects & ~allowed) != 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(requested),
                $"Texture format {desc.Format} exposes only the {allowed} aspect planes.");
        }
        return new TextureSubresourceRange(firstMip, mipCount, firstLayer, layerCount, exact.Aspects);
    }

    public static bool Overlaps(in PassInputData left, in PassInputData right)
    {
        if (left.Resource != right.Resource) return false;
        if (left.IsBuffer)
        {
            ulong leftEnd = checked(left.BufferRange.Offset + left.BufferRange.Size);
            ulong rightEnd = checked(right.BufferRange.Offset + right.BufferRange.Size);
            return left.BufferRange.Offset < rightEnd && right.BufferRange.Offset < leftEnd;
        }

        TextureSubresourceRange a = left.TextureRange;
        TextureSubresourceRange b = right.TextureRange;
        bool mip = a.FirstMipLevel < b.FirstMipLevel + b.MipLevelCount && b.FirstMipLevel < a.FirstMipLevel + a.MipLevelCount;
        bool layer = a.FirstArrayLayer < b.FirstArrayLayer + b.ArrayLayerCount && b.FirstArrayLayer < a.FirstArrayLayer + a.ArrayLayerCount;
        return mip && layer && (a.Aspects & b.Aspects) != 0;
    }

    internal static bool IsReadOnlyDepthLocalRead(in PassInputData left, in PassInputData right) =>
        !left.IsBuffer &&
        !right.IsBuffer &&
        left.Flags == GraphAccess.Read &&
        right.Flags == GraphAccess.Read &&
        (left.State == GraphResourceUsage.DepthRead && right.State == GraphResourceUsage.ShaderResource ||
         left.State == GraphResourceUsage.ShaderResource && right.State == GraphResourceUsage.DepthRead);
}
