using SlangShaderSharp;

namespace SomeEngine.Assets.Importers;

public static class SlangShaderCookProfiles
{
    public const string DefaultName = "default";
    public const string D3D12ShaderModel62Name = "d3d12-sm6.2";
    public const string D3D12ShaderModel66Name = "d3d12-sm6.6-bindless";

    public static TargetDesc[] CreateTargets(IGlobalSession slang, string name)
    {
        ArgumentNullException.ThrowIfNull(slang);
        string dxilProfile = DxilProfileName(name);
        return
        [
            new TargetDesc
            {
                Format = SlangCompileTarget.Dxil,
                Profile = slang.FindProfile(dxilProfile),
            },
            new TargetDesc
            {
                Format = SlangCompileTarget.Spirv,
                Profile = slang.FindProfile("glsl_460"),
            },
        ];
    }

    public static string BuildSetting(string name)
        => $"slang-cook-profile:{name}|dxil:{DxilProfileName(name)}|spirv:glsl_460";

    private static string DxilProfileName(string name)
        => name switch
        {
            DefaultName => "sm_6_5",
            D3D12ShaderModel62Name => "sm_6_2",
            D3D12ShaderModel66Name => "sm_6_6",
            _ => throw new ArgumentException(
                $"Unknown Slang shader cook profile '{name}'. Supported profiles are "
                    + $"'{DefaultName}', '{D3D12ShaderModel62Name}', and '{D3D12ShaderModel66Name}'.",
                nameof(name)),
        };
}
