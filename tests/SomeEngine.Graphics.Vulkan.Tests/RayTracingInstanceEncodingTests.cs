using SlangShaderSharp;
using SomeEngine.Graphics;
using SomeEngine.Tests;
using Xunit;

namespace SomeEngine.Graphics.Vulkan.Tests;

[Collection("Vulkan shader compilation")]
[Trait("Category", "Hardware")]
public sealed class RayTracingInstanceEncodingTests(VulkanTestCompiler shaderCompiler)
{
    [Fact]
    public void Typed_instances_validate_the_complete_batch_before_writing()
    {
        using IGraphicsBackend backend = VulkanGraphicsBackend.Create();
        using Device device = RayTracingInstanceAssertions.CreateHardwareDevice(backend);
        RayTracingInstanceAssertions.VerifyEncoding(backend, device,
            () => RayTracingInstanceAssertions.CreateHardwareDevice(backend));
    }

    [Fact]
    public void Typed_instances_report_transform_identity_mask_and_facing()
    {
        using IGraphicsBackend backend = VulkanGraphicsBackend.Create();
        using Device device = RayTracingInstanceAssertions.CreateHardwareDevice(backend);
        Assert.True(backend.TryGetCapability(device, out RayTracing? capability) && capability!.InlineRayQuery,
            "The typed instance query contract requires hardware inline ray queries.");
        VulkanTestShader shader = shaderCompiler.Compile(
            RayTracingInstanceAssertions.QuerySource, ("queryMain", SlangStage.Compute));
        using Pipeline pipeline = backend.CreateComputePipeline(device,
            new(shader.SlangCode, shader.Reflection, shader.Entries[0], shader.GetEntryCode(0)));
        RayTracingInstanceAssertions.VerifyQueries(backend, device, pipeline,
            shader.Reflection.GetGlobalParamsVarLayout() ?? VariableLayoutReflection.Null);
        GC.KeepAlive(shader);
    }
}
