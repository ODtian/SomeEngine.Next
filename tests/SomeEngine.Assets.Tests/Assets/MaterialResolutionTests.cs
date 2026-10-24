using System.Numerics;
using SomeEngine.Assets.Schema;
using SomeEngine.Resources;

namespace SomeEngine.Assets.Tests.Assets;

public sealed class MaterialResolutionTests
{
    [Fact]
    public async Task ResolvedContentCombinesParentReferencesAndOverridesIndependently()
    {
        using var directory = new AssetTestDirectory();
        var project = new AssetProject(directory.Path, []);
        AssetId<ShaderAsset> shader = new(Guid.NewGuid());
        AssetId<TextureAsset> albedo = new(Guid.NewGuid());
        AssetId<TextureAsset> normal = new(Guid.NewGuid());
        using var parent = new MaterialAsset { Name = "Parent" };
        parent.GetBlock<MaterialBlock>().Shader = shader;
        _ = new StandardMaterialParameters(parent.GetBlock<MaterialBlock>())
        {
            AlbedoMap = albedo, Roughness = 0.25f, BaseColorTint = new Vector4(0.2f, 0.4f, 0.6f, 1f),
        };
        project.RegisterAsset(await parent.SaveAsync(directory.File("parent.material.asset")));
        using var child = new MaterialAsset { Name = "Child" };
        child.GetBlock<MaterialBlock>().Parent = parent.Reference;
        _ = new StandardMaterialParameters(child.GetBlock<MaterialBlock>())
        {
            AlbedoMap = default, NormalMap = normal, Roughness = 0.8f,
        };
        await using var files = new AssetLoader(project.CreateStorage());

        using MaterialBlock content = await child.ResolveAsync(files);

        var values = new StandardMaterialParameters(content);
        Assert.Equal("Child", content.Name);
        Assert.Equal(parent.Reference, content.Parent);
        Assert.Equal(shader, content.Shader);
        Assert.Equal(default, values.AlbedoMap);
        Assert.Equal(normal, values.NormalMap);
        Assert.Equal(0.8f, values.Roughness);
        Assert.Equal(new Vector4(0.2f, 0.4f, 0.6f, 1f), values.BaseColorTint);
        values.Roughness = 0.4f;
        Assert.Equal(0.8f, new StandardMaterialParameters(child.GetBlock<MaterialBlock>()).Roughness);
        using MaterialBlock again = await child.ResolveAsync(files);
        Assert.Equal(0.8f, new StandardMaterialParameters(again).Roughness);
    }
}
