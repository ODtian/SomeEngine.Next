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
        // Shader Model 6.6 exposes typed 64-bit UAV atomics through R32G32_UINT.
        Format.R64UInt => DxgiFormat.FormatR32G32Uint,
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
        _ => default!,
    };
    internal static DxgiFormat ToResourceFormat(Format format, ReadOnlySpan<Format> permittedViewFormats)
    {
        bool requiresTypeless = IsDepthStencil(format);
        foreach (Format permitted in permittedViewFormats)
        {
            _ = ToDxgi(permitted);
            requiresTypeless |= permitted != format;
        }

        return requiresTypeless ? ToTypeless(format) : ToDxgi(format);
    }

    internal static DxgiFormat ToShaderViewFormat(Format format, TextureAspects aspects) => (format, aspects) switch
    {
        (Format.D16UNorm, TextureAspects.Depth) => DxgiFormat.FormatR16Unorm,
        (Format.D24UNormS8UInt, TextureAspects.Depth) => DxgiFormat.FormatR24UnormX8Typeless,
        (Format.D24UNormS8UInt, TextureAspects.Stencil) => DxgiFormat.FormatX24TypelessG8Uint,
        (Format.D32Float, TextureAspects.Depth) => DxgiFormat.FormatR32Float,
        (Format.D32FloatS8UInt, TextureAspects.Depth) => DxgiFormat.FormatR32FloatX8X24Typeless,
        (Format.D32FloatS8UInt, TextureAspects.Stencil) => DxgiFormat.FormatX32TypelessG8X24Uint,
        (_, TextureAspects.Color or TextureAspects.Plane0)when !IsDepthStencil(format) => ToDxgi(format),
        _ => default!,
    };
    internal static DxgiFormat ToTypelessFamily(Format format) => ToTypeless(format);
    internal static uint PlaneIndex(Format format, TextureAspects aspect) => aspect is TextureAspects.Stencil or TextureAspects.Plane1 ? 1u : 0u;
    internal static uint PlaneCount(Format format) => FormatLayouts.Get(format).PlaneCount;
    internal static uint BytesPerElement(Format format) => FormatLayouts.Get(format).BlockBytes;
    internal static bool IsDepthStencil(Format format) => (FormatLayouts.Get(format).Aspects & (TextureAspects.Depth | TextureAspects.Stencil)) != 0;
    internal static bool IsBlockCompressed(Format format) => FormatLayouts.Get(format).IsCompressed;
    internal static bool IsSrgb(Format format) => FormatLayouts.Get(format).IsSrgb;
    internal static bool IsInteger(Format format) => FormatLayouts.IsInteger(format);
    internal static void GetCopyBlockInfo(Format format, out uint blockWidth, out uint blockHeight, out uint bytesPerBlock)
    {
        FormatLayout layout = FormatLayouts.Get(format);
        blockWidth = layout.BlockWidth;
        blockHeight = layout.BlockHeight;
        bytesPerBlock = layout.BlockBytes;
    }

    private static DxgiFormat ToTypeless(Format format) => FormatLayouts.Get(format).Family switch
    {
        Format.R8UNorm => DxgiFormat.FormatR8Typeless,
        Format.R8G8UNorm => DxgiFormat.FormatR8G8Typeless,
        Format.R8G8B8A8UNorm => DxgiFormat.FormatR8G8B8A8Typeless,
        Format.B8G8R8A8UNorm => DxgiFormat.FormatB8G8R8A8Typeless,
        Format.R10G10B10A2UNorm => DxgiFormat.FormatR10G10B10A2Typeless,
        Format.R11G11B10Float => DxgiFormat.FormatR11G11B10Float,
        Format.R16UNorm => DxgiFormat.FormatR16Typeless,
        Format.R16G16UNorm => DxgiFormat.FormatR16G16Typeless,
        Format.R16G16B16A16UNorm => DxgiFormat.FormatR16G16B16A16Typeless,
        Format.R32UInt => DxgiFormat.FormatR32Typeless,
        Format.R64UInt => DxgiFormat.FormatR32G32Typeless,
        Format.R32G32B32Float => DxgiFormat.FormatR32G32B32Typeless,
        Format.R32G32B32A32UInt => DxgiFormat.FormatR32G32B32A32Typeless,
        Format.D24UNormS8UInt => DxgiFormat.FormatR24G8Typeless,
        Format.D32FloatS8UInt => DxgiFormat.FormatR32G8X24Typeless,
        Format.BC1UNorm => DxgiFormat.FormatBC1Typeless,
        Format.BC2UNorm => DxgiFormat.FormatBC2Typeless,
        Format.BC3UNorm => DxgiFormat.FormatBC3Typeless,
        Format.BC4UNorm => DxgiFormat.FormatBC4Typeless,
        Format.BC5UNorm => DxgiFormat.FormatBC5Typeless,
        Format.BC6HUFloat => DxgiFormat.FormatBC6HTypeless,
        Format.BC7UNorm => DxgiFormat.FormatBC7Typeless,
        _ => default
    };
}
