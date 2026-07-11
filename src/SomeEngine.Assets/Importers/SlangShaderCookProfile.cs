using System.Text.Json;

namespace SomeEngine.Assets.Importers;

public sealed class SlangShaderImporterSettings
{
    public string CookProfile { get; set; } = SlangShaderCookProfiles.DefaultName;

    public static SlangShaderImporterSettings Default() => new();

    internal static SlangShaderImporterSettings Load(SourceMeta sourceMeta, string sourcePath)
    {
        if (!sourceMeta.ImporterSettings.HasValue)
        {
            return Default();
        }

        SlangShaderImporterSettings? settings =
            sourceMeta.ImporterSettings.Value.Deserialize<SlangShaderImporterSettings>(
                AssetIoHelpers.JsonOptions);
        if (settings == null)
        {
            throw new InvalidOperationException(
                $"Source '{sourcePath}' contains invalid importer settings for {nameof(SlangShaderImporter)}.");
        }

        return settings;
    }
}

public readonly record struct SlangShaderCookProfile
{
    internal SlangShaderCookProfile(string name, string dxilProfile, string spirvProfile)
    {
        Name = name;
        DxilProfile = dxilProfile;
        SpirvProfile = spirvProfile;
    }

    public string Name { get; }
    public string DxilProfile { get; }
    public string SpirvProfile { get; }

    internal string FingerprintPart
        => $"slang-cook-profile:{Name}|dxil:{DxilProfile}|spirv:{SpirvProfile}";
}

public static class SlangShaderCookProfiles
{
    public const string DefaultName = "default";
    public const string D3D12ShaderModel62Name = "d3d12-sm6.2";

    public static SlangShaderCookProfile Default { get; } =
        new(DefaultName, "sm_6_5", "glsl_460");

    public static SlangShaderCookProfile D3D12ShaderModel62 { get; } =
        new(D3D12ShaderModel62Name, "sm_6_2", "glsl_460");

    public static SlangShaderCookProfile Resolve(string name)
        => name switch
        {
            DefaultName => Default,
            D3D12ShaderModel62Name => D3D12ShaderModel62,
            _ => throw new ArgumentException(
                $"Unknown Slang shader cook profile '{name}'. Supported profiles are "
                    + $"'{DefaultName}' and '{D3D12ShaderModel62Name}'.",
                nameof(name)),
        };
}
