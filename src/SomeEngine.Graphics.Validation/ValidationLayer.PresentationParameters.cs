using System.Numerics;

namespace SomeEngine.Graphics.Validation;

public sealed partial class ValidationLayer
{
    private static void ValidateSwapchainDescription(Device device, in SwapchainDesc description)
    {
        ArgumentNullException.ThrowIfNull(description.Surface, nameof(description));
        bool dxil = device.Capabilities.CompileTarget == SlangShaderSharp.SlangCompileTarget.Dxil;
        if (description.ImageCount < 2 || dxil && description.ImageCount > 16)
            throw new ArgumentOutOfRangeException(nameof(description));
        if (!dxil && description.ImageUsages == TextureUsages.None)
            throw new ArgumentOutOfRangeException(nameof(description));
        if (dxil && (description.ImageUsages & (TextureUsages.DepthStencilAttachment | TextureUsages.ShadingRate |
                TextureUsages.SamplerFeedback | TextureUsages.Shareable)) != 0)
            throw new ArgumentOutOfRangeException(nameof(description));
        ValidateSwapchainConfig(device, description.Config, nameof(description));
    }

    private static void ValidateSwapchainConfig(Device device, in SwapchainConfig config, string parameterName)
    {
        bool dxil = device.Capabilities.CompileTarget == SlangShaderSharp.SlangCompileTarget.Dxil;
        if (config.MaximumFrameLatency == 0 || dxil && config.MaximumFrameLatency > 16 ||
            !dxil && (config.Width == 0 || config.Height == 0))
            throw new ArgumentOutOfRangeException(parameterName);
        if (!Enum.IsDefined(config.Format) || !Enum.IsDefined(config.ColorSpace) || !Enum.IsDefined(config.PresentType))
            throw new ArgumentOutOfRangeException(parameterName);
        if (!dxil) return;
        if (config.Width > device.Capabilities.Limits.MaximumTextureDimension2D ||
            config.Height > device.Capabilities.Limits.MaximumTextureDimension2D)
            throw new ArgumentOutOfRangeException(parameterName);
        if (config.Format is not (Format.R8G8B8A8UNorm or Format.R8G8B8A8UNormSrgb or Format.B8G8R8A8UNorm or
                Format.B8G8R8A8UNormSrgb or Format.R16G16B16A16Float or Format.R10G10B10A2UNorm))
            throw new ArgumentOutOfRangeException(parameterName);
        ColorSpace expected = config.Format switch
        {
            Format.R16G16B16A16Float => ColorSpace.ScRgb,
            Format.R10G10B10A2UNorm => ColorSpace.Hdr10,
            _ => ColorSpace.Srgb,
        };
        if (config.ColorSpace != expected)
            throw new ArgumentException("The color space is incompatible with the swapchain format.", parameterName);
        if ((config.PresentType is PresentType.Fifo or PresentType.Mailbox) && config.AllowTearing ||
            config.PresentType == PresentType.Immediate && !config.AllowTearing)
            throw new ArgumentException("The presentation mode and tearing setting are incompatible.", parameterName);
        ValidateHdr10Metadata(config, parameterName);
    }

    private SwapchainImageUse ValidateSwapchainImage(in SwapchainImage image, SwapchainImageStatus status)
    {
        SwapchainImageUse use = image.ImageUse ?? throw new InvalidOperationException("The swapchain image is uninitialized.");
        Require(use.Swapchain);
        if (image.Sequence == 0 || use.CurrentSequence != image.Sequence ||
            use.CurrentGeneration != use.Swapchain.Info.Generation || use.CurrentStatus != status || use.Swapchain.IsOutOfDate)
            throw new InvalidOperationException(
                $"The image requires status {status}, acquisition {image.Sequence}, and swapchain generation {use.Swapchain.Info.Generation}; " +
                $"current status is {use.CurrentStatus}, acquisition is {use.CurrentSequence}, generation is {use.CurrentGeneration}, " +
                $"and out-of-date is {use.Swapchain.IsOutOfDate}.");
        return use;
    }

    private void ValidateSwapchainTextureForRecording(Texture texture)
    {
        Swapchain? swapchain;
        lock (_gate)
            swapchain = _objects.TryGetValue(texture, out ValidationObjectInfo? info) ? info.Parent as Swapchain : null;
        if (swapchain is null) return;
        foreach (SwapchainImageUse image in swapchain.ImageUses)
            if (ReferenceEquals(image.CurrentTexture, texture) && image.CurrentGeneration == swapchain.Info.Generation &&
                image.CurrentStatus == SwapchainImageStatus.Acquired && !swapchain.IsOutOfDate) return;
        throw new InvalidOperationException("A swapchain texture is recorded while its current image is acquired.");
    }

    private static void ValidateHdr10Metadata(
        in SwapchainConfig config,
        string parameterName)
    {
        if (config.Hdr10Metadata is not Hdr10Metadata metadata)
            return;
        if (config.ColorSpace != ColorSpace.Hdr10)
        {
            throw new ArgumentException(
                "HDR10 metadata can be supplied only for an HDR10 swapchain color space.",
                parameterName);
        }

        const ushort maximumChromaticity = 50_000;
        if (metadata.RedPrimaryX > maximumChromaticity ||
            metadata.RedPrimaryY > maximumChromaticity ||
            metadata.GreenPrimaryX > maximumChromaticity ||
            metadata.GreenPrimaryY > maximumChromaticity ||
            metadata.BluePrimaryX > maximumChromaticity ||
            metadata.BluePrimaryY > maximumChromaticity ||
            metadata.WhitePointX > maximumChromaticity ||
            metadata.WhitePointY > maximumChromaticity)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                "HDR10 chromaticity coordinates must be in the CTA-861 range 0..50000.");
        }
        if (metadata.MaximumMasteringLuminance == 0 ||
            metadata.MinimumMasteringLuminance >
                checked((ulong)metadata.MaximumMasteringLuminance * 10_000UL))
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                "HDR10 mastering luminance values are inconsistent.");
        }
        if (metadata.MaximumContentLightLevel != 0 &&
            metadata.MaximumFrameAverageLightLevel > metadata.MaximumContentLightLevel)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                "HDR10 maximum frame-average light level cannot exceed maximum content light level.");
        }
    }
}
