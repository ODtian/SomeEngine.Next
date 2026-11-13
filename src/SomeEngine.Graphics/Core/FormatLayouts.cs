namespace SomeEngine.Graphics;
internal readonly record struct FormatLayout(Format Family, uint BlockBytes, TextureAspects Aspects, bool IsSrgb, bool IsCompressed)
{
    internal uint BlockWidth => IsCompressed ? 4u : 1u;
    internal uint BlockHeight => IsCompressed ? 4u : 1u;
    internal uint PlaneCount => (Aspects & TextureAspects.Stencil) != 0 ? 2u : 1u;
}

/// <summary>The format's byte layout and compatible view family, shared by native conversion and validation.</summary>
internal static class FormatLayouts
{
    internal static FormatLayout Get(Format format) => new(FormatFamily(format), BlockBytes(format), format switch
    {
        Format.D16UNorm or Format.D32Float => TextureAspects.Depth,
        Format.D24UNormS8UInt or Format.D32FloatS8UInt => TextureAspects.Depth | TextureAspects.Stencil,
        _ => TextureAspects.Color,
    }, format is Format.R8G8B8A8UNormSrgb or Format.B8G8R8A8UNormSrgb or Format.BC1UNormSrgb or Format.BC2UNormSrgb or Format.BC3UNormSrgb or Format.BC7UNormSrgb, format is >= Format.BC1UNorm and <= Format.BC7UNormSrgb);
    private static uint BlockBytes(Format format) => format switch
    {
        Format.R8UNorm or Format.R8SNorm or Format.R8UInt or Format.R8SInt => 1,
        Format.R8G8UNorm or Format.R8G8SNorm or Format.R8G8UInt or Format.R8G8SInt or Format.R16UNorm or Format.R16SNorm or Format.R16UInt or Format.R16SInt or Format.R16Float or Format.D16UNorm => 2,
        Format.R8G8B8A8UNorm or Format.R8G8B8A8UNormSrgb or Format.R8G8B8A8SNorm or Format.R8G8B8A8UInt or Format.R8G8B8A8SInt or Format.B8G8R8A8UNorm or Format.B8G8R8A8UNormSrgb or Format.R10G10B10A2UNorm or Format.R11G11B10Float or Format.R16G16UNorm or Format.R16G16SNorm or Format.R16G16UInt or Format.R16G16SInt or Format.R16G16Float or Format.R32UInt or Format.R32SInt or Format.R32Float or Format.D24UNormS8UInt or Format.D32Float => 4,
        Format.R16G16B16A16UNorm or Format.R16G16B16A16SNorm or Format.R16G16B16A16UInt or Format.R16G16B16A16SInt or Format.R16G16B16A16Float or Format.R64UInt or Format.R32G32UInt or Format.R32G32SInt or Format.R32G32Float or Format.D32FloatS8UInt => 8,
        Format.R32G32B32Float => 12,
        Format.R32G32B32A32UInt or Format.R32G32B32A32SInt or Format.R32G32B32A32Float => 16,
        Format.BC1UNorm or Format.BC1UNormSrgb or Format.BC4UNorm or Format.BC4SNorm => 8,
        Format.BC2UNorm or Format.BC2UNormSrgb or Format.BC3UNorm or Format.BC3UNormSrgb or Format.BC5UNorm or Format.BC5SNorm or Format.BC6HUFloat or Format.BC6HSFloat or Format.BC7UNorm or Format.BC7UNormSrgb => 16,
        _ => 0,
    };
    private static Format FormatFamily(Format format) => format switch
    {
        Format.R8UNorm or Format.R8SNorm or Format.R8UInt or Format.R8SInt => Format.R8UNorm,
        Format.R8G8UNorm or Format.R8G8SNorm or Format.R8G8UInt or Format.R8G8SInt => Format.R8G8UNorm,
        Format.R8G8B8A8UNorm or Format.R8G8B8A8UNormSrgb or Format.R8G8B8A8SNorm or Format.R8G8B8A8UInt or Format.R8G8B8A8SInt => Format.R8G8B8A8UNorm,
        Format.B8G8R8A8UNorm or Format.B8G8R8A8UNormSrgb => Format.B8G8R8A8UNorm,
        Format.R10G10B10A2UNorm => Format.R10G10B10A2UNorm,
        Format.R11G11B10Float => Format.R11G11B10Float,
        Format.R16UNorm or Format.R16SNorm or Format.R16UInt or Format.R16SInt or Format.R16Float or Format.D16UNorm => Format.R16UNorm,
        Format.R16G16UNorm or Format.R16G16SNorm or Format.R16G16UInt or Format.R16G16SInt or Format.R16G16Float => Format.R16G16UNorm,
        Format.R16G16B16A16UNorm or Format.R16G16B16A16SNorm or Format.R16G16B16A16UInt or Format.R16G16B16A16SInt or Format.R16G16B16A16Float => Format.R16G16B16A16UNorm,
        Format.R32UInt or Format.R32SInt or Format.R32Float or Format.D32Float => Format.R32UInt,
        Format.R64UInt or Format.R32G32UInt or Format.R32G32SInt or Format.R32G32Float => Format.R64UInt,
        Format.R32G32B32Float => Format.R32G32B32Float,
        Format.R32G32B32A32UInt or Format.R32G32B32A32SInt or Format.R32G32B32A32Float => Format.R32G32B32A32UInt,
        Format.D24UNormS8UInt => Format.D24UNormS8UInt,
        Format.D32FloatS8UInt => Format.D32FloatS8UInt,
        Format.BC1UNorm or Format.BC1UNormSrgb => Format.BC1UNorm,
        Format.BC2UNorm or Format.BC2UNormSrgb => Format.BC2UNorm,
        Format.BC3UNorm or Format.BC3UNormSrgb => Format.BC3UNorm,
        Format.BC4UNorm or Format.BC4SNorm => Format.BC4UNorm,
        Format.BC5UNorm or Format.BC5SNorm => Format.BC5UNorm,
        Format.BC6HUFloat or Format.BC6HSFloat => Format.BC6HUFloat,
        Format.BC7UNorm or Format.BC7UNormSrgb => Format.BC7UNorm,
        _ => default,
    };
    internal static bool IsInteger(Format format) => format is Format.R8UInt or Format.R8SInt or Format.R8G8UInt or Format.R8G8SInt or Format.R8G8B8A8UInt or Format.R8G8B8A8SInt or Format.R16UInt or Format.R16SInt or Format.R16G16UInt or Format.R16G16SInt or Format.R16G16B16A16UInt or Format.R16G16B16A16SInt or Format.R32UInt or Format.R32SInt or Format.R64UInt or Format.R32G32UInt or Format.R32G32SInt or Format.R32G32B32A32UInt or Format.R32G32B32A32SInt;
}
