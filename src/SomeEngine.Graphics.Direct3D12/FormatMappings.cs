using DxgiFormat = Silk.NET.DXGI.Format;

namespace SomeEngine.Graphics.Direct3D12;

internal static class FormatMappings
{
    internal static DxgiFormat ToDxgi(Format format) => format switch
    {
        Format.R8UNorm => DxgiFormat.FormatR8Unorm,
        Format.R8SNorm => DxgiFormat.FormatR8SNorm,
        Format.R8UInt => DxgiFormat.FormatR8Uint,
        Format.R8SInt => DxgiFormat.FormatR8Sint,
        Format.R8G8UNorm => DxgiFormat.FormatR8G8Unorm,
        Format.R8G8SNorm => DxgiFormat.FormatR8G8SNorm,
        Format.R8G8UInt => DxgiFormat.FormatR8G8Uint,
        Format.R8G8SInt => DxgiFormat.FormatR8G8Sint,
        Format.R8G8B8A8UNorm => DxgiFormat.FormatR8G8B8A8Unorm,
        Format.R8G8B8A8UNormSrgb => DxgiFormat.FormatR8G8B8A8UnormSrgb,
        Format.R8G8B8A8SNorm => DxgiFormat.FormatR8G8B8A8SNorm,
        Format.R8G8B8A8UInt => DxgiFormat.FormatR8G8B8A8Uint,
        Format.R8G8B8A8SInt => DxgiFormat.FormatR8G8B8A8Sint,
        Format.B8G8R8A8UNorm => DxgiFormat.FormatB8G8R8A8Unorm,
        Format.B8G8R8A8UNormSrgb => DxgiFormat.FormatB8G8R8A8UnormSrgb,
        Format.R10G10B10A2UNorm => DxgiFormat.FormatR10G10B10A2Unorm,
        Format.R11G11B10Float => DxgiFormat.FormatR11G11B10Float,
        Format.R16UNorm => DxgiFormat.FormatR16Unorm,
        Format.R16SNorm => DxgiFormat.FormatR16SNorm,
        Format.R16UInt => DxgiFormat.FormatR16Uint,
        Format.R16SInt => DxgiFormat.FormatR16Sint,
        Format.R16Float => DxgiFormat.FormatR16Float,
        Format.R16G16UNorm => DxgiFormat.FormatR16G16Unorm,
        Format.R16G16SNorm => DxgiFormat.FormatR16G16SNorm,
        Format.R16G16UInt => DxgiFormat.FormatR16G16Uint,
        Format.R16G16SInt => DxgiFormat.FormatR16G16Sint,
        Format.R16G16Float => DxgiFormat.FormatR16G16Float,
        Format.R16G16B16A16UNorm => DxgiFormat.FormatR16G16B16A16Unorm,
        Format.R16G16B16A16SNorm => DxgiFormat.FormatR16G16B16A16SNorm,
        Format.R16G16B16A16UInt => DxgiFormat.FormatR16G16B16A16Uint,
        Format.R16G16B16A16SInt => DxgiFormat.FormatR16G16B16A16Sint,
        Format.R16G16B16A16Float => DxgiFormat.FormatR16G16B16A16Float,
        Format.R32UInt => DxgiFormat.FormatR32Uint,
        Format.R32SInt => DxgiFormat.FormatR32Sint,
        Format.R32Float => DxgiFormat.FormatR32Float,
        Format.R32G32UInt => DxgiFormat.FormatR32G32Uint,
        Format.R32G32SInt => DxgiFormat.FormatR32G32Sint,
        Format.R32G32Float => DxgiFormat.FormatR32G32Float,
        Format.R32G32B32Float => DxgiFormat.FormatR32G32B32Float,
        Format.R32G32B32A32UInt => DxgiFormat.FormatR32G32B32A32Uint,
        Format.R32G32B32A32SInt => DxgiFormat.FormatR32G32B32A32Sint,
        Format.R32G32B32A32Float => DxgiFormat.FormatR32G32B32A32Float,
        Format.D16UNorm => DxgiFormat.FormatD16Unorm,
        Format.D24UNormS8UInt => DxgiFormat.FormatD24UnormS8Uint,
        Format.D32Float => DxgiFormat.FormatD32Float,
        Format.D32FloatS8UInt => DxgiFormat.FormatD32FloatS8X24Uint,
        Format.BC1UNorm => DxgiFormat.FormatBC1Unorm,
        Format.BC1UNormSrgb => DxgiFormat.FormatBC1UnormSrgb,
        Format.BC2UNorm => DxgiFormat.FormatBC2Unorm,
        Format.BC2UNormSrgb => DxgiFormat.FormatBC2UnormSrgb,
        Format.BC3UNorm => DxgiFormat.FormatBC3Unorm,
        Format.BC3UNormSrgb => DxgiFormat.FormatBC3UnormSrgb,
        Format.BC4UNorm => DxgiFormat.FormatBC4Unorm,
        Format.BC4SNorm => DxgiFormat.FormatBC4SNorm,
        Format.BC5UNorm => DxgiFormat.FormatBC5Unorm,
        Format.BC5SNorm => DxgiFormat.FormatBC5SNorm,
        Format.BC6HUFloat => DxgiFormat.FormatBC6HUF16,
        Format.BC6HSFloat => DxgiFormat.FormatBC6HSF16,
        Format.BC7UNorm => DxgiFormat.FormatBC7Unorm,
        Format.BC7UNormSrgb => DxgiFormat.FormatBC7UnormSrgb,
        _ => throw new ArgumentOutOfRangeException(nameof(format)),
    };

    internal static DxgiFormat ToResourceFormat(
        Format format,
        ReadOnlySpan<Format> permittedViewFormats)
    {
        bool requiresTypeless = IsDepthStencil(format);
        foreach (Format permitted in permittedViewFormats)
        {
            _ = ToDxgi(permitted);
            requiresTypeless |= permitted != format;
        }

        return requiresTypeless ? ToTypeless(format) : ToDxgi(format);
    }

    internal static DxgiFormat ToShaderViewFormat(Format format, TextureAspects aspects) =>
        (format, aspects) switch
        {
            (Format.D16UNorm, TextureAspects.Depth) => DxgiFormat.FormatR16Unorm,
            (Format.D24UNormS8UInt, TextureAspects.Depth) =>
                DxgiFormat.FormatR24UnormX8Typeless,
            (Format.D24UNormS8UInt, TextureAspects.Stencil) =>
                DxgiFormat.FormatX24TypelessG8Uint,
            (Format.D32Float, TextureAspects.Depth) => DxgiFormat.FormatR32Float,
            (Format.D32FloatS8UInt, TextureAspects.Depth) =>
                DxgiFormat.FormatR32FloatX8X24Typeless,
            (Format.D32FloatS8UInt, TextureAspects.Stencil) =>
                DxgiFormat.FormatX32TypelessG8X24Uint,
            (_, TextureAspects.Color or TextureAspects.Plane0) when !IsDepthStencil(format) =>
                ToDxgi(format),
            _ => throw new ArgumentOutOfRangeException(nameof(aspects)),
        };

    internal static uint PlaneIndex(Format format, TextureAspects aspect)
    {
        bool depthStencil = IsDepthStencil(format);
        return (depthStencil, format, aspect) switch
        {
            (false, _, TextureAspects.Color or TextureAspects.Plane0) => 0,
            (true, _, TextureAspects.Depth or TextureAspects.Plane0) => 0,
            (true, Format.D24UNormS8UInt or Format.D32FloatS8UInt,
                TextureAspects.Stencil or TextureAspects.Plane1) => 1,
            _ => throw new ArgumentOutOfRangeException(nameof(aspect)),
        };
    }

    internal static uint PlaneCount(Format format)
    {
        _ = IsDepthStencil(format);
        return format is Format.D24UNormS8UInt or Format.D32FloatS8UInt ? 2u : 1u;
    }

    internal static uint BytesPerElement(Format format) => format switch
    {
        Format.R8UNorm or Format.R8SNorm or Format.R8UInt or Format.R8SInt => 1,
        Format.R8G8UNorm or Format.R8G8SNorm or Format.R8G8UInt or Format.R8G8SInt or
            Format.R16UNorm or Format.R16SNorm or Format.R16UInt or Format.R16SInt or
            Format.R16Float or Format.D16UNorm => 2,
        Format.R8G8B8A8UNorm or Format.R8G8B8A8UNormSrgb or
            Format.R8G8B8A8SNorm or Format.R8G8B8A8UInt or Format.R8G8B8A8SInt or
            Format.B8G8R8A8UNorm or Format.B8G8R8A8UNormSrgb or
            Format.R10G10B10A2UNorm or Format.R11G11B10Float or
            Format.R16G16UNorm or Format.R16G16SNorm or Format.R16G16UInt or
            Format.R16G16SInt or Format.R16G16Float or
            Format.R32UInt or Format.R32SInt or Format.R32Float or
            Format.D24UNormS8UInt or Format.D32Float => 4,
        Format.R16G16B16A16UNorm or Format.R16G16B16A16SNorm or
            Format.R16G16B16A16UInt or Format.R16G16B16A16SInt or
            Format.R16G16B16A16Float or Format.R32G32UInt or Format.R32G32SInt or
            Format.R32G32Float or Format.D32FloatS8UInt => 8,
        Format.R32G32B32Float => 12,
        Format.R32G32B32A32UInt or Format.R32G32B32A32SInt or
            Format.R32G32B32A32Float => 16,
        Format.BC1UNorm or Format.BC1UNormSrgb or Format.BC4UNorm or Format.BC4SNorm =>
            throw new ArgumentException("Block-compressed formats cannot be Buffer element formats.", nameof(format)),
        Format.BC2UNorm or Format.BC2UNormSrgb or Format.BC3UNorm or
            Format.BC3UNormSrgb or Format.BC5UNorm or Format.BC5SNorm or
            Format.BC6HUFloat or Format.BC6HSFloat or Format.BC7UNorm or
            Format.BC7UNormSrgb =>
            throw new ArgumentException("Block-compressed formats cannot be Buffer element formats.", nameof(format)),
        _ => throw new ArgumentOutOfRangeException(nameof(format)),
    };

    internal static bool IsDepthStencil(Format format)
    {
        _ = ToDxgi(format);
        return format is Format.D16UNorm or Format.D24UNormS8UInt or
            Format.D32Float or Format.D32FloatS8UInt;
    }

    internal static bool IsBlockCompressed(Format format)
    {
        _ = ToDxgi(format);
        return format is >= Format.BC1UNorm and <= Format.BC7UNormSrgb;
    }

    internal static bool IsSrgb(Format format)
    {
        _ = ToDxgi(format);
        return format is
            Format.R8G8B8A8UNormSrgb or
            Format.B8G8R8A8UNormSrgb or
            Format.BC1UNormSrgb or
            Format.BC2UNormSrgb or
            Format.BC3UNormSrgb or
            Format.BC7UNormSrgb;
    }

    internal static bool IsInteger(Format format)
    {
        _ = ToDxgi(format);
        return format is
            Format.R8UInt or Format.R8SInt or
            Format.R8G8UInt or Format.R8G8SInt or
            Format.R8G8B8A8UInt or Format.R8G8B8A8SInt or
            Format.R16UInt or Format.R16SInt or
            Format.R16G16UInt or Format.R16G16SInt or
            Format.R16G16B16A16UInt or Format.R16G16B16A16SInt or
            Format.R32UInt or Format.R32SInt or
            Format.R32G32UInt or Format.R32G32SInt or
            Format.R32G32B32A32UInt or Format.R32G32B32A32SInt;
    }

    internal static void GetCopyBlockInfo(
        Format format,
        out uint blockWidth,
        out uint blockHeight,
        out uint bytesPerBlock)
    {
        if (!IsBlockCompressed(format))
        {
            blockWidth = 1;
            blockHeight = 1;
            bytesPerBlock = BytesPerElement(format);
            return;
        }

        blockWidth = 4;
        blockHeight = 4;
        bytesPerBlock = format is
            Format.BC1UNorm or
            Format.BC1UNormSrgb or
            Format.BC4UNorm or
            Format.BC4SNorm
                ? 8u
                : 16u;
    }

    internal static DxgiFormat ToTypelessFamily(Format format) => ToTypeless(format);

    private static DxgiFormat ToTypeless(Format format) => format switch
    {
        Format.R8UNorm or Format.R8SNorm or Format.R8UInt or Format.R8SInt =>
            DxgiFormat.FormatR8Typeless,
        Format.R8G8UNorm or Format.R8G8SNorm or Format.R8G8UInt or Format.R8G8SInt =>
            DxgiFormat.FormatR8G8Typeless,
        Format.R8G8B8A8UNorm or Format.R8G8B8A8UNormSrgb or
            Format.R8G8B8A8SNorm or Format.R8G8B8A8UInt or Format.R8G8B8A8SInt =>
            DxgiFormat.FormatR8G8B8A8Typeless,
        Format.B8G8R8A8UNorm or Format.B8G8R8A8UNormSrgb =>
            DxgiFormat.FormatB8G8R8A8Typeless,
        Format.R10G10B10A2UNorm => DxgiFormat.FormatR10G10B10A2Typeless,
        Format.R11G11B10Float => DxgiFormat.FormatR11G11B10Float,
        Format.R16UNorm or Format.R16SNorm or Format.R16UInt or
            Format.R16SInt or Format.R16Float or Format.D16UNorm =>
            DxgiFormat.FormatR16Typeless,
        Format.R16G16UNorm or Format.R16G16SNorm or Format.R16G16UInt or
            Format.R16G16SInt or Format.R16G16Float =>
            DxgiFormat.FormatR16G16Typeless,
        Format.R16G16B16A16UNorm or Format.R16G16B16A16SNorm or
            Format.R16G16B16A16UInt or Format.R16G16B16A16SInt or
            Format.R16G16B16A16Float =>
            DxgiFormat.FormatR16G16B16A16Typeless,
        Format.R32UInt or Format.R32SInt or Format.R32Float or Format.D32Float =>
            DxgiFormat.FormatR32Typeless,
        Format.R32G32UInt or Format.R32G32SInt or Format.R32G32Float =>
            DxgiFormat.FormatR32G32Typeless,
        Format.R32G32B32Float => DxgiFormat.FormatR32G32B32Typeless,
        Format.R32G32B32A32UInt or Format.R32G32B32A32SInt or
            Format.R32G32B32A32Float =>
            DxgiFormat.FormatR32G32B32A32Typeless,
        Format.D24UNormS8UInt => DxgiFormat.FormatR24G8Typeless,
        Format.D32FloatS8UInt => DxgiFormat.FormatR32G8X24Typeless,
        Format.BC1UNorm or Format.BC1UNormSrgb => DxgiFormat.FormatBC1Typeless,
        Format.BC2UNorm or Format.BC2UNormSrgb => DxgiFormat.FormatBC2Typeless,
        Format.BC3UNorm or Format.BC3UNormSrgb => DxgiFormat.FormatBC3Typeless,
        Format.BC4UNorm or Format.BC4SNorm => DxgiFormat.FormatBC4Typeless,
        Format.BC5UNorm or Format.BC5SNorm => DxgiFormat.FormatBC5Typeless,
        Format.BC6HUFloat or Format.BC6HSFloat => DxgiFormat.FormatBC6HTypeless,
        Format.BC7UNorm or Format.BC7UNormSrgb => DxgiFormat.FormatBC7Typeless,
        _ => throw new ArgumentOutOfRangeException(nameof(format)),
    };
}
