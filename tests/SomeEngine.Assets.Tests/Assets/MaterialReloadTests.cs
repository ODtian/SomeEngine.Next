using SomeEngine.Assets.Schema;
using SomeEngine.Resources;

namespace SomeEngine.Assets.Tests.Assets;

public sealed class MaterialReloadTests
{
    [Fact]
    public async Task ReloadUsesTheSameExplicitShaderLoaderAsCreation()
    {
        using var directory = new AssetTestDirectory();
        await using var loader = new AssetLoader(new LooseAssetStorage(directory.Path, new AssetManifest()));
        using ShaderAsset shaderFile = await ShaderAsset.OpenAsync(SomeEngine.Tests.TestProjectPaths.BuiltShaderPath("hello_triangle.shader.asset"));
        Shader shader = await ShaderTestCompilation.CreateAsync(shaderFile);
        using MaterialAsset file = new StandardMaterialAsset { Name = "after" };
        Guid id = Guid.NewGuid();
        file.GetBlock<MaterialBlock>().Shader = new(id);
        using var material = new StandardMaterial { Name = "before" };
        await file.UpdateAsync(material, loader, (selected, _) =>
        {
            Assert.Equal(id, selected.Value);
            return ValueTask.FromResult(shader);
        }, null);
        Assert.Same(shader, material.Shader);
        Assert.Equal("after", material.Name);
    }

    [Fact]
    public async Task MaterialsKeepDirectShaderReferencesWithoutTakingTheirLifetime()
    {
        using var directory = new AssetTestDirectory();
        await using var loader = new AssetLoader(new LooseAssetStorage(directory.Path, new AssetManifest()));
        await using ShaderAsset shaderFile = await ShaderAsset.OpenAsync(
            SomeEngine.Tests.TestProjectPaths.BuiltShaderPath("hello_triangle.shader.asset"));
        Shader shader = await ShaderTestCompilation.CreateAsync(shaderFile);
        using MaterialAsset file = new StandardMaterialAsset();
        file.GetBlock<MaterialBlock>().Shader = new(Guid.NewGuid());
        using Material first = await file.CreateAsync(loader, (_, _) => ValueTask.FromResult(shader), null);
        using Material second = await file.CreateAsync(loader, (_, _) => ValueTask.FromResult(shader), null);
        Assert.Same(shader, first.Shader);
        Assert.Same(shader, second.Shader);
        first.Dispose();
        ulong revision = second.Revision;
        Shader replacement = await ShaderTestCompilation.CreateAsync(shaderFile);
        second.Shader = replacement;
        Assert.Same(replacement, second.Shader);
        Assert.Equal(revision + 1, second.Revision);
        second.Dispose();
        Assert.True(shader.TryGetSlangTarget(SlangShaderSharp.SlangCompileTarget.Dxil, out _));
    }

    [Fact]
    public async Task ReloadChangesTheExistingObjectAtTheExplicitUpdateBoundary()
    {
        using var directory = new AssetTestDirectory();
        await using var loader = new AssetLoader(new LooseAssetStorage(directory.Path, new AssetManifest()));
        using var current = new StandardMaterial { Name = "before" };
        using MaterialAsset file = new StandardMaterialAsset { Name = "after" };
        _ = new StandardMaterialParameters(file.GetBlock<MaterialBlock>()) { Roughness = 0.75f };
        ulong revision = current.Revision;
        int notifications = 0;
        current.Changed += material =>
        {
            Assert.Same(current, material);
            Assert.Equal("after", material.Name);
            Assert.Equal(0.75f, current.Roughness);
            notifications++;
        };
        await file.UpdateAsync(current, loader);
        Assert.Equal("after", current.Name);
        Assert.Equal(revision + 1, current.Revision);
        Assert.Equal(1, notifications);
    }

    [Fact]
    public void SlotWritesNotifyAfterTheValueChangesAndIgnoreEqualValues()
    {
        var material = new StandardMaterial { Roughness = 0.25f };
        int notifications = 0;
        material.Changed += changed =>
        {
            Assert.Same(material, changed);
            Assert.Equal(0.75f, material.Roughness);
            notifications++;
        };
        material.Roughness = 0.25f;
        material.Roughness = 0.75f;
        material.Roughness = 0.75f;
        Assert.Equal(1, notifications);
    }
}
