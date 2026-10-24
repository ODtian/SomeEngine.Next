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
        await using var files = new AssetLoader(project.CreateStorage());
        Dictionary<AssetId<ShaderAsset>, Shader> shaders = [];
        Dictionary<AssetId<TextureAsset>, Texture> textures = [];
        StandardMaterial material = await asset.CreateAsync<StandardMaterial>(files, shaders, textures);
        long identity = material.MaterialId;
        ulong revision = material.Revision;
        asset.GetBlock<MaterialBlock>().Reset(nameof(StandardMaterial.Roughness));
        asset.GetBlock<MaterialBlock>().Reset(nameof(StandardMaterial.MetallicFactor));

        await asset.UpdateAsync(material, files, shaders, textures);

        Assert.Equal(identity, material.MaterialId);
        Assert.Equal(revision + 1, material.Revision);
        Assert.Equal(0.25f, material.Roughness);
        Assert.Equal(0f, material.MetallicFactor);
    }

    [Fact]
    public async Task ShaderCanBeCreatedAfterItsFileIsRestored()
    {
        using var directory = new AssetTestDirectory();
        var project = new AssetProject(directory.Path, []);
        await SaveShaderAsync(project, directory);
        string path = directory.File("shared.shader.asset");
        byte[] bytes = await File.ReadAllBytesAsync(path);
        File.Delete(path);

        await Assert.ThrowsAsync<FileNotFoundException>(() => ShaderAsset.OpenAsync(path).AsTask());
        await File.WriteAllBytesAsync(path, bytes);
        using ShaderAsset asset = await ShaderAsset.OpenAsync(path);
        Shader shader = await asset.CreateAsync(ShaderTestCompilation.Slang, ShaderTestCompilation.CreateSessionDescription());

        Assert.True(shader.TryGetSlangTarget(SlangShaderSharp.SlangCompileTarget.Dxil, out _));
    }

    [Fact]
    public async Task ShaderReplacementChangesTheMaterialAtTheBusinessUpdate()
    {
        using var directory = new AssetTestDirectory();
        var project = new AssetProject(directory.Path, []);
        AssetId<ShaderAsset> id = await SaveShaderAsync(project, directory);
        await using var files = new AssetLoader(project.CreateStorage());
        using ShaderAsset source = await ShaderAsset.OpenAsync(files, id);
        Shader original = await source.CreateAsync(ShaderTestCompilation.Slang, ShaderTestCompilation.CreateSessionDescription());
        Dictionary<AssetId<ShaderAsset>, Shader> shaders = new() { [id] = original };
        Dictionary<AssetId<TextureAsset>, Texture> textures = [];
        using var asset = new MaterialAsset();
        asset.GetBlock<MaterialBlock>().Shader = id;
        StandardMaterial material = await asset.CreateAsync<StandardMaterial>(files, shaders, textures);
        source.Name = "Changed shader";

        Shader replacement = await source.CreateAsync(ShaderTestCompilation.Slang, ShaderTestCompilation.CreateSessionDescription());
        shaders[id] = replacement;

        Assert.Same(original, material.Shader);
        await asset.UpdateAsync(material, files, shaders, textures);
        Assert.Same(replacement, material.Shader);
        Assert.Equal("Changed shader", material.Shader!.Name);
    }

    [Fact]
    public async Task ReloadUsesTheProvidedShaderReference()
    {
        using var directory = new AssetTestDirectory();
        var project = new AssetProject(directory.Path, []);
        AssetId<ShaderAsset> id = await SaveShaderAsync(project, directory);
        await using var files = new AssetLoader(project.CreateStorage());
        using ShaderAsset source = await ShaderAsset.OpenAsync(files, id);
        Shader shader = await source.CreateAsync(ShaderTestCompilation.Slang, ShaderTestCompilation.CreateSessionDescription());
        Dictionary<AssetId<ShaderAsset>, Shader> shaders = new() { [id] = shader };
        using MaterialAsset asset = new StandardMaterialAsset { Name = "after" };
        asset.GetBlock<MaterialBlock>().Shader = id;
        var material = new StandardMaterial { Name = "before" };

        await asset.UpdateAsync(material, files, shaders, new Dictionary<AssetId<TextureAsset>, Texture>());

        Assert.Same(shader, material.Shader);
        Assert.Equal("after", material.Name);
    }

    [Fact]
    public async Task BusinessMaterialsShareTheProvidedShaderAndHaveIndividualIdentities()
    {
        using var directory = new AssetTestDirectory();
        var project = new AssetProject(directory.Path, []);
        AssetId<ShaderAsset> id = await SaveShaderAsync(project, directory);
        await using var files = new AssetLoader(project.CreateStorage());
        using ShaderAsset source = await ShaderAsset.OpenAsync(files, id);
        Shader shader = await source.CreateAsync(ShaderTestCompilation.Slang, ShaderTestCompilation.CreateSessionDescription());
        Dictionary<AssetId<ShaderAsset>, Shader> shaders = new() { [id] = shader };
        Dictionary<AssetId<TextureAsset>, Texture> textures = [];
        using MaterialAsset asset = new StandardMaterialAsset();
        asset.GetBlock<MaterialBlock>().Shader = id;

        StandardMaterial first = await asset.CreateAsync<StandardMaterial>(files, shaders, textures);
        StandardMaterial second = await asset.CreateAsync<StandardMaterial>(files, shaders, textures);

        Assert.NotEqual(first.MaterialId, second.MaterialId);
        Assert.Same(shader, first.Shader);
        Assert.Same(shader, second.Shader);
        second.Roughness = 0.7f;
        Assert.Equal(0.5f, first.Roughness);
        Assert.Equal(0.7f, second.Roughness);
        Assert.True(shader.TryGetSlangTarget(SlangShaderSharp.SlangCompileTarget.Dxil, out _));
    }

    [Fact]
    public async Task ReloadChangesTheExistingObjectAtTheExplicitUpdateBoundary()
    {
        using var directory = new AssetTestDirectory();
        await using var files = new AssetLoader(new LooseAssetStorage(directory.Path, new AssetManifest()));
        var current = new StandardMaterial { Name = "before" };
        using MaterialAsset asset = new StandardMaterialAsset { Name = "after" };
        _ = new StandardMaterialParameters(asset.GetBlock<MaterialBlock>()) { Roughness = 0.75f };
        ulong revision = current.Revision;

        await asset.UpdateAsync(current, files, new Dictionary<AssetId<ShaderAsset>, Shader>(),
            new Dictionary<AssetId<TextureAsset>, Texture>());

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
