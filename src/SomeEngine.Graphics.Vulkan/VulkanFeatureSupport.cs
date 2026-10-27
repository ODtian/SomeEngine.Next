namespace SomeEngine.Graphics.Vulkan;

internal sealed record VulkanExtendedFeatureSupport(
    bool ConditionalRendering,
    bool InheritedConditionalRendering,
    bool TransformFeedback,
    bool GeometryStreams,
    bool TransformFeedbackQueries,
    bool TransformFeedbackRasterizationStreamSelect,
    uint MaximumTransformFeedbackStreams,
    uint MaximumTransformFeedbackBuffers,
    uint MaximumTransformFeedbackBufferDataSize,
    uint MaximumTransformFeedbackBufferDataStride,
    uint MaximumTransformFeedbackStreamDataSize,
    bool DeviceGeneratedCommands,
    VulkanBufferUsageFlags2Provider BufferUsageFlags2Provider,
    uint MaximumIndirectSequenceCount,
    bool ConservativeRasterization,
    bool VertexAttributeInstanceRateDivisor,
    bool VertexAttributeInstanceRateZeroDivisor,
    uint MaximumVertexAttributeDivisor,
    bool CustomBorderColorWithoutFormat,
    bool NullDescriptor,
    bool ExtendedDynamicState,
    bool ExtendedDynamicState2,
    bool MeshShader,
    bool TaskShader,
    bool PipelineFragmentShadingRate,
    bool PrimitiveFragmentShadingRate,
    bool AttachmentFragmentShadingRate,
    bool AccelerationStructure,
    bool DescriptorBindingAccelerationStructureUpdateAfterBind,
    uint MaximumDescriptorSetUpdateAfterBindAccelerationStructures,
    bool RayTracingPipeline,
    bool RayTracingPipelineTraceRaysIndirect,
    bool RayQuery,
    bool ShaderImageInt64Atomics)
{
    internal const string ConservativeRasterizationExtensionName =
        "VK_EXT_conservative_rasterization";
    internal const string VertexAttributeDivisorExtensionName =
        "VK_EXT_vertex_attribute_divisor";
    internal const string CustomBorderColorExtensionName =
        "VK_EXT_custom_border_color";
    internal const string DeviceGeneratedCommandsExtensionName =
        "VK_EXT_device_generated_commands";
    internal const string Maintenance5ExtensionName = "VK_KHR_maintenance5";
    internal const string ExtendedFlagsExtensionName = "VK_KHR_extended_flags";
    internal const string Robustness2ExtensionName = "VK_EXT_robustness2";
    internal const string ShaderImageAtomicInt64ExtensionName =
        "VK_EXT_shader_image_atomic_int64";

    internal static unsafe VulkanExtendedFeatureSupport Query(
        Vk vk,
        VkPhysicalDevice physicalDevice,
        IReadOnlySet<string> extensions,
        uint apiVersion,
        bool bufferDeviceAddress)
    {
        PhysicalDeviceConditionalRenderingFeaturesEXT conditional = QueryFeature<PhysicalDeviceConditionalRenderingFeaturesEXT>(
            vk, physicalDevice, extensions.Contains("VK_EXT_conditional_rendering"),
            StructureType.PhysicalDeviceConditionalRenderingFeaturesExt);
        PhysicalDeviceTransformFeedbackFeaturesEXT transform = QueryFeature<PhysicalDeviceTransformFeedbackFeaturesEXT>(
            vk, physicalDevice, extensions.Contains("VK_EXT_transform_feedback"),
            StructureType.PhysicalDeviceTransformFeedbackFeaturesExt);
        PhysicalDeviceTransformFeedbackPropertiesEXT transformProperties =
            QueryProperty<PhysicalDeviceTransformFeedbackPropertiesEXT>(
                vk,
                physicalDevice,
                extensions.Contains("VK_EXT_transform_feedback"),
                StructureType.PhysicalDeviceTransformFeedbackPropertiesExt);
        PhysicalDeviceVertexAttributeDivisorFeaturesEXT divisor =
            QueryFeature<PhysicalDeviceVertexAttributeDivisorFeaturesEXT>(
                vk,
                physicalDevice,
                extensions.Contains(VertexAttributeDivisorExtensionName),
                StructureType.PhysicalDeviceVertexAttributeDivisorFeaturesExt);
        PhysicalDeviceVertexAttributeDivisorPropertiesEXT divisorProperties =
            QueryProperty<PhysicalDeviceVertexAttributeDivisorPropertiesEXT>(
                vk,
                physicalDevice,
                extensions.Contains(VertexAttributeDivisorExtensionName),
                StructureType.PhysicalDeviceVertexAttributeDivisorPropertiesExt);
        PhysicalDeviceCustomBorderColorFeaturesEXT customBorder =
            QueryFeature<PhysicalDeviceCustomBorderColorFeaturesEXT>(
                vk,
                physicalDevice,
                extensions.Contains(CustomBorderColorExtensionName),
                StructureType.PhysicalDeviceCustomBorderColorFeaturesExt);
        PhysicalDeviceDeviceGeneratedCommandsFeaturesEXT generatedCommands =
            QueryFeature<PhysicalDeviceDeviceGeneratedCommandsFeaturesEXT>(
                vk,
                physicalDevice,
                extensions.Contains(DeviceGeneratedCommandsExtensionName),
                StructureType.PhysicalDeviceDeviceGeneratedCommandsFeaturesExt);
        bool generatedCommandsAvailable = generatedCommands.DeviceGeneratedCommands &&
            bufferDeviceAddress;
        bool core14 = apiVersion >= Vk.MakeVersion(1, 4, 0);
        PhysicalDeviceMaintenance5FeaturesKHR maintenance5 =
            QueryFeature<PhysicalDeviceMaintenance5FeaturesKHR>(
                vk,
                physicalDevice,
                generatedCommandsAvailable &&
                    (core14 || extensions.Contains(Maintenance5ExtensionName)),
                StructureType.PhysicalDeviceMaintenance5FeaturesKhr);
        VulkanExtendedFlagsFeatures extendedFlags = QueryFeature<VulkanExtendedFlagsFeatures>(
            vk,
            physicalDevice,
            generatedCommandsAvailable && !maintenance5.Maintenance5 &&
                extensions.Contains(ExtendedFlagsExtensionName),
            VulkanExtendedFlagsFeatures.StructureTypeValue);
        VulkanBufferUsageFlags2Provider bufferUsageFlags2Provider =
            maintenance5.Maintenance5
                ? core14
                    ? VulkanBufferUsageFlags2Provider.Core14
                    : VulkanBufferUsageFlags2Provider.Maintenance5
                : extendedFlags.ExtendedFlags
                    ? VulkanBufferUsageFlags2Provider.ExtendedFlags
                    : VulkanBufferUsageFlags2Provider.None;
        PhysicalDeviceDeviceGeneratedCommandsPropertiesEXT generatedProperties =
            QueryProperty<PhysicalDeviceDeviceGeneratedCommandsPropertiesEXT>(
                vk,
                physicalDevice,
                extensions.Contains(DeviceGeneratedCommandsExtensionName),
                StructureType.PhysicalDeviceDeviceGeneratedCommandsPropertiesExt);
        PhysicalDeviceRobustness2FeaturesEXT robustness2 =
            QueryFeature<PhysicalDeviceRobustness2FeaturesEXT>(
                vk,
                physicalDevice,
                extensions.Contains(Robustness2ExtensionName),
                StructureType.PhysicalDeviceRobustness2FeaturesExt);
        PhysicalDeviceExtendedDynamicStateFeaturesEXT dynamic = QueryFeature<PhysicalDeviceExtendedDynamicStateFeaturesEXT>(
            vk, physicalDevice, extensions.Contains("VK_EXT_extended_dynamic_state"),
            StructureType.PhysicalDeviceExtendedDynamicStateFeaturesExt);
        PhysicalDeviceExtendedDynamicState2FeaturesEXT dynamic2 = QueryFeature<PhysicalDeviceExtendedDynamicState2FeaturesEXT>(
            vk, physicalDevice, extensions.Contains("VK_EXT_extended_dynamic_state2"),
            StructureType.PhysicalDeviceExtendedDynamicState2FeaturesExt);
        PhysicalDeviceMeshShaderFeaturesEXT mesh = QueryFeature<PhysicalDeviceMeshShaderFeaturesEXT>(
            vk, physicalDevice, extensions.Contains("VK_EXT_mesh_shader"),
            StructureType.PhysicalDeviceMeshShaderFeaturesExt);
        PhysicalDeviceFragmentShadingRateFeaturesKHR shadingRate = QueryFeature<PhysicalDeviceFragmentShadingRateFeaturesKHR>(
            vk, physicalDevice, extensions.Contains("VK_KHR_fragment_shading_rate"),
            StructureType.PhysicalDeviceFragmentShadingRateFeaturesKhr);
        PhysicalDeviceAccelerationStructureFeaturesKHR acceleration = QueryFeature<PhysicalDeviceAccelerationStructureFeaturesKHR>(
            vk, physicalDevice, extensions.Contains("VK_KHR_acceleration_structure"),
            StructureType.PhysicalDeviceAccelerationStructureFeaturesKhr);
        PhysicalDeviceAccelerationStructurePropertiesKHR accelerationProperties =
            QueryProperty<PhysicalDeviceAccelerationStructurePropertiesKHR>(
                vk,
                physicalDevice,
                extensions.Contains("VK_KHR_acceleration_structure"),
                StructureType.PhysicalDeviceAccelerationStructurePropertiesKhr);
        PhysicalDeviceRayTracingPipelineFeaturesKHR rayTracing = QueryFeature<PhysicalDeviceRayTracingPipelineFeaturesKHR>(
            vk, physicalDevice, extensions.Contains("VK_KHR_ray_tracing_pipeline"),
            StructureType.PhysicalDeviceRayTracingPipelineFeaturesKhr);
        PhysicalDeviceRayQueryFeaturesKHR rayQuery = QueryFeature<PhysicalDeviceRayQueryFeaturesKHR>(
            vk, physicalDevice, extensions.Contains("VK_KHR_ray_query"),
            StructureType.PhysicalDeviceRayQueryFeaturesKhr);
        PhysicalDeviceShaderImageAtomicInt64FeaturesEXT imageInt64 =
            QueryFeature<PhysicalDeviceShaderImageAtomicInt64FeaturesEXT>(
                vk,
                physicalDevice,
                extensions.Contains(ShaderImageAtomicInt64ExtensionName),
                StructureType.PhysicalDeviceShaderImageAtomicInt64FeaturesExt);
        return new VulkanExtendedFeatureSupport(
            conditional.ConditionalRendering,
            conditional.InheritedConditionalRendering,
            transform.TransformFeedback,
            transform.GeometryStreams,
            transformProperties.TransformFeedbackQueries,
            transformProperties.TransformFeedbackRasterizationStreamSelect,
            transformProperties.MaxTransformFeedbackStreams,
            transformProperties.MaxTransformFeedbackBuffers,
            transformProperties.MaxTransformFeedbackBufferDataSize,
            transformProperties.MaxTransformFeedbackBufferDataStride,
            transformProperties.MaxTransformFeedbackStreamDataSize,
            generatedCommandsAvailable &&
                bufferUsageFlags2Provider != VulkanBufferUsageFlags2Provider.None,
            bufferUsageFlags2Provider,
            generatedProperties.MaxIndirectSequenceCount,
            extensions.Contains(ConservativeRasterizationExtensionName),
            divisor.VertexAttributeInstanceRateDivisor,
            divisor.VertexAttributeInstanceRateZeroDivisor,
            divisorProperties.MaxVertexAttribDivisor,
            customBorder.CustomBorderColors &&
                customBorder.CustomBorderColorWithoutFormat,
            robustness2.NullDescriptor,
            dynamic.ExtendedDynamicState,
            dynamic2.ExtendedDynamicState2,
            mesh.MeshShader,
            mesh.TaskShader,
            shadingRate.PipelineFragmentShadingRate,
            shadingRate.PrimitiveFragmentShadingRate,
            shadingRate.AttachmentFragmentShadingRate,
            acceleration.AccelerationStructure,
            acceleration.DescriptorBindingAccelerationStructureUpdateAfterBind,
            accelerationProperties.MaxDescriptorSetUpdateAfterBindAccelerationStructures,
            rayTracing.RayTracingPipeline,
            rayTracing.RayTracingPipelineTraceRaysIndirect,
            rayQuery.RayQuery,
            imageInt64.ShaderImageInt64Atomics);
    }

    private static unsafe T QueryFeature<T>(
        Vk vk,
        VkPhysicalDevice physicalDevice,
        bool available,
        StructureType structureType)
        where T : unmanaged
    {
        T feature = default;
        if (!available)
            return feature;
        *(StructureType*)Unsafe.AsPointer(ref feature) = structureType;
        PhysicalDeviceFeatures2 root = new()
        {
            SType = StructureType.PhysicalDeviceFeatures2,
            PNext = Unsafe.AsPointer(ref feature),
        };
        vk.GetPhysicalDeviceFeatures2(physicalDevice, &root);
        return feature;
    }

    private static unsafe T QueryProperty<T>(
        Vk vk,
        VkPhysicalDevice physicalDevice,
        bool available,
        StructureType structureType)
        where T : unmanaged
    {
        T property = default;
        if (!available)
            return property;
        *(StructureType*)Unsafe.AsPointer(ref property) = structureType;
        PhysicalDeviceProperties2 root = new()
        {
            SType = StructureType.PhysicalDeviceProperties2,
            PNext = Unsafe.AsPointer(ref property),
        };
        vk.GetPhysicalDeviceProperties2(physicalDevice, &root);
        return property;
    }
}

internal enum VulkanBufferUsageFlags2Provider
{
    None,
    Core14,
    Maintenance5,
    ExtendedFlags,
}

// Silk.NET 2.23.0 predates VK_KHR_extended_flags. This matches the Khronos
// VkPhysicalDeviceExtendedFlagsFeaturesKHR declaration; x64 size is 24 bytes.
[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
internal unsafe struct VulkanExtendedFlagsFeatures
{
    internal const StructureType StructureTypeValue = (StructureType)1000668004;

    public StructureType SType;
    public void* PNext;
    public Silk.NET.Core.Bool32 ExtendedFlags;
}
