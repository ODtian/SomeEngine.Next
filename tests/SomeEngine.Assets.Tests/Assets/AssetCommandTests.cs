using SomeEngine.Assets.Importers;
using SomeEngine.Assets.Schema;

namespace SomeEngine.Assets.Tests.Assets;

public sealed class AssetCommandTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "someengine-asset-command-" + Guid.NewGuid().ToString("N"));

    public AssetCommandTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public async Task BuildCommandCreatesShaderAndReusesItsVersion()
    {
        File.WriteAllText(Path.Combine(_root, "Surface.slang"), """
            [shader("pixel")] float4 Pixel() : SV_Target { return float4(0, 1, 0, 1); }
            """);
        using var definition = new ShaderAsset { Name = "surface" };
        definition.GetBlock<ShaderSourceBlock>().SourceNames = ["Surface.slang"];
        await definition.SaveSourceAsync(Path.Combine(_root, "Surface.shader"));
        AssetProject project = AssetProjects.Create(_root);
        using var output = new StringWriter();

        Assert.Equal(0, await AssetCommands.RunAsync(project, ["build", "Surface.shader"], output));
        AssetEntry built = Assert.Single(project.List());
        await using ShaderAsset shader = await ShaderAsset.OpenAsync(Path.Combine(_root, built.Path));
        Assert.Equal("surface", shader.Name);
        Assert.Equal(definition.Id + Environment.NewLine, output.ToString());

        Assert.Equal(0, await AssetCommands.RunAsync(project, ["build", "Surface.shader"], output));
        Assert.Equal(built.Version, Assert.Single(project.List()).Version);
    }

    [Fact]
    public async Task ImportCommandCreatesWorldAndReusesItsVersion()
    {
        File.WriteAllText(Path.Combine(_root, "Town.gltf"), """
            { "asset": { "version": "2.0" }, "scene": 0, "scenes": [{ "name": "Town", "extras": { "someengine_id": "town" } }] }
            """);
        AssetProject project = AssetProjects.Create(_root);
        using var output = new StringWriter();

        Assert.Equal(0, await AssetCommands.RunAsync(project, ["import", "Town.gltf"], output));
        AssetEntry imported = Assert.Single(project.List());
        await using WorldAsset world = await WorldAsset.OpenAsync(Path.Combine(_root, imported.Path));
        Assert.Equal("Town", world.Name);
        Assert.Equal(imported.AssetGuid + Environment.NewLine, output.ToString());

        Assert.Equal(0, await AssetCommands.RunAsync(project, ["import", "Town.gltf"], output));
        Assert.Equal(imported.Version, Assert.Single(project.List()).Version);
    }

    [Fact]
    public async Task ListCommandFiltersAssetsByType()
    {
        AssetProject project = AssetProjects.Create(_root);
        using var shader = new ShaderAsset { Name = "Surface" };
        project.RegisterAsset(await shader.SaveAsync(Path.Combine(_root, "Surface.shader.asset")));
        using var material = new StandardMaterialAsset { Name = "Surface material" };
        project.RegisterAsset(await material.SaveAsync(Path.Combine(_root, "Surface.material.asset")));
        using var output = new StringWriter();

        Assert.Equal(0, await AssetCommands.RunAsync(project, ["list", ShaderAsset.FileType], output));
        Assert.Equal($"{shader.Id}\t{ShaderAsset.FileType}\tSurface.shader.asset{Environment.NewLine}", output.ToString());
    }
}
