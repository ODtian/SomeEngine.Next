namespace SomeEngine.Graphics.Vulkan;

internal sealed unsafe partial class VulkanBackend
{
    private void DispatchMeshCore(CommandContext context, in DispatchArguments arguments)
    {
        VulkanCommandContext command = ((VulkanCommandContext)context);
        VulkanDevice device = (VulkanDevice)command.Device;
        
        device.MeshShaderApi.CmdDrawMeshTask(
            command.NativeRecording,
            arguments.X,
            arguments.Y,
            arguments.Z);
    }

    private void DispatchMeshIndirectCore(CommandContext context, in BufferRegion arguments)
    {
        VulkanCommandContext command = ((VulkanCommandContext)context);
        VulkanDevice device = (VulkanDevice)command.Device;
        
        VulkanBuffer buffer = RequireBuffer(device, arguments.Buffer, nameof(arguments));
        BufferRange range = arguments.Range.Expand(buffer.Info.Size);
        
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
        VulkanCommandContext command = ((VulkanCommandContext)context);
        VulkanDevice device = (VulkanDevice)command.Device;
        device.TryGetCapability(out VariableRateShading? shading);

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
        VulkanCommandContext command = ((VulkanCommandContext)context);
        
        VulkanDevice device = (VulkanDevice)command.Device;
        device.TryGetCapability(out VariableRateShading? shading);
        if (texture is null)
        {
            command.SetShadingRateAttachment(default, default);
            return;
        }
        VulkanTexture nativeTexture = RequireTexture(device, texture, nameof(texture));

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
            command.SetShadingRateAttachment(
                nativeView,
                new Extent2D(shading!.ImageTileWidth, shading.ImageTileHeight));
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
        _ => default,
    };

    private static FragmentShadingRateCombinerOpKHR ToNative(ShadingRateCombiner combiner) =>
        combiner switch
        {
            ShadingRateCombiner.Passthrough => FragmentShadingRateCombinerOpKHR.KeepKhr,
            ShadingRateCombiner.Override => FragmentShadingRateCombinerOpKHR.ReplaceKhr,
            ShadingRateCombiner.Minimum => FragmentShadingRateCombinerOpKHR.MinKhr,
            ShadingRateCombiner.Maximum => FragmentShadingRateCombinerOpKHR.MaxKhr,
            _ => default,
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
