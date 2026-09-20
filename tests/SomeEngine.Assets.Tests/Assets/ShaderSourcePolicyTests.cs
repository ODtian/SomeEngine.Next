using SomeEngine.Assets.Importers;
using SomeEngine.Tests;
using SomeEngine.Assets.Schema;

namespace SomeEngine.Assets.Tests.Assets;

public class ShaderSourcePolicyTests
{


    [Theory]
    [InlineData("triangle.shader", "VSMain", "PSMain")]
    [InlineData("hello_triangle.shader", "VSMain", "PSMain")]
    public async Task DefinitionsCompileOriginalSlangEntryPoints(string shaderFile, params string[] entryPoints)
    {
        string path = TestProjectPaths.ShaderPath(shaderFile);
        using var definition = await ShaderAsset.OpenSourceAsync(path);
        using var asset = SlangShaderImporter.CompileDefinition(definition, path);
        using var shader = await asset.GetBlock<ShaderBlock>().CreateAsync(asset.Name);

        foreach (string entryPoint in entryPoints)
        {
            Assert.Contains(shader.Programs!, program =>
                program is not null && program.EntryPoints.Any(entry =>
                    entry.Name == entryPoint && entry.Code.Length > 0));
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
        Assert.NotEmpty(code.Programs);
        Assert.NotNull(code.EntryPointReflections);
        Assert.NotEmpty(code.EntryPointReflections);
    }

}
