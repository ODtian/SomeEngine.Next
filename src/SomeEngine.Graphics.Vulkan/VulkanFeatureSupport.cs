namespace SomeEngine.Graphics.Vulkan;

internal sealed record VulkanExtendedFeatureSupport(
    bool ConditionalRendering,
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
    bool RayQuery)
{
    internal const string ConservativeRasterizationExtensionName =
        "VK_EXT_conservative_rasterization";
    internal const string VertexAttributeDivisorExtensionName =
        "VK_EXT_vertex_attribute_divisor";
    internal const string CustomBorderColorExtensionName =
        "VK_EXT_custom_border_color";
    internal const string DeviceGeneratedCommandsExtensionName =
        "VK_EXT_device_generated_commands";
    internal const string Robustness2ExtensionName = "VK_EXT_robustness2";

    internal static unsafe VulkanExtendedFeatureSupport Query(
        Vk vk,
        VkPhysicalDevice physicalDevice,
        IReadOnlySet<string> extensions)
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
        return new VulkanExtendedFeatureSupport(
            conditional.ConditionalRendering,
            transform.TransformFeedback,
            transform.GeometryStreams,
            transformProperties.TransformFeedbackQueries,
            transformProperties.TransformFeedbackRasterizationStreamSelect,
            transformProperties.MaxTransformFeedbackStreams,
            transformProperties.MaxTransformFeedbackBuffers,
            transformProperties.MaxTransformFeedbackBufferDataSize,
            transformProperties.MaxTransformFeedbackBufferDataStride,
            transformProperties.MaxTransformFeedbackStreamDataSize,
            generatedCommands.DeviceGeneratedCommands,
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
            rayQuery.RayQuery);
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
