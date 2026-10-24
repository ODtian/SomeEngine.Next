using Microsoft.Extensions.DependencyInjection;

namespace SomeEngine.Assets.Importers;

/// <summary>Creates a project with engine glTF import and Shader source compilation.</summary>
public static class AssetProjects
{
    public static AssetProject Create(string projectRoot,
        IReadOnlyList<string>? shaderIncludeDirectories = null,
        Action<IServiceCollection>? configureServices = null,
        string? capabilitiesVersion = null)
        => new(projectRoot,
            [StandardGltfSceneImporter.CreateSourceImporter(configureServices, capabilitiesVersion)],
            sourceFormats: [new ShaderSourceFormat(shaderIncludeDirectories)]);
}
