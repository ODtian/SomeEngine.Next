using SlangShaderSharp;
using SomeEngine.Assets.Schema;
using SomeEngine.Resources;

namespace SomeEngine.Render.Cluster.Tests;

internal static class TestShaderLoading
{
    private static readonly Lazy<IGlobalSession> s_slang = new(CreateGlobalSession);

    internal static IGlobalSession Slang => s_slang.Value;

    internal static SessionDesc CreateSessionDescription(
        SlangCompileTarget target = SlangCompileTarget.Dxil)
    {
        IGlobalSession slang = Slang;
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
        CancellationToken cancellationToken = default) =>
        asset.CreateAsync(Slang, CreateSessionDescription(target),
            cancellationToken: cancellationToken);

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
