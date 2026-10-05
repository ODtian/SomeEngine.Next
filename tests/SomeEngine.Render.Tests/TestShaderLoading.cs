using SlangShaderSharp;
using SomeEngine.Assets.Schema;
using SomeEngine.Resources;

namespace SomeEngine.Render.Tests;

internal static class TestShaderLoading
{
    internal static SessionDesc CreateSessionDescription(
        IGlobalSession slang,
        SlangCompileTarget target = SlangCompileTarget.Dxil)
    {
        return new SessionDesc
        {
            Targets =
            [
                new TargetDesc
                {
                    Format = target,
                    Profile = slang.FindProfile(
                        target == SlangCompileTarget.Dxil ? "sm_6_6" : "glsl_460"),
                },
            ],
            DefaultMatrixLayoutMode = SlangMatrixLayoutMode.RowMajor,
            CompilerOptionEntries =
            [
                new(CompilerOptionName.NoMangle, CompilerOptionValue.FromInt(1, 0)),
                new(CompilerOptionName.VulkanEmitReflection, CompilerOptionValue.FromInt(1, 0)),
                new(CompilerOptionName.DebugInformation, CompilerOptionValue.FromInt(0, 0)),
                new(CompilerOptionName.ExperimentalFeature, CompilerOptionValue.FromInt(1, 0)),
            ],
        };
    }

    internal static ValueTask<Shader> CreateAsync(
        ShaderAsset asset,
        SlangCompileTarget target = SlangCompileTarget.Dxil,
        CancellationToken cancellationToken = default)
    {
        IGlobalSession slang = CreateGlobalSession();
        return asset.GetBlock<ShaderBlock>().CreateAsync(
            asset.GetBlock<ShaderSourceBlock>(),
            slang,
            CreateSessionDescription(slang, target),
            asset.Name,
            cancellationToken: cancellationToken);
    }

    private static IGlobalSession CreateGlobalSession()
    {
        SlangResult result = SlangShaderSharp.Slang.CreateGlobalSession(
            SlangShaderSharp.Slang.ApiVersion,
            out IGlobalSession slang);
        if (result.Failed || slang is null)
            throw new InvalidOperationException($"Slang global-session creation failed with {result}.");
        return slang;
    }
}
