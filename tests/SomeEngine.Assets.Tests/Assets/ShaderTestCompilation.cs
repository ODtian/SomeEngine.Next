using SomeEngine.Assets.Importers;
using SomeEngine.Assets.Schema;

namespace SomeEngine.Assets.Tests.Assets;

internal static class ShaderTestCompilation
{
    public static ShaderAsset Define(string sourcePath)
    {
        var definition = new ShaderAsset { Name = Path.GetFileNameWithoutExtension(sourcePath) };
        definition.GetBlock<ShaderSourceBlock>().SourceFiles = [Path.GetFileName(sourcePath)];
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
}
