using SomeEngine.Assets.Importers;
using SomeEngine.Tests;

namespace SomeEngine.Assets.Tests.Assets;

public class ShaderSourcePolicyTests
{
    [Fact]
    public void ShaderSourcesUseSlangExtension()
    {
        string projectRoot = TestProjectPaths.ProjectRoot();
        string shaderDir = TestProjectPaths.ShaderDirectory();
        string[] hlslSources = Directory
            .EnumerateFiles(shaderDir, "*.hlsl", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(projectRoot, path))
            .ToArray();

        Assert.Empty(hlslSources);
    }

    [Theory]
    [InlineData("triangle.slang", "VSMain", "PSMain")]
    [InlineData("hello_triangle.slang", "VSMain", "PSMain")]
    public void MigratedShadersImportAsSlang(string shaderFile, params string[] entryPoints)
    {
        var asset = SlangShaderImporter.Import(TestProjectPaths.ShaderPath(shaderFile));

        foreach (string entryPoint in entryPoints)
        {
            Assert.Contains(asset.Variants!, v =>
                v.EntryPoint == entryPoint
                && v.Data.HasValue
                && v.Data.Value.Length > 0);
        }
    }

    [Fact]
    public void ClusterShaderLayoutsDoNotDeclareAPageTable()
    {
        string[] layoutSources =
        [
            "cluster_draw.slang",
            "cluster_resolve.slang",
            "cluster_shade_pipeline.slang",
            "debug_sphere.slang",
        ];

        foreach (string shaderFile in layoutSources)
        {
            string source = File.ReadAllText(TestProjectPaths.ShaderPath(shaderFile));
            Assert.DoesNotContain("PageTable", source, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Theory]
    [InlineData("cluster_draw.slang")]
    [InlineData("cluster_resolve.slang")]
    [InlineData("cluster_shade_material.slang")]
    [InlineData("cluster_shade_unlit.slang")]
    [InlineData("debug_sphere.slang")]
    public void ClusterEntryModulesImportAfterLayoutChange(string shaderFile)
    {
        var asset = SlangShaderImporter.ImportTransient(TestProjectPaths.ShaderPath(shaderFile));

        Assert.NotNull(asset.Variants);
        Assert.NotEmpty(asset.Variants);
        Assert.NotNull(asset.EntryPointReflections);
        Assert.NotEmpty(asset.EntryPointReflections);
    }

}
