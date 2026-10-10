using static SomeEngine.Graphics.Direct3D12.Tests.D3D12TestCompute;

namespace SomeEngine.Graphics.Direct3D12.Tests;

internal static class ShadowTestImages
{
    internal static SampledImage ReceiverMask(uint width, uint height, uint[]? pixels = null) =>
        new(width, height, pixels ?? new uint[checked(width * height)], 0,
            Type: ResourceBindingType.TextureUav, Name: "PageReceiverMasks");

    internal static SampledImage[] ReceiverHierarchy(uint width, uint height, uint[]? pixels = null) =>
    [
        ReceiverMask(width, height, pixels),
        .. Enumerable.Range(1, 7).Select(mip =>
        {
            uint mipWidth = Math.Max(width >> mip, 1u);
            uint mipHeight = Math.Max(height >> mip, 1u);
            return new SampledImage(mipWidth, mipHeight, new uint[checked(mipWidth * mipHeight)], 0,
                Type: ResourceBindingType.TextureUav, Name: $"PageReceiverMaskMip{mip}");
        }),
    ];
}
