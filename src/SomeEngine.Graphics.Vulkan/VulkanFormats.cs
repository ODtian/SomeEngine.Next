namespace SomeEngine.Graphics.Vulkan;

internal static unsafe class VulkanFormats
{
    internal static bool TryFromNative(VkFormat format, out RhiFormat result)
    {
        foreach (RhiFormat candidate in Enum.GetValues<RhiFormat>())
        {
            if (ToNative(candidate) != format)
                continue;
            result = candidate;
            return true;
        }
        result = default;
        return false;
    }

    internal static bool IsDepthStencil(RhiFormat format) => format is
        RhiFormat.D16UNorm or
        RhiFormat.D24UNormS8UInt or
        RhiFormat.D32Float or
        RhiFormat.D32FloatS8UInt;

    internal static ImageAspectFlags Aspects(RhiFormat format) => format switch
    {
        RhiFormat.D16UNorm or RhiFormat.D32Float => ImageAspectFlags.DepthBit,
        RhiFormat.D24UNormS8UInt or RhiFormat.D32FloatS8UInt =>
            ImageAspectFlags.DepthBit | ImageAspectFlags.StencilBit,
        _ => ImageAspectFlags.ColorBit,
    };

    internal static (uint Width, uint Height, uint Bytes) GetBlockInfo(RhiFormat format) => format switch
    {
        RhiFormat.R8UNorm or RhiFormat.R8SNorm or RhiFormat.R8UInt or RhiFormat.R8SInt => (1, 1, 1),
        RhiFormat.R8G8UNorm or RhiFormat.R8G8SNorm or RhiFormat.R8G8UInt or RhiFormat.R8G8SInt or
        RhiFormat.R16UNorm or RhiFormat.R16SNorm or RhiFormat.R16UInt or RhiFormat.R16SInt or
        RhiFormat.R16Float or RhiFormat.D16UNorm => (1, 1, 2),
        RhiFormat.R8G8B8A8UNorm or RhiFormat.R8G8B8A8UNormSrgb or RhiFormat.R8G8B8A8SNorm or
        RhiFormat.R8G8B8A8UInt or RhiFormat.R8G8B8A8SInt or RhiFormat.B8G8R8A8UNorm or
        RhiFormat.B8G8R8A8UNormSrgb or RhiFormat.R10G10B10A2UNorm or RhiFormat.R11G11B10Float or
        RhiFormat.R16G16UNorm or RhiFormat.R16G16SNorm or RhiFormat.R16G16UInt or RhiFormat.R16G16SInt or
        RhiFormat.R16G16Float or RhiFormat.R32UInt or RhiFormat.R32SInt or RhiFormat.R32Float or
        RhiFormat.D24UNormS8UInt or RhiFormat.D32Float => (1, 1, 4),
        RhiFormat.R16G16B16A16UNorm or RhiFormat.R16G16B16A16SNorm or RhiFormat.R16G16B16A16UInt or
        RhiFormat.R16G16B16A16SInt or RhiFormat.R16G16B16A16Float or RhiFormat.R32G32UInt or
        RhiFormat.R32G32SInt or RhiFormat.R32G32Float or RhiFormat.D32FloatS8UInt => (1, 1, 8),
        RhiFormat.R32G32B32Float => (1, 1, 12),
        RhiFormat.R32G32B32A32UInt or RhiFormat.R32G32B32A32SInt or RhiFormat.R32G32B32A32Float => (1, 1, 16),
        RhiFormat.BC1UNorm or RhiFormat.BC1UNormSrgb or RhiFormat.BC4UNorm or RhiFormat.BC4SNorm => (4, 4, 8),
        RhiFormat.BC2UNorm or RhiFormat.BC2UNormSrgb or RhiFormat.BC3UNorm or RhiFormat.BC3UNormSrgb or
        RhiFormat.BC5UNorm or RhiFormat.BC5SNorm or RhiFormat.BC6HUFloat or RhiFormat.BC6HSFloat or
        RhiFormat.BC7UNorm or RhiFormat.BC7UNormSrgb => (4, 4, 16),
        _ => throw new ArgumentOutOfRangeException(nameof(format)),
    };

    internal static VkFormat ToNative(RhiFormat format) => format switch
    {
        RhiFormat.R8UNorm => VkFormat.R8Unorm,
        RhiFormat.R8SNorm => VkFormat.R8SNorm,
        RhiFormat.R8UInt => VkFormat.R8Uint,
        RhiFormat.R8SInt => VkFormat.R8Sint,
        RhiFormat.R8G8UNorm => VkFormat.R8G8Unorm,
        RhiFormat.R8G8SNorm => VkFormat.R8G8SNorm,
        RhiFormat.R8G8UInt => VkFormat.R8G8Uint,
        RhiFormat.R8G8SInt => VkFormat.R8G8Sint,
        RhiFormat.R8G8B8A8UNorm => VkFormat.R8G8B8A8Unorm,
        RhiFormat.R8G8B8A8UNormSrgb => VkFormat.R8G8B8A8Srgb,
        RhiFormat.R8G8B8A8SNorm => VkFormat.R8G8B8A8SNorm,
        RhiFormat.R8G8B8A8UInt => VkFormat.R8G8B8A8Uint,
        RhiFormat.R8G8B8A8SInt => VkFormat.R8G8B8A8Sint,
        RhiFormat.B8G8R8A8UNorm => VkFormat.B8G8R8A8Unorm,
        RhiFormat.B8G8R8A8UNormSrgb => VkFormat.B8G8R8A8Srgb,
        RhiFormat.R10G10B10A2UNorm => VkFormat.A2B10G10R10UnormPack32,
        RhiFormat.R11G11B10Float => VkFormat.B10G11R11UfloatPack32,
        RhiFormat.R16UNorm => VkFormat.R16Unorm,
        RhiFormat.R16SNorm => VkFormat.R16SNorm,
        RhiFormat.R16UInt => VkFormat.R16Uint,
        RhiFormat.R16SInt => VkFormat.R16Sint,
        RhiFormat.R16Float => VkFormat.R16Sfloat,
        RhiFormat.R16G16UNorm => VkFormat.R16G16Unorm,
        RhiFormat.R16G16SNorm => VkFormat.R16G16SNorm,
        RhiFormat.R16G16UInt => VkFormat.R16G16Uint,
        RhiFormat.R16G16SInt => VkFormat.R16G16Sint,
        RhiFormat.R16G16Float => VkFormat.R16G16Sfloat,
        RhiFormat.R16G16B16A16UNorm => VkFormat.R16G16B16A16Unorm,
        RhiFormat.R16G16B16A16SNorm => VkFormat.R16G16B16A16SNorm,
        RhiFormat.R16G16B16A16UInt => VkFormat.R16G16B16A16Uint,
        RhiFormat.R16G16B16A16SInt => VkFormat.R16G16B16A16Sint,
        RhiFormat.R16G16B16A16Float => VkFormat.R16G16B16A16Sfloat,
        RhiFormat.R32UInt => VkFormat.R32Uint,
        RhiFormat.R32SInt => VkFormat.R32Sint,
        RhiFormat.R32Float => VkFormat.R32Sfloat,
        RhiFormat.R32G32UInt => VkFormat.R32G32Uint,
        RhiFormat.R32G32SInt => VkFormat.R32G32Sint,
        RhiFormat.R32G32Float => VkFormat.R32G32Sfloat,
        RhiFormat.R32G32B32Float => VkFormat.R32G32B32Sfloat,
        RhiFormat.R32G32B32A32UInt => VkFormat.R32G32B32A32Uint,
        RhiFormat.R32G32B32A32SInt => VkFormat.R32G32B32A32Sint,
        RhiFormat.R32G32B32A32Float => VkFormat.R32G32B32A32Sfloat,
        RhiFormat.D16UNorm => VkFormat.D16Unorm,
        RhiFormat.D24UNormS8UInt => VkFormat.D24UnormS8Uint,
        RhiFormat.D32Float => VkFormat.D32Sfloat,
        RhiFormat.D32FloatS8UInt => VkFormat.D32SfloatS8Uint,
        RhiFormat.BC1UNorm => VkFormat.BC1RgbaUnormBlock,
        RhiFormat.BC1UNormSrgb => VkFormat.BC1RgbaSrgbBlock,
        RhiFormat.BC2UNorm => VkFormat.BC2UnormBlock,
        RhiFormat.BC2UNormSrgb => VkFormat.BC2SrgbBlock,
        RhiFormat.BC3UNorm => VkFormat.BC3UnormBlock,
        RhiFormat.BC3UNormSrgb => VkFormat.BC3SrgbBlock,
        RhiFormat.BC4UNorm => VkFormat.BC4UnormBlock,
        RhiFormat.BC4SNorm => VkFormat.BC4SNormBlock,
        RhiFormat.BC5UNorm => VkFormat.BC5UnormBlock,
        RhiFormat.BC5SNorm => VkFormat.BC5SNormBlock,
        RhiFormat.BC6HUFloat => VkFormat.BC6HUfloatBlock,
        RhiFormat.BC6HSFloat => VkFormat.BC6HSfloatBlock,
        RhiFormat.BC7UNorm => VkFormat.BC7UnormBlock,
        RhiFormat.BC7UNormSrgb => VkFormat.BC7SrgbBlock,
        _ => throw new ArgumentOutOfRangeException(nameof(format)),
    };

    internal static DeviceCapabilities CreateCapabilities(
        Vk vk,
        VkPhysicalDevice physicalDevice,
        in PhysicalDeviceFeatures nativeFeatures,
        VulkanExtendedFeatureSupport extendedFeatures)
    {
        PhysicalDeviceMaintenance4Properties maintenance4 = new()
        {
            SType = StructureType.PhysicalDeviceMaintenance4Properties,
        };
        PhysicalDeviceProperties2 properties2 = new()
        {
            SType = StructureType.PhysicalDeviceProperties2,
            PNext = &maintenance4,
        };
        vk.GetPhysicalDeviceProperties2(physicalDevice, &properties2);
        PhysicalDeviceLimits limits = properties2.Properties.Limits;
        DeviceLimits deviceLimits = new(
            maintenance4.MaxBufferSize,
            limits.MaxImageDimension1D,
            limits.MaxImageDimension2D,
            limits.MaxImageDimension3D,
            limits.MaxImageArrayLayers,
            limits.MaxColorAttachments,
            limits.MaxViewports,
            Math.Min(limits.MaxDescriptorSetSampledImages, 1_000_000u),
            limits.MaxDescriptorSetSamplers,
            checked((uint)Math.Min(limits.MinUniformBufferOffsetAlignment, uint.MaxValue)),
            checked((uint)Math.Max(limits.OptimalBufferCopyRowPitchAlignment, 1)),
            checked((uint)Math.Max(limits.OptimalBufferCopyOffsetAlignment, 1)));

        RhiFormat[] formats = Enum.GetValues<RhiFormat>();
        FormatSupport[] support = new FormatSupport[formats.Length];
        for (int index = 0; index < formats.Length; index++)
            support[index] = QueryFormat(vk, physicalDevice, formats[index], nativeFeatures);

        DynamicStates dynamicStates =
            DynamicStates.Viewport |
            DynamicStates.Scissor |
            DynamicStates.BlendConstants |
            DynamicStates.StencilReference |
            DynamicStates.DepthBias;
        if (nativeFeatures.DepthBounds)
            dynamicStates |= DynamicStates.DepthBounds;
        if (extendedFeatures.ExtendedDynamicState)
            dynamicStates |= DynamicStates.PrimitiveTopology;
        if (extendedFeatures.ExtendedDynamicState2)
            dynamicStates |= DynamicStates.StripCut;
        return new DeviceCapabilities(
            deviceLimits,
            supportsBundles: false,
            supportsPipelineStatistics: nativeFeatures.PipelineStatisticsQuery,
            supportsStreamOutputStatistics: extendedFeatures.TransformFeedback,
            supportsDepthBounds: nativeFeatures.DepthBounds,
            dynamicStates,
            support,
            ShaderTarget.Spirv);
    }

    private static FormatSupport QueryFormat(
        Vk vk,
        VkPhysicalDevice physicalDevice,
        RhiFormat format,
        in PhysicalDeviceFeatures nativeFeatures)
    {
        VkFormat native = ToNative(format);
        FormatProperties properties;
        vk.GetPhysicalDeviceFormatProperties(physicalDevice, native, &properties);
        FormatFeatureFlags optimal = properties.OptimalTilingFeatures;
        FormatFeatureFlags buffer = properties.BufferFeatures;
        FormatFeatures features = FormatFeatures.None;
        if (buffer != 0)
            features |= FormatFeatures.Buffer;
        if ((buffer & FormatFeatureFlags.VertexBufferBit) != 0)
            features |= FormatFeatures.VertexBuffer;
        if (format is RhiFormat.R16UInt or RhiFormat.R32UInt)
            features |= FormatFeatures.IndexBuffer;
        if ((optimal & FormatFeatureFlags.SampledImageBit) != 0)
            features |= FormatFeatures.ShaderLoad | FormatFeatures.ShaderSample;
        if ((optimal & FormatFeatureFlags.SampledImageFilterLinearBit) != 0)
            features |= FormatFeatures.Mipmaps;
        if ((optimal & FormatFeatureFlags.ColorAttachmentBit) != 0)
            features |= FormatFeatures.ColorAttachment;
        if ((optimal & FormatFeatureFlags.ColorAttachmentBlendBit) != 0)
            features |= FormatFeatures.ColorAttachmentBlend;
        if ((optimal & FormatFeatureFlags.DepthStencilAttachmentBit) != 0)
            features |= FormatFeatures.DepthStencilAttachment;
        if ((optimal & FormatFeatureFlags.StorageImageBit) != 0)
            features |= FormatFeatures.Storage | FormatFeatures.StorageLoad | FormatFeatures.StorageStore;
        if ((optimal & FormatFeatureFlags.StorageImageAtomicBit) != 0)
            features |= FormatFeatures.StorageAtomic;
        if (nativeFeatures.LogicOp && (features & FormatFeatures.ColorAttachment) != 0)
            features |= FormatFeatures.LogicOperation;

        SampleCounts sampleCounts = SampleCounts.None;
        if (TryImage(vk, physicalDevice, native, ImageType.Type1D, out ImageFormatProperties oneDimensional))
        {
            features |= FormatFeatures.Texture1D;
            sampleCounts |= ToSampleCounts(oneDimensional.SampleCounts);
        }
        if (TryImage(vk, physicalDevice, native, ImageType.Type2D, out ImageFormatProperties twoDimensional))
        {
            features |= FormatFeatures.Texture2D | FormatFeatures.TextureCube;
            sampleCounts |= ToSampleCounts(twoDimensional.SampleCounts);
        }
        if (TryImage(vk, physicalDevice, native, ImageType.Type3D, out ImageFormatProperties threeDimensional))
        {
            features |= FormatFeatures.Texture3D;
            sampleCounts |= ToSampleCounts(threeDimensional.SampleCounts);
        }
        if ((sampleCounts & ~SampleCounts.One) != 0 &&
            (features & FormatFeatures.ColorAttachment) != 0)
        {
            features |= FormatFeatures.MultisampleColorAttachment |
                FormatFeatures.MultisampleLoad |
                FormatFeatures.MultisampleResolve;
        }
        SampleCounts sparseSampleCounts = SampleCounts.None;
        if (TrySparseImage(vk, physicalDevice, native, ImageType.Type2D, out ImageFormatProperties sparse2D))
        {
            features |= FormatFeatures.SparseTexture2D;
            sparseSampleCounts |= ToSampleCounts(sparse2D.SampleCounts);
        }
        if (TrySparseImage(vk, physicalDevice, native, ImageType.Type3D, out ImageFormatProperties sparse3D))
        {
            features |= FormatFeatures.SparseTexture3D;
            sparseSampleCounts |= ToSampleCounts(sparse3D.SampleCounts);
        }
        return new FormatSupport(format, features, sampleCounts, sparseSampleCounts);
    }

    private static bool TrySparseImage(
        Vk vk,
        VkPhysicalDevice physicalDevice,
        VkFormat format,
        ImageType type,
        out ImageFormatProperties properties)
    {
        ImageFormatProperties nativeProperties = default;
        Result result = vk.GetPhysicalDeviceImageFormatProperties(
            physicalDevice,
            format,
            type,
            ImageTiling.Optimal,
            ImageUsageFlags.SampledBit,
            ImageCreateFlags.CreateSparseBindingBit | ImageCreateFlags.CreateSparseResidencyBit,
            &nativeProperties);
        properties = nativeProperties;
        return result == Result.Success;
    }

    private static bool TryImage(
        Vk vk,
        VkPhysicalDevice physicalDevice,
        VkFormat format,
        ImageType type,
        out ImageFormatProperties properties)
    {
        ImageFormatProperties nativeProperties = default;
        Result result = vk.GetPhysicalDeviceImageFormatProperties(
            physicalDevice,
            format,
            type,
            ImageTiling.Optimal,
            ImageUsageFlags.SampledBit,
            ImageCreateFlags.None,
            &nativeProperties);
        properties = nativeProperties;
        return result == Result.Success;
    }

    private static SampleCounts ToSampleCounts(SampleCountFlags flags)
    {
        SampleCounts result = SampleCounts.None;
        if ((flags & SampleCountFlags.Count1Bit) != 0) result |= SampleCounts.One;
        if ((flags & SampleCountFlags.Count2Bit) != 0) result |= SampleCounts.Two;
        if ((flags & SampleCountFlags.Count4Bit) != 0) result |= SampleCounts.Four;
        if ((flags & SampleCountFlags.Count8Bit) != 0) result |= SampleCounts.Eight;
        if ((flags & SampleCountFlags.Count16Bit) != 0) result |= SampleCounts.Sixteen;
        if ((flags & SampleCountFlags.Count32Bit) != 0) result |= SampleCounts.ThirtyTwo;
        return result;
    }
}
