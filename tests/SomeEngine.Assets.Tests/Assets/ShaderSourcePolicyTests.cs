using SomeEngine.Assets.Importers;
using SomeEngine.Tests;
using SomeEngine.Assets.Schema;
using SomeEngine.Resources;
using SlangShaderSharp;

namespace SomeEngine.Assets.Tests.Assets;

public class ShaderSourcePolicyTests
{
    [Theory]
    [InlineData("templates/DefaultProject", "standard", "StandardTransparencyMaterial")]
    [InlineData("games/SomeGame", "unlit", "UnlitTransparencyMaterial")]
    public async Task ProjectMaterialSourcesSaveForwardDebugAndTransparencyCapabilities(
        string projectDirectory, string name, string transparencyMaterial)
    {
        using var files = new AssetTestDirectory();
        string sourceDirectory = Path.Combine(TestProjectPaths.ProjectRoot(), projectDirectory, "assets", "Shaders");
        File.Copy(Path.Combine(sourceDirectory, name + ".shader"), files.File(name + ".shader"));
        File.Copy(Path.Combine(sourceDirectory, name + ".slang"), files.File(name + ".slang"));
        var project = new AssetProject(files.Path, [], shaderIncludeDirectories: [TestProjectPaths.ShaderDirectory()]);

        Guid id = await project.BuildAsync(name + ".shader");

        await using var loader = new AssetLoader(project.CreateStorage());
        using ShaderAsset asset = await ShaderAsset.OpenAsync(loader, new(id));
        Shader shader = await asset.CreateSavedAsync(ShaderTestCompilation.Slang,
            ShaderTestCompilation.CreateSessionDescription());
        int target = shader.RequireSlangTarget(SlangCompileTarget.Spirv);
        _ = shader.GetSlangAttributedTypeLayout(target, "TransparencyMaterial", LayoutRules.DefaultStructuredBuffer);
        Assert.NotNull(shader.GetSlangReflection(target).FindTypeByName(transparencyMaterial));
        foreach (string mode in new[] { "0", "8" })
        {
            foreach ((string entry, string[] arguments) in new (string, string[])[]
            {
                ("ClusterShadeMode", [mode]),
                ("ClusterShadowRequests", ["0", mode]),
                ("ClusterForwardShade", ["0", mode]),
            })
            {
                Shader variant = shader.RequireSpecialization(entry, arguments);
                Assert.True(variant.TryGetSlangEntry(target, entry, SlangStage.Compute, out EntryPointReflection code));
                Assert.False(variant.GetSlangEntryBytes(target, code).IsEmpty);
            }
        }
    }

    [Theory]
    [InlineData("triangle.shader", "VSMain", "PSMain")]
    [InlineData("hello_triangle.shader", "VSMain", "PSMain")]
    public async Task DefinitionsCompileOriginalSlangEntryPoints(string shaderFile, params string[] entryPoints)
    {
        string path = TestProjectPaths.ShaderPath(shaderFile);
        using var definition = await ShaderAsset.OpenSourceAsync(path);
        using var asset = SlangShaderImporter.CompileDefinition(definition, path);
        Shader shader = await ShaderTestCompilation.CreateAsync(asset);

        foreach (string entryPoint in entryPoints)
        {
            int targetIndex = shader.RequireSlangTarget(SlangCompileTarget.Spirv);
            Assert.True(shader.TryGetSlangEntry(
                targetIndex,
                entryPoint,
                out EntryPointReflection entry));
            Assert.True(
                shader.GetSlangEntryBlob(targetIndex, entry) is not null ||
                !shader.GetSlangEntryBytes(targetIndex, entry).IsEmpty);
        }
    }

    [Theory]
    [InlineData("cluster_draw.shader")]
    [InlineData("cluster_resolve.shader")]
    [InlineData("standard.shader")]
    [InlineData("unlit.shader")]
    [InlineData("cluster_tessellation_phong.shader")]
    [InlineData("debug_sphere.shader")]
    public async Task ClusterDefinitionsCompileAfterLayoutChange(string shaderFile)
    {
        string path = shaderFile is "standard.shader" or "unlit.shader"
            ? Path.Combine(TestProjectPaths.ProjectRoot(), "templates", "DefaultProject", "assets", "Shaders", shaderFile)
            : TestProjectPaths.ShaderPath(shaderFile);
        using var definition = await ShaderAsset.OpenSourceAsync(path);
        using var asset = SlangShaderImporter.CompileDefinition(definition, path,
            includeDirectories: [TestProjectPaths.ShaderDirectory()]);

        ShaderBlock code = asset.GetBlock<ShaderBlock>();
        Assert.False((await code.ReadBytesAsync(ShaderBlock.EntryCodeKey(ShaderTestCompilation.CreateSessionDescription(), 0, 0))).IsEmpty);
        Assert.False((await asset.GetBlock<ShaderSourceBlock>().ReadBytesAsync(ShaderSourceBlock.SerializedSourceKey(0))).IsEmpty);
        Shader shader = await ShaderTestCompilation.CreateAsync(asset);
        Assert.True(shader.SlangEntryCount > 0);
    }

}
