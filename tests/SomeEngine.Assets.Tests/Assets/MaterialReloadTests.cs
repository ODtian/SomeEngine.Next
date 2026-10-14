using SomeEngine.Assets.Schema;
using SomeEngine.Resources;

namespace SomeEngine.Assets.Tests.Assets;

public sealed class MaterialReloadTests
{
    [Fact]
    public async Task ReloadUsesTheSharedShaderFromTheLoader()
    {
        using var directory = new AssetTestDirectory();
        var project = new AssetProject(directory.Path, []);
        AssetId<ShaderAsset> id = await SaveShaderAsync(project, directory);
        await using var loader = new AssetLoader(project.CreateStorage(), ShaderTestCompilation.Slang,
            ShaderTestCompilation.CreateSessionDescription());
        Shader shader = await loader.GetShaderAsync(id);
        using MaterialAsset file = new StandardMaterialAsset { Name = "after" };
        file.GetBlock<MaterialBlock>().Shader = id;
        using var material = new StandardMaterial { Name = "before" };
        await file.UpdateAsync(material, loader);
        Assert.Same(shader, material.Shader);
        Assert.Equal("after", material.Name);
    }

    [Fact]
    public async Task BusinessMaterialsShareTheLoaderShaderAndKeepTheirInstanceIdentity()
    {
        using var directory = new AssetTestDirectory();
        var project = new AssetProject(directory.Path, []);
        AssetId<ShaderAsset> id = await SaveShaderAsync(project, directory);
        await using var loader = new AssetLoader(project.CreateStorage(), ShaderTestCompilation.Slang,
            ShaderTestCompilation.CreateSessionDescription());
        Shader shader = await loader.GetShaderAsync(id);
        using MaterialAsset file = new StandardMaterialAsset();
        file.GetBlock<MaterialBlock>().Shader = id;
        using StandardMaterial first = await file.CreateAsync<StandardMaterial>(loader);
        using StandardMaterial second = await file.CreateAsync<StandardMaterial>(loader);
        Assert.Same(shader, first.Shader);
        Assert.Same(shader, second.Shader);
        first.Dispose();
        long identity = second.MaterialId;
        second.Roughness = 0.7f;
        Assert.Equal(identity, second.MaterialId);
        Assert.Equal(0.7f, second.Roughness);
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

    private static async ValueTask<AssetId<ShaderAsset>> SaveShaderAsync(
        AssetProject project, AssetTestDirectory directory)
    {
        string path = directory.File("shared.slang");
        await File.WriteAllTextAsync(path, """
            RWStructuredBuffer<uint> Output;
            [shader("compute")]
            [numthreads(1, 1, 1)]
            void Shade(uint3 thread : SV_DispatchThreadID) { Output[thread.x] = 3; }
            """);
        using ShaderAsset asset = ShaderTestCompilation.Compile(path);
        return new(project.RegisterAsset(await asset.SaveAsync(directory.File("shared.shader.asset"))));
    }
}
