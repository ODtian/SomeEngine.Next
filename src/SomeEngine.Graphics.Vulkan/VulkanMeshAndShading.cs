namespace SomeEngine.Graphics.Vulkan;

internal sealed unsafe partial class VulkanBackend
{
    private void DispatchMeshCore(CommandContext context, in DispatchArguments arguments)
    {
        VulkanCommandContext command = RequireCommandContext(context, nameof(context));
        VulkanDevice device = (VulkanDevice)command.Device;
        if (!device.TryGetCapability(out MeshShaders? mesh) || mesh is null)
            throw new NotSupportedException("The Device was not created with MeshShaders support.");
        device.MeshShaderApi.CmdDrawMeshTask(
            command.NativeRecording,
            arguments.X,
            arguments.Y,
            arguments.Z);
    }

    private void DispatchMeshIndirectCore(CommandContext context, in BufferRegion arguments)
    {
        VulkanCommandContext command = RequireCommandContext(context, nameof(context));
        VulkanDevice device = (VulkanDevice)command.Device;
        if (!device.TryGetCapability(out MeshShaders? mesh) || mesh is null)
            throw new NotSupportedException("The Device was not created with MeshShaders support.");
        VulkanBuffer buffer = RequireBuffer(device, arguments.Buffer, nameof(arguments));
        BufferRange range = arguments.Range.Resolve(buffer.Info.Size);
        if (range.Size < 12 || (range.Offset & 3) != 0)
            throw new ArgumentOutOfRangeException(nameof(arguments));
        command.Capture(buffer);
        device.MeshShaderApi.CmdDrawMeshTasksIndirect(
            command.NativeRecording,
            buffer.Native,
            range.Offset,
            1,
            12);
    }

    private void SetShadingRateCore(
        CommandContext context,
        ShadingRate rate,
        ShadingRateCombiner primitiveCombiner,
        ShadingRateCombiner imageCombiner)
    {
        VulkanCommandContext command = RequireCommandContext(context, nameof(context));
        VulkanDevice device = (VulkanDevice)command.Device;
        if (!device.TryGetCapability(out VariableRateShading? shading) || shading is null)
            throw new NotSupportedException("The Device was not created with VariableRateShading support.");
        if (!shading.Rates.Contains(rate) || !shading.Combiners.Contains(primitiveCombiner) ||
            !shading.Combiners.Contains(imageCombiner))
            throw new NotSupportedException("The requested Vulkan fragment shading-rate combination is unsupported.");
        Extent2D fragmentSize = ToNative(rate);
        FragmentShadingRateCombinerOpKHR* combiners = stackalloc FragmentShadingRateCombinerOpKHR[2]
        {
            ToNative(primitiveCombiner),
            ToNative(imageCombiner),
        };
        device.FragmentShadingRateApi.CmdSetFragmentShadingRate(
            command.NativeRecording,
            &fragmentSize,
            combiners);
    }

    private void SetShadingRateImageCore(CommandContext context, RhiTexture? texture)
    {
        VulkanCommandContext command = RequireCommandContext(context, nameof(context));
        VulkanDevice device = (VulkanDevice)command.Device;
        if (!device.TryGetCapability(out VariableRateShading? shading) || shading is null ||
            !shading.ShadingRateImage)
            throw new NotSupportedException("The Device does not support fragment shading-rate attachments.");
        if (texture is null)
        {
            command.SetShadingRateAttachment(default, default);
            return;
        }
        VulkanTexture nativeTexture = RequireTexture(device, texture, nameof(texture));
        if ((nativeTexture.Info.Usages & TextureUsages.ShadingRate) == 0)
            throw new ArgumentException("The Texture requires ShadingRate usage.", nameof(texture));
        if (nativeTexture.Info.Format != RhiFormat.R8UInt ||
            nativeTexture.Info.Dimension != TextureDimension.Texture2D ||
            nativeTexture.Info.SampleCount != 1 ||
            nativeTexture.Info.ArrayLayerCount != 1)
        {
            throw new ArgumentException(
                "A Vulkan shading-rate image must be one-layer, single-sample R8UInt Texture2D.",
                nameof(texture));
        }
        ImageViewCreateInfo createInfo = new()
        {
            SType = StructureType.ImageViewCreateInfo,
            Image = nativeTexture.Native,
            ViewType = ImageViewType.Type2D,
            Format = VulkanFormats.ToNative(nativeTexture.Info.Format),
            SubresourceRange = new ImageSubresourceRange(
                ImageAspectFlags.ColorBit,
                0,
                1,
                0,
                1),
        };
        VkImageView nativeView = default;
        device.ThrowIfDeviceCallFailed(
            Api.CreateImageView(device.Native, &createInfo, null, &nativeView),
            "vkCreateImageView(shading rate)");
        var view = new VulkanShadingRateImageView(device, nativeTexture, nativeView);
        try
        {
            command.Capture(view);
            command.SetShadingRateAttachment(
                nativeView,
                new Extent2D(shading.ImageTileWidth, shading.ImageTileHeight));
        }
        finally
        {
            view.ReleaseNative();
        }
    }

    private static Extent2D ToNative(ShadingRate rate) => rate switch
    {
        ShadingRate.Rate1x1 => new Extent2D(1, 1),
        ShadingRate.Rate1x2 => new Extent2D(1, 2),
        ShadingRate.Rate2x1 => new Extent2D(2, 1),
        ShadingRate.Rate2x2 => new Extent2D(2, 2),
        ShadingRate.Rate2x4 => new Extent2D(2, 4),
        ShadingRate.Rate4x2 => new Extent2D(4, 2),
        ShadingRate.Rate4x4 => new Extent2D(4, 4),
        _ => throw new ArgumentOutOfRangeException(nameof(rate)),
    };

    private static FragmentShadingRateCombinerOpKHR ToNative(ShadingRateCombiner combiner) =>
        combiner switch
        {
            ShadingRateCombiner.Passthrough => FragmentShadingRateCombinerOpKHR.KeepKhr,
            ShadingRateCombiner.Override => FragmentShadingRateCombinerOpKHR.ReplaceKhr,
            ShadingRateCombiner.Minimum => FragmentShadingRateCombinerOpKHR.MinKhr,
            ShadingRateCombiner.Maximum => FragmentShadingRateCombinerOpKHR.MaxKhr,
            _ => throw new NotSupportedException("Vulkan has no additive fragment shading-rate combiner."),
        };

    private sealed class VulkanShadingRateImageView : IVulkanRetained
    {
        private readonly VulkanDevice _device;
        private readonly VulkanTexture _texture;
        private readonly VulkanLifetime _lifetime;
        private VkImageView _native;

        internal VulkanShadingRateImageView(
            VulkanDevice device,
            VulkanTexture texture,
            VkImageView native)
        {
            _device = device;
            _texture = texture;
            _native = native;
            _texture.RetainNative();
            _lifetime = new VulkanLifetime(DestroyNative);
        }

        public void RetainNative() => _lifetime.Retain();
        public void ReleaseNative() => _lifetime.Release();
        private void DestroyNative()
        {
            if (_native.Handle != 0)
                _device.Backend.Api.DestroyImageView(_device.Native, _native, null);
            _native = default;
            _texture.ReleaseNative();
        }
    }
}

internal sealed unsafe partial class VulkanBackend
{
    private sealed partial class VulkanCommandContext
    {
        private VkImageView _shadingRateAttachment;
        private Extent2D _shadingRateTexelSize;

        internal void SetShadingRateAttachment(VkImageView view, Extent2D texelSize)
        {
            _shadingRateAttachment = view;
            _shadingRateTexelSize = texelSize;
        }

        internal bool TryGetShadingRateAttachment(
            out VkImageView view,
            out Extent2D texelSize)
        {
            view = _shadingRateAttachment;
            texelSize = _shadingRateTexelSize;
            return view.Handle != 0;
        }
    }
}
