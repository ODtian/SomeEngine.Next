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
        TargetDesc[] targets = SlangShaderImporter.CreateTargets(Slang, [SlangShaderSharp.SlangCompileTarget.Dxil, SlangShaderSharp.SlangCompileTarget.Spirv]);
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
            Path.Combine(Path.GetDirectoryName(Path.GetFullPath(sourcePath))!, "test-inputs.shader"), [SlangShaderSharp.SlangCompileTarget.Dxil, SlangShaderSharp.SlangCompileTarget.Spirv],
            includeDirectories: [SomeEngine.Tests.TestProjectPaths.ShaderDirectory()]);

    public static AssetId<ShaderAsset> StandardMaterial => new(EngineShaderContent.GetShaderId(
        SomeEngine.Tests.TestProjectPaths.ProjectRoot(), "standard_material.shader"));

    public static AssetLoader OpenEngineFiles(IAssetStorage? application = null)
    {
        string root = SomeEngine.Tests.TestProjectPaths.ProjectRoot();
        var engine = new LooseAssetStorage(root, AssetManifest.Load(Path.Combine(root, "Library", "AssetManifest")));
        return new AssetLoader(application is null ? [engine] : [application, engine]);
    }

    public static async Task SetEntryAsync(ShaderLibrary shaders, ShaderInputBlock inputs, string name, string source)
    {
        Shader shader = await shaders.CreateAsync(name, name + ".slang", source);
        int index = shader.SlangSourceUnitCount - 1;
        inputs.SourceNames = [shader.GetSlangSourceUnitName(index) + ".slang"];
        Assert.True(shader.GetSlangSourceUnit(index).Serialize(out ISlangBlob serialized).Succeeded);
        inputs.SetSerializedSource(0, serialized.Buffer);
    }

    public static async ValueTask<Shader> CreateAsync(
        ShaderAsset asset,
        CancellationToken cancellationToken = default)
    {
        await using AssetLoader files = OpenEngineFiles();
        using var shaders = new ShaderLibrary(Slang, CreateSessionDescription());
        return await shaders.CreateAsync(asset, files, cancellationToken: cancellationToken);
    }
}
