using SlangShaderSharp;
using SomeEngine.Graphics;
using SomeEngine.Tests;
using Xunit;

namespace SomeEngine.Graphics.Direct3D12.Tests;

[Collection("Direct3D12 shader compilation")]
[Trait("Category", "Hardware")]
public sealed class RayTracingInstanceEncodingTests(D3D12TestCompiler shaderCompiler)
{
    [Fact]
    public void Typed_instances_validate_the_complete_batch_before_writing()
    {
        using IGraphicsBackend backend = D3D12GraphicsBackend.Create();
        using Device device = RayTracingInstanceAssertions.CreateHardwareDevice(backend);
        RayTracingInstanceAssertions.VerifyEncoding(backend, device,
            () => RayTracingInstanceAssertions.CreateHardwareDevice(backend));
    }

    [Fact]
    public void Typed_instances_report_transform_identity_mask_and_facing()
    {
        using IGraphicsBackend backend = D3D12GraphicsBackend.Create();
        using Device device = RayTracingInstanceAssertions.CreateHardwareDevice(backend);
        Assert.True(backend.TryGetCapability(device, out RayTracing? capability) && capability!.InlineRayQuery,
            "The typed instance query contract requires hardware inline ray queries.");
        D3D12TestShader shader = shaderCompiler.Compile("typed_instance_query",
            RayTracingInstanceAssertions.QuerySource, [new("queryMain", SlangStage.Compute)], "sm_6_5");
        using Pipeline pipeline = backend.CreateComputePipeline(device,
            new(shader.SlangCode, shader.Reflection, shader.GetEntryPoint(0), shader.GetEntryCode(0)));
        RayTracingInstanceAssertions.VerifyQueries(backend, device, pipeline,
            shader.Reflection.GetGlobalParamsVarLayout() ?? VariableLayoutReflection.Null);
        GC.KeepAlive(shader);
    }
}
