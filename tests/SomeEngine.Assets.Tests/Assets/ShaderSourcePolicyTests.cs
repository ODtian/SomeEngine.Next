using SomeEngine.Assets.Importers;
using SomeEngine.Tests;
using SomeEngine.Assets.Schema;
using SomeEngine.Resources;
using SlangShaderSharp;

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
        using ShaderSourceBlock? material = shaderFile == "standard.shader" ? StandardMaterialInputs.Create()
            : shaderFile == "unlit.shader" ? UnlitMaterialInputs.Create() : null;
        using ClusterMaterialEntriesBlock? entries = shaderFile == "standard.shader" ? ClusterMaterialEntriesBlock.Create()
            : shaderFile == "unlit.shader" ? ClusterMaterialEntriesBlock.CreateUnlit() : null;
        if (material is not null)
        {
            definition.SetBlock(material);
            definition.SetBlock(entries!);
            definition.SelectInputs<ShaderSourceBlock>();
            definition.SelectInputs<ClusterMaterialEntriesBlock>();
        }
        using var asset = SlangShaderImporter.CompileDefinition(definition, path,
            includeDirectories: [TestProjectPaths.ShaderDirectory()]);

        ShaderBlock code = asset.GetBlock<ShaderBlock>();
        Assert.False((await code.ReadBytesAsync(ShaderBlock.EntryCodeKey(ShaderTestCompilation.CreateSessionDescription(), 0, 0))).IsEmpty);
        Assert.False((await asset.GetBlock<ShaderSourceBlock>().ReadBytesAsync(ShaderSourceBlock.SerializedSourceKey(0))).IsEmpty);
        Shader shader = await ShaderTestCompilation.CreateAsync(asset);
        Assert.True(shader.SlangEntryCount > 0);
    }

}
