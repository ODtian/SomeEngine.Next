using SomeEngine.Assets.Schema;
using SomeEngine.Resources;

namespace SomeEngine.Assets.Tests.Assets;

public sealed class MaterialReloadTests
{
    [Fact]
    public async Task ReloadRestoresTheParentAndClassFieldDefaults()
    {
        using var directory = new AssetTestDirectory();
        var project = new AssetProject(directory.Path, []);
        using var parent = new MaterialAsset();
        _ = new StandardMaterialParameters(parent.GetBlock<MaterialBlock>()) { Roughness = 0.25f };
        project.RegisterAsset(await parent.SaveAsync(directory.File("parent.material.asset")));
        using var asset = new MaterialAsset();
        asset.GetBlock<MaterialBlock>().Parent = parent.Reference;
        _ = new StandardMaterialParameters(asset.GetBlock<MaterialBlock>()) { Roughness = 0.8f, MetallicFactor = 0.7f };
        await using var loader = new AssetLoader(project.CreateStorage());
        using StandardMaterial material = await asset.CreateAsync<StandardMaterial>(loader);
        long identity = material.MaterialId;
        asset.GetBlock<MaterialBlock>().Reset(nameof(StandardMaterial.Roughness));
        asset.GetBlock<MaterialBlock>().Reset(nameof(StandardMaterial.MetallicFactor));

        await asset.UpdateAsync(material, loader);

        Assert.Equal(identity, material.MaterialId);
        Assert.Equal(0.25f, material.Roughness);
        Assert.Equal(0f, material.MetallicFactor);
    }

    [Fact]
    public async Task ShaderCanBeRequestedAgainAfterItsFileIsRestored()
    {
        using var directory = new AssetTestDirectory();
        var project = new AssetProject(directory.Path, []);
        AssetId<ShaderAsset> id = await SaveShaderAsync(project, directory);
        string path = directory.File("shared.shader.asset");
        byte[] bytes = await File.ReadAllBytesAsync(path);
        File.Delete(path);
        await using var loader = new AssetLoader(project.CreateStorage(), ShaderTestCompilation.Slang,
            ShaderTestCompilation.CreateSessionDescription());

        await Assert.ThrowsAsync<FileNotFoundException>(() => loader.GetShaderAsync(id).AsTask());
        await File.WriteAllBytesAsync(path, bytes);
        Shader shader = await loader.GetShaderAsync(id);

        Assert.Same(shader, await loader.GetShaderAsync(id));
        Assert.True(shader.TryGetSlangTarget(SlangShaderSharp.SlangCompileTarget.Dxil, out _));
    }

    [Fact]
    public async Task ShaderRefreshChangesTheSharedShaderAtTheBusinessUpdate()
    {
        using var directory = new AssetTestDirectory();
        var project = new AssetProject(directory.Path, []);
        AssetId<ShaderAsset> id = await SaveShaderAsync(project, directory);
        await using var loader = new AssetLoader(project.CreateStorage(), ShaderTestCompilation.Slang,
            ShaderTestCompilation.CreateSessionDescription());
        using var asset = new MaterialAsset();
        asset.GetBlock<MaterialBlock>().Shader = id;
        using StandardMaterial material = await asset.CreateAsync<StandardMaterial>(loader);
        Shader original = material.Shader!;
        using ShaderAsset changed = await ShaderAsset.OpenAsync(directory.File("shared.shader.asset"));
        changed.Name = "Changed shader";
        project.RegisterAsset(await changed.SaveAsync(directory.File("shared.shader.asset")));

        Shader replacement = await loader.RefreshShaderAsync(id);

        Assert.Same(original, material.Shader);
        Assert.Same(replacement, await loader.GetShaderAsync(id));
        await asset.UpdateAsync(material, loader);
        Assert.Same(replacement, material.Shader);
        Assert.Equal("Changed shader", material.Shader!.Name);
    }

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
        await file.UpdateAsync(current, loader);
        Assert.Equal("after", current.Name);
        Assert.Equal(revision + 1, current.Revision);
        Assert.Equal(0.75f, current.Roughness);
    }

    [Fact]
    public void PropertyWritesAdvanceVersionForActualChanges()
    {
        var material = new StandardMaterial { Roughness = 0.25f };
        ulong version = material.Revision;
        material.Roughness = 0.25f;
        material.Roughness = 0.75f;
        material.Roughness = 0.75f;
        Assert.Equal(version + 1, material.Revision);
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
