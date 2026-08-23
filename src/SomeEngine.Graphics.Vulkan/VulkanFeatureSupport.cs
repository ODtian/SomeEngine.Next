namespace SomeEngine.Graphics.Vulkan;

internal sealed record VulkanExtendedFeatureSupport(
    bool ConditionalRendering,
    bool TransformFeedback,
    bool ExtendedDynamicState,
    bool ExtendedDynamicState2,
    bool MeshShader,
    bool TaskShader,
    bool PipelineFragmentShadingRate,
    bool PrimitiveFragmentShadingRate,
    bool AttachmentFragmentShadingRate,
    bool AccelerationStructure,
    bool RayTracingPipeline,
    bool RayTracingPipelineTraceRaysIndirect,
    bool RayQuery)
{
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
        PhysicalDeviceRayTracingPipelineFeaturesKHR rayTracing = QueryFeature<PhysicalDeviceRayTracingPipelineFeaturesKHR>(
            vk, physicalDevice, extensions.Contains("VK_KHR_ray_tracing_pipeline"),
            StructureType.PhysicalDeviceRayTracingPipelineFeaturesKhr);
        PhysicalDeviceRayQueryFeaturesKHR rayQuery = QueryFeature<PhysicalDeviceRayQueryFeaturesKHR>(
            vk, physicalDevice, extensions.Contains("VK_KHR_ray_query"),
            StructureType.PhysicalDeviceRayQueryFeaturesKhr);
        return new VulkanExtendedFeatureSupport(
            conditional.ConditionalRendering,
            transform.TransformFeedback,
            dynamic.ExtendedDynamicState,
            dynamic2.ExtendedDynamicState2,
            mesh.MeshShader,
            mesh.TaskShader,
            shadingRate.PipelineFragmentShadingRate,
            shadingRate.PrimitiveFragmentShadingRate,
            shadingRate.AttachmentFragmentShadingRate,
            acceleration.AccelerationStructure,
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
}
