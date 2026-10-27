using System.Numerics;
using System.Runtime.InteropServices;
using SlangShaderSharp;
using SomeEngine.Assets;
using SomeEngine.Assets.Schema;
using SomeEngine.Render.Components;
using SomeEngine.Render.Shadows;
using SomeEngine.Resources;
using SomeEngine.Tests;

namespace SomeEngine.Assets.Tests.Assets;

public sealed class VirtualShadowReceiverUniformTests
{
    [Theory]
    [InlineData(SlangCompileTarget.Dxil)]
    [InlineData(SlangCompileTarget.Spirv)]
    public async Task Demand_uniform_preserves_receiver_camera_in_the_shader_format(SlangCompileTarget target)
    {
        Shader shader = await ReadShader("vsm_mark_pages.shader.asset");
        TypeLayoutReflection layout = ConstantBufferContents(shader, target, "PageDemand");
        var expectedCamera = new Vector3(17.25f, -23.5f, 41.75f);
        const float expectedCotan = 1.375f;
        var uniform = new VirtualShadowPageDemandUniforms
        {
            ReceiverProjection = new VirtualShadowReceiverProjection
            {
                DepthOfField = new CameraDepthOfField(3, 5, 7, 11, 13, 17, 19),
                CameraPosition = expectedCamera,
                CotanHalfFieldOfView = expectedCotan,
            },
        };
        byte[] upload = new byte[Marshal.SizeOf<VirtualShadowPageDemandUniforms>()];
        MemoryMarshal.Write(upload, in uniform);

        VariableLayoutReflection projection = Field(layout, "ReceiverProjection");
        VariableLayoutReflection camera = Field(projection.TypeLayout, "CameraPosition");
        VariableLayoutReflection cotan = Field(projection.TypeLayout, "CotanHalfFieldOfView");
        Vector3 observedCamera = MemoryMarshal.Read<Vector3>(upload.AsSpan(
            checked((int)(projection.GetOffset() + camera.GetOffset()))));
        float observedCotan = MemoryMarshal.Read<float>(upload.AsSpan(
            checked((int)(projection.GetOffset() + cotan.GetOffset()))));
        Assert.Equal(expectedCamera, observedCamera);
        Assert.Equal(expectedCotan, observedCotan);
        GC.KeepAlive(shader);
    }

    [Theory]
    [InlineData(SlangCompileTarget.Dxil)]
    [InlineData(SlangCompileTarget.Spirv)]
    public async Task Sampling_uniform_preserves_receiver_camera_in_the_shader_format(SlangCompileTarget target)
    {
        Shader shader = await ReadShader("standard.shader.asset");
        TypeLayoutReflection layout = ConstantBufferContents(shader, target, "VirtualShadowSampling");
        var expectedCamera = new Vector3(-31.5f, 43.25f, 59.75f);
        const float expectedCotan = 2.625f;
        var uniform = new VirtualShadowSamplingUniforms
        {
            ReceiverProjection = new VirtualShadowReceiverProjection
            {
                DepthOfField = new CameraDepthOfField(23, 29, 31, 37, 41, 43, 47),
                CameraPosition = expectedCamera,
                CotanHalfFieldOfView = expectedCotan,
            },
        };
        byte[] upload = new byte[Marshal.SizeOf<VirtualShadowSamplingUniforms>()];
        MemoryMarshal.Write(upload, in uniform);

        VariableLayoutReflection projection = Field(layout, "ReceiverProjection");
        VariableLayoutReflection camera = Field(projection.TypeLayout, "CameraPosition");
        VariableLayoutReflection cotan = Field(projection.TypeLayout, "CotanHalfFieldOfView");
        Vector3 observedCamera = MemoryMarshal.Read<Vector3>(upload.AsSpan(
            checked((int)(projection.GetOffset() + camera.GetOffset()))));
        float observedCotan = MemoryMarshal.Read<float>(upload.AsSpan(
            checked((int)(projection.GetOffset() + cotan.GetOffset()))));
        Assert.Equal(expectedCamera, observedCamera);
        Assert.Equal(expectedCotan, observedCotan);
        GC.KeepAlive(shader);
    }

    private static async ValueTask<Shader> ReadShader(string assetName)
    {
        await using ShaderAsset file = await ShaderAsset.OpenAsync(Path.Combine(
            TestProjectPaths.ProjectRoot(), "samples", "DefaultGame", "assets", "Shaders", assetName));
        return await ShaderTestCompilation.CreateAsync(file);
    }

    private static TypeLayoutReflection ConstantBufferContents(Shader shader, SlangCompileTarget target, string resource)
    {
        int targetIndex = shader.RequireSlangTarget(target);
        VariableLayoutReflection globals = shader.GetSlangReflection(targetIndex).GetGlobalParamsVarLayout()
            ?? VariableLayoutReflection.Null;
        if (globals == VariableLayoutReflection.Null)
            throw new InvalidDataException("The shader has no reflected global parameters.");
        TypeLayoutReflection layout = globals.TypeLayout;
        if (layout.Kind is SlangTypeKind.ConstantBuffer or SlangTypeKind.ParameterBlock)
            layout = layout.ElementTypeLayout;
        return Field(layout, resource).TypeLayout.ElementTypeLayout;
    }

    private static VariableLayoutReflection Field(TypeLayoutReflection layout, string name)
    {
        for (uint index = 0; index < layout.FieldCount; index++)
        {
            VariableLayoutReflection field = layout.GetFieldByIndex(index);
            if (field.Name == name) return field;
        }
        throw new InvalidDataException($"The shader format has no {name} field.");
    }
}
