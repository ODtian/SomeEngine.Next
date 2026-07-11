namespace SomeEngine.RenderGraph;

internal static class AccessNormalizer
{
    public static FrozenAccess[] Normalize(FrozenResource[] resources, FrozenAccess[] accesses)
    {
        FrozenAccess[] normalized = new FrozenAccess[accesses.Length];
        for (int index = 0; index < accesses.Length; index++)
        {
            FrozenAccess access = accesses[index];
            FrozenResource resource = resources[access.Resource];
            normalized[index] = access.Kind == ResourceNodeKind.Buffer
                ? access with { BufferRange = NormalizeBuffer(resource.BufferDesc, access.BufferRange) }
                : access with { TextureRange = NormalizeTexture(resource.TextureDesc, access.TextureRange) };
        }
        return normalized;
    }

    public static BufferRange NormalizeBuffer(in BufferDesc desc, in BufferRange requested)
    {
        ulong offset = requested.Offset;
        if (offset >= desc.Size) throw new ArgumentOutOfRangeException(nameof(requested), "Buffer access starts outside the resource.");
        ulong size = requested.Size == ulong.MaxValue ? desc.Size - offset : requested.Size;
        if (size == 0 || size > desc.Size - offset) throw new ArgumentOutOfRangeException(nameof(requested), "Buffer access exceeds the resource.");
        return new BufferRange(offset, size);
    }

    public static TextureSubresourceRange NormalizeTexture(in TextureDesc desc, in TextureSubresourceRange requested)
    {
        int firstMip = requested.FirstMip;
        int firstLayer = requested.FirstLayer;
        if ((uint)firstMip >= (uint)desc.MipLevels || (uint)firstLayer >= (uint)desc.ArrayLayers)
            throw new ArgumentOutOfRangeException(nameof(requested), "Texture access starts outside the resource.");
        int mipCount = requested.MipCount == int.MaxValue ? desc.MipLevels - firstMip : requested.MipCount;
        int layerCount = requested.LayerCount == int.MaxValue ? desc.ArrayLayers - firstLayer : requested.LayerCount;
        if (mipCount <= 0 || mipCount > desc.MipLevels - firstMip || layerCount <= 0 || layerCount > desc.ArrayLayers - firstLayer)
            throw new ArgumentOutOfRangeException(nameof(requested), "Texture access exceeds the resource.");
        TextureAspect allowed = desc.Format switch
        {
            Format.D32Float => TextureAspect.Depth,
            Format.D24UNormS8UInt => TextureAspect.Depth | TextureAspect.Stencil,
            _ => TextureAspect.Color,
        };
        if (requested.Aspect == 0 || (requested.Aspect & ~allowed) != 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(requested),
                $"Texture format {desc.Format} exposes only the {allowed} aspect planes.");
        }
        return new TextureSubresourceRange(firstMip, mipCount, firstLayer, layerCount, requested.Aspect);
    }

    public static bool Overlaps(in FrozenAccess left, in FrozenAccess right)
    {
        if (left.Kind != right.Kind || left.Resource != right.Resource) return false;
        if (left.Kind == ResourceNodeKind.Buffer)
        {
            ulong leftEnd = checked(left.BufferRange.Offset + left.BufferRange.Size);
            ulong rightEnd = checked(right.BufferRange.Offset + right.BufferRange.Size);
            return left.BufferRange.Offset < rightEnd && right.BufferRange.Offset < leftEnd;
        }

        TextureSubresourceRange a = left.TextureRange;
        TextureSubresourceRange b = right.TextureRange;
        bool mip = a.FirstMip < b.FirstMip + b.MipCount && b.FirstMip < a.FirstMip + a.MipCount;
        bool layer = a.FirstLayer < b.FirstLayer + b.LayerCount && b.FirstLayer < a.FirstLayer + a.LayerCount;
        return mip && layer && (a.Aspect & b.Aspect) != 0;
    }
}
