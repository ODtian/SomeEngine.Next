using SomeEngine.Resources;

namespace SomeEngine.Assets.Tests.Assets;

public sealed class MaterialTemplateTests
{
    [Fact]
    public async Task EngineTemplatesCreateMaterialsWithSharedWhiteAndFlatNormalTextures()
    {
        using var files = new AssetTestDirectory();
        var project = new AssetProject(files.Path, []);
        await MaterialTemplates.SaveAsync(project, default, default);
        await using var loader = new AssetLoader(project.CreateStorage());
        using MaterialAsset standardAsset = await MaterialAsset.OpenAsync(loader, MaterialTemplates.Standard);
        using MaterialAsset unlitAsset = await MaterialAsset.OpenAsync(loader, MaterialTemplates.Unlit);
        Dictionary<AssetId<ShaderAsset>, Shader> shaders = [];
        Dictionary<AssetId<TextureAsset>, Texture> textures = [];
        using MaterialBlock content = await standardAsset.ResolveAsync(loader);
        foreach (AssetId<TextureAsset> id in content.Parameters
            .Where(value => value.Kind == MaterialParameterKind.Texture)
            .Select(value => value.Texture).Distinct())
        {
            using TextureAsset texture = await TextureAsset.OpenAsync(loader, id);
            textures.Add(id, await texture.GetBlock<TextureBlock>().CreateAsync(texture.Name));
        }

        StandardMaterial standard = await standardAsset.CreateAsync<StandardMaterial>(loader, shaders, textures);
        UnlitMaterial unlit = await unlitAsset.CreateAsync<UnlitMaterial>(loader, shaders, textures);

        Assert.Same(standard.AlbedoMap, standard.ARMMap);
        Assert.Same(standard.AlbedoMap, unlit.AlbedoMap);
        Assert.True(standard.AlbedoMap!.TryGetTile(TextureTiles.ChunkKey(0, 0, 0), out var white));
        Assert.Equal(new byte[] { 255, 255, 255, 255 }, white.ToArray());
        Assert.True(standard.NormalMap!.TryGetTile(TextureTiles.ChunkKey(0, 0, 0), out var normal));
        Assert.Equal(new byte[] { 128, 128, 255, 255 }, normal.ToArray());
        Assert.Equal(0.5f, standard.Roughness);
    }
}
