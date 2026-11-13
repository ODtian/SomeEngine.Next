namespace SomeEngine.Graphics.Vulkan;

internal sealed partial class VulkanBackend : IBackendShaderTarget
{
    SlangShaderSharp.SlangCompileTarget IBackendShaderTarget.ShaderTarget => SlangShaderSharp.SlangCompileTarget.Spirv;

    private sealed partial class VulkanDevice : IPipelineFeatures
    {
        internal object PipelineCacheMergeGate { get; } = new();
        bool IPipelineFeatures.NullDescriptors => ExtendedFeatures.NullDescriptor;
        bool IPipelineFeatures.ConservativeRasterization => ExtendedFeatures.ConservativeRasterization;
        bool IPipelineFeatures.VertexInstanceDivisorZero => ExtendedFeatures.VertexAttributeInstanceRateZeroDivisor;
        uint IPipelineFeatures.MaximumVertexInstanceDivisor => ExtendedFeatures.VertexAttributeInstanceRateDivisor
            ? ExtendedFeatures.MaximumVertexAttributeDivisor : 1;
        bool IPipelineFeatures.StreamOutput => ExtendedFeatures.TransformFeedback;
        uint IPipelineFeatures.StreamOutputBuffers => ExtendedFeatures.MaximumTransformFeedbackBuffers;
        uint IPipelineFeatures.StreamOutputBufferBytes => ExtendedFeatures.MaximumTransformFeedbackBufferDataSize;
        uint IPipelineFeatures.StreamOutputStride => ExtendedFeatures.MaximumTransformFeedbackBufferDataStride;
        uint IPipelineFeatures.StreamOutputBytes => ExtendedFeatures.MaximumTransformFeedbackStreamDataSize;
        bool IPipelineFeatures.CountedDispatch => ExtendedFeatures.DeviceGeneratedCommands;
    }
}
