using SlangShaderSharp;
using SomeEngine.Assets.Importers;
using SomeEngine.Assets.Schema;
using SomeEngine.Resources;

namespace SomeEngine.Assets.Tests.Assets;

internal static class ShaderTestCompilation
{
    public static IGlobalSession Slang => SlangShaderImporter.GlobalSession;

    public static SessionDesc CreateSessionDescription()
    {
        TargetDesc[] targets = SlangShaderImporter.CreateDefaultTargets(Slang);
        return SlangShaderImporter.CreateSessionDescription(
            Path.Combine(AppContext.BaseDirectory, "shader-test.shader"),
            targets);
    }

    public static ShaderAsset Define(string sourcePath)
    {
        var definition = new ShaderAsset { Name = Path.GetFileNameWithoutExtension(sourcePath) };
        definition.GetBlock<ShaderSourceBlock>().SourceNames = [Path.GetFileName(sourcePath)];
        return definition;
    }

    public static ShaderAsset Compile(string sourcePath)
    {
        using ShaderAsset definition = Define(sourcePath);
        return Compile(definition, sourcePath);
    }

    public static ShaderAsset Compile(ShaderAsset definition, string sourcePath)
        => SlangShaderImporter.CompileDefinition(definition,
            Path.Combine(Path.GetDirectoryName(Path.GetFullPath(sourcePath))!, "test-inputs.shader"),
            includeDirectories: [SomeEngine.Tests.TestProjectPaths.ShaderDirectory()]);

    public static ValueTask<Shader> CreateAsync(
        ShaderAsset asset,
        CancellationToken cancellationToken = default)
    {
        return asset.GetBlock<ShaderBlock>().CreateAsync(
            asset.GetBlock<ShaderSourceBlock>(),
            Slang,
            CreateSessionDescription(),
            asset.Name,
            cancellationToken: cancellationToken);
    }
}
